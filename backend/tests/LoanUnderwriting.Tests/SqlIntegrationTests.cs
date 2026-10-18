using LoanUnderwriting.Core;
using LoanUnderwriting.Infrastructure;
using LoanUnderwriting.Infrastructure.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace LoanUnderwriting.Tests;

/// <summary>Runs only when LOAN_TEST_SQL holds a connection string to SQL Server 2025 (server level, no database), for example
/// "Server=.\MSSQLSERVER01;Integrated Security=True;TrustServerCertificate=True". Each run uses a throwaway database.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOAN_TEST_SQL")))
            Skip = "Set LOAN_TEST_SQL to a SQL Server 2025 connection string to run SQL integration tests.";
    }
}

public sealed class SqlFixture : IAsyncLifetime
{
    private readonly string _db = "LoanTest_" + Guid.NewGuid().ToString("N")[..10];
    public SqlDb Db = default!;
    public string Conn = "";
    public const int Dim = 8;

    public async Task InitializeAsync()
    {
        var baseConn = Environment.GetEnvironmentVariable("LOAN_TEST_SQL");
        if (string.IsNullOrWhiteSpace(baseConn)) return;
        Conn = new SqlConnectionStringBuilder(baseConn) { InitialCatalog = _db }.ConnectionString;
        var opts = new DatabaseOptions { ConnectionString = Conn, VectorProvider = "Native" };
        await new SchemaInitializer(opts, Dim, NullLogger<SchemaInitializer>.Instance).EnsureAsync();
        Db = new SqlDb(opts);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrEmpty(Conn)) return;
        SqlConnection.ClearAllPools();
        var b = new SqlConnectionStringBuilder(Conn) { InitialCatalog = "master" };
        await using var c = new SqlConnection(b.ConnectionString);
        await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{_db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_db}]";
        await cmd.ExecuteNonQueryAsync();
    }
}

public class SqlIntegrationTests(SqlFixture f) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task Case_state_checkpoints_audit_and_qa_log_round_trip()
    {
        var repo = new SqlCaseRepository(f.Db);
        var id = Guid.NewGuid();
        var s = new LoanState { CaseId = id, EntityName = "Round Trip", FileName = "a.pdf", SubmittedBy = "Ethan Cole", SubmittedAt = DateTimeOffset.UtcNow, Document = new("abc123", "a.pdf") };
        await repo.CreateAsync(s);
        var scored = s with { Scorecard = ScorecardService.Compute(Samples.Apex), Status = CaseStatus.AwaitingReview, Flags = new[] { new ComplianceFlag("F1", Severity.Medium, "r", "e") } };
        await repo.SaveAsync(scored, "score");

        var loaded = (await repo.LoadAsync(id))!;
        Assert.Equal(CaseStatus.AwaitingReview, loaded.Status);
        Assert.Equal(scored.Scorecard!.RiskScore, loaded.Scorecard!.RiskScore);
        Assert.Equal(Severity.Medium, loaded.Flags.Single().Severity);

        await repo.AppendAuditAsync(id, new AuditEvent(DateTimeOffset.UtcNow, "system", "Scorecard", "done", "stub", "DM-1.0.0", null, 10, 5));
        var audit = await repo.GetAuditAsync(id);
        Assert.Equal("DM-1.0.0", audit.Single().DecisionModelVersion);

        await repo.AddQaLogAsync(new QaLogEntry(Guid.NewGuid(), id, "Ethan Cole", DateTimeOffset.UtcNow, "What is net revenue?", false, RefusalReason.None, "answer",
            new[] { new Citation("c1", 1, "text") }, new[] { "c1" }));
        Assert.Single(await repo.GetQaLogAsync(id));

        Assert.Contains(await repo.ListAsync(), c => c.CaseId == id && c.RiskScore == scored.Scorecard.RiskScore);
        Assert.Contains(id, await repo.ListByStatusAsync(CaseStatus.AwaitingReview));
        Assert.Equal("DM-1.0.0", (await repo.GetActivePolicyAsync()).Version);
    }

    [SqlFact]
    public async Task Run_lock_prevents_a_second_runner()
    {
        var repo = new SqlCaseRepository(f.Db);
        var id = Guid.NewGuid();
        await repo.CreateAsync(new LoanState { CaseId = id, SubmittedAt = DateTimeOffset.UtcNow, Document = new("d", "d.pdf"), EntityName = "x", FileName = "d.pdf", SubmittedBy = "x" });
        Assert.True(await repo.TryLockAsync(id, TimeSpan.FromMinutes(10)));
        Assert.False(await repo.TryLockAsync(id, TimeSpan.FromMinutes(10)));
        await repo.UnlockAsync(id);
        Assert.True(await repo.TryLockAsync(id, TimeSpan.FromMinutes(10)));
    }

    [SqlFact]
    public async Task Native_vector_search_is_ordered_by_cosine_distance_and_filtered_by_case()
    {
        var store = new NativeVectorStore(f.Db, SqlFixture.Dim);
        var caseA = Guid.NewGuid();
        var caseB = Guid.NewGuid();
        float[] V(params float[] x) => x.Concat(Enumerable.Repeat(0f, SqlFixture.Dim - x.Length)).ToArray();
        await store.ReplaceAsync(caseA, new[]
        {
            new ChunkEmbedding("a1", 1, 0, "revenue line", "h1", V(1, 0, 0)),
            new ChunkEmbedding("a2", 1, 1, "debt line", "h2", V(0, 1, 0)),
            new ChunkEmbedding("a3", 2, 2, "interest line", "h3", V(0.9f, 0.1f, 0)),
        });
        await store.ReplaceAsync(caseB, new[] { new ChunkEmbedding("b1", 1, 0, "other case", "h4", V(1, 0, 0)) });

        var hits = await store.SearchAsync(caseA, V(1, 0, 0), 3);
        Assert.Equal(new[] { "a1", "a3", "a2" }, hits.Select(h => h.ChunkId));
        Assert.True(hits[0].Distance < 1e-6);
        Assert.DoesNotContain(hits, h => h.ChunkId == "b1");
        Assert.Equal(3, await store.CountAsync(caseA));

        await store.ReplaceAsync(caseA, new[] { new ChunkEmbedding("a9", 1, 0, "replaced", "h9", V(0, 0, 1)) }); // idempotent re-ingest
        Assert.Equal(1, await store.CountAsync(caseA));
        await store.DeleteCaseAsync(caseA);
        Assert.Equal(0, await store.CountAsync(caseA));
        Assert.Equal(1, await store.CountAsync(caseB));
    }
}
