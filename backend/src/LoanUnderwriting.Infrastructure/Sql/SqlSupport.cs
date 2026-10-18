using LoanUnderwriting.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace LoanUnderwriting.Infrastructure.Sql;

public sealed class SqlDb(DatabaseOptions options)
{
    public string ConnectionString => options.ConnectionString;

    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var c = new SqlConnection(options.ConnectionString);
        await c.OpenAsync(ct);
        return c;
    }
}

/// <summary>Creates the database (if allowed) and the tables. Idempotent, safe to run at every start.</summary>
public sealed class SchemaInitializer(DatabaseOptions options, int embeddingDimensions, VectorRuntime vector, ILogger<SchemaInitializer> log)
{
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(options.ConnectionString);
        var dbName = builder.InitialCatalog;
        if (!string.IsNullOrWhiteSpace(dbName))
        {
            builder.InitialCatalog = "master";
            await using var master = new SqlConnection(builder.ConnectionString);
            await master.OpenAsync(ct);
            await using var create = master.CreateCommand();
            create.CommandText = "IF DB_ID(@db) IS NULL EXEC('CREATE DATABASE [' + @db + ']')";
            create.Parameters.AddWithValue("@db", dbName);
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var conn = new SqlConnection(options.ConnectionString);
        await conn.OpenAsync(ct);
        await DecideVectorModeAsync(conn, ct);
        var embeddingType = vector.UseNative ? $"VECTOR({embeddingDimensions})" : "NVARCHAR(MAX)";
        foreach (var statement in Script(embeddingType))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        log.LogInformation("Database schema ready (vectors: {Mode}, {Dim} dimensions).", vector.UseNative ? "native VECTOR" : "JSON fallback", embeddingDimensions);
    }

    /// <summary>Probes the server for the VECTOR type before any table is created, so the failure is clear and nothing is half-created.</summary>
    private async Task DecideVectorModeAsync(SqlConnection conn, CancellationToken ct)
    {
        var provider = options.VectorProvider;
        if (provider.Equals("JsonFallback", StringComparison.OrdinalIgnoreCase))
        {
            vector.UseNative = false;
            vector.Reason = "Database:VectorProvider is JsonFallback.";
            log.LogWarning("Vector search uses the JSON fallback (development only): embeddings are stored as JSON and compared in the application.");
            return;
        }

        var supported = true;
        var version = "";
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CAST('[0.1,0.2]' AS VECTOR(2))";
            await cmd.ExecuteScalarAsync(ct);
        }
        catch (SqlException)
        {
            supported = false;
            await using var v = conn.CreateCommand();
            v.CommandText = "SELECT CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(50)) + ' ' + CAST(SERVERPROPERTY('Edition') AS NVARCHAR(100))";
            version = (await v.ExecuteScalarAsync(ct))?.ToString() ?? "unknown";
        }

        if (supported) { vector.UseNative = true; vector.Reason = "Server supports the VECTOR type."; return; }

        if (provider.Equals("Auto", StringComparison.OrdinalIgnoreCase))
        {
            vector.UseNative = false;
            vector.Reason = $"SQL Server {version} has no VECTOR type; using the JSON fallback.";
            log.LogWarning("SQL Server {Version} does not support the VECTOR type. Falling back to JSON embeddings (development only). Use SQL Server 2025 or Azure SQL for production.", version);
            return;
        }

        throw new InvalidOperationException(
            $"The SQL Server at this connection ({version}) does not support the VECTOR data type, which needs SQL Server 2025 or Azure SQL. " +
            "Fix: point Database:ConnectionString at a SQL Server 2025 instance, or for development only set Database:VectorProvider to \"Auto\" or \"JsonFallback\".");
    }

    private static IEnumerable<string> Script(string embeddingType)
    {
        yield return @"IF OBJECT_ID('dbo.cases') IS NULL CREATE TABLE dbo.cases (
            case_id UNIQUEIDENTIFIER PRIMARY KEY, entity_name NVARCHAR(200), file_name NVARCHAR(260), document_id VARCHAR(64),
            submitted_by NVARCHAR(200), submitted_at DATETIME2, status VARCHAR(20), risk_score DECIMAL(5,1) NULL,
            final_decision VARCHAR(10) NULL, state_json NVARCHAR(MAX), run_lock DATETIME2 NULL)";
        yield return @"IF OBJECT_ID('dbo.workflow_checkpoint') IS NULL CREATE TABLE dbo.workflow_checkpoint (
            case_id UNIQUEIDENTIFIER, seq INT, step VARCHAR(40), state_json NVARCHAR(MAX), created_at DATETIME2,
            PRIMARY KEY (case_id, seq))";
        yield return @"IF OBJECT_ID('dbo.audit_event') IS NULL CREATE TABLE dbo.audit_event (
            event_id BIGINT IDENTITY PRIMARY KEY, case_id UNIQUEIDENTIFIER, at DATETIME2, actor NVARCHAR(200), step NVARCHAR(80),
            model NVARCHAR(100) NULL, decision_model_version VARCHAR(20) NULL, input_hash CHAR(64) NULL, summary NVARCHAR(MAX),
            tokens INT NULL, latency_ms INT NULL)";
        yield return "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_audit_case') CREATE INDEX ix_audit_case ON dbo.audit_event(case_id)";
        yield return $@"IF OBJECT_ID('dbo.document_chunks') IS NULL CREATE TABLE dbo.document_chunks (
            chunk_id VARCHAR(64) PRIMARY KEY, case_id UNIQUEIDENTIFIER NOT NULL, page INT, chunk_index INT,
            text NVARCHAR(MAX), content_hash CHAR(64), embedding {embeddingType} NOT NULL)";
        yield return "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name='ix_chunks_case') CREATE INDEX ix_chunks_case ON dbo.document_chunks(case_id)";
        yield return @"IF OBJECT_ID('dbo.qa_log') IS NULL CREATE TABLE dbo.qa_log (
            qa_id UNIQUEIDENTIFIER PRIMARY KEY, case_id UNIQUEIDENTIFIER, asked_by NVARCHAR(200), asked_at DATETIME2,
            question NVARCHAR(1000), refused BIT, refusal_reason VARCHAR(20) NULL, answer NVARCHAR(MAX) NULL,
            citations_json NVARCHAR(MAX) NULL, chunk_ids_json NVARCHAR(MAX) NULL)";
        yield return @"IF OBJECT_ID('dbo.policy_version') IS NULL CREATE TABLE dbo.policy_version (
            version VARCHAR(20) PRIMARY KEY, is_active BIT, approve_below DECIMAL(5,1), deny_above DECIMAL(5,1),
            min_confidence DECIMAL(3,2), approved_by NVARCHAR(200), approved_at DATETIME2)";
        yield return @"IF NOT EXISTS (SELECT 1 FROM dbo.policy_version) INSERT INTO dbo.policy_version
            VALUES ('DM-1.0.0', 1, 30.0, 70.0, 0.80, 'seed', SYSUTCDATETIME())";
    }
}
