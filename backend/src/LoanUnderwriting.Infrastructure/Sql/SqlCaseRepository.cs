using System.Text.Json;
using LoanUnderwriting.Core;
using Microsoft.Data.SqlClient;

namespace LoanUnderwriting.Infrastructure.Sql;

public sealed class SqlCaseRepository(SqlDb db) : ICaseRepository
{
    private static string Ser<T>(T v) => JsonSerializer.Serialize(v, JsonUtil.Options);
    private static T? De<T>(string s) => JsonSerializer.Deserialize<T>(s, JsonUtil.Options);

    private static void Bind(SqlCommand c, LoanState s)
    {
        c.Parameters.AddWithValue("@id", s.CaseId);
        c.Parameters.AddWithValue("@entity", s.EntityName);
        c.Parameters.AddWithValue("@file", s.FileName);
        c.Parameters.AddWithValue("@doc", s.Document.Id);
        c.Parameters.AddWithValue("@by", s.SubmittedBy);
        c.Parameters.AddWithValue("@at", s.SubmittedAt.UtcDateTime);
        c.Parameters.AddWithValue("@status", s.Status.ToString());
        c.Parameters.AddWithValue("@risk", s.Scorecard is null ? DBNull.Value : s.Scorecard.RiskScore);
        c.Parameters.AddWithValue("@final", s.Final is null ? DBNull.Value : s.Final.ToString());
        c.Parameters.AddWithValue("@json", Ser(s));
    }

    public async Task CreateAsync(LoanState s, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = @"INSERT INTO cases (case_id, entity_name, file_name, document_id, submitted_by, submitted_at, status, risk_score, final_decision, state_json)
                          VALUES (@id,@entity,@file,@doc,@by,@at,@status,@risk,@final,@json)";
        Bind(c, s);
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<LoanState?> LoadAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "SELECT state_json FROM cases WHERE case_id=@id";
        c.Parameters.AddWithValue("@id", caseId);
        var json = await c.ExecuteScalarAsync(ct) as string;
        return json is null ? null : De<LoanState>(json);
    }

    public async Task SaveAsync(LoanState s, string step, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"UPDATE cases SET entity_name=@entity, status=@status, risk_score=@risk, final_decision=@final, state_json=@json WHERE case_id=@id";
            c.Parameters.AddWithValue("@id", s.CaseId);
            c.Parameters.AddWithValue("@entity", s.EntityName);
            c.Parameters.AddWithValue("@status", s.Status.ToString());
            c.Parameters.AddWithValue("@risk", s.Scorecard is null ? DBNull.Value : s.Scorecard.RiskScore);
            c.Parameters.AddWithValue("@final", s.Final is null ? DBNull.Value : s.Final.ToString());
            c.Parameters.AddWithValue("@json", Ser(s));
            await c.ExecuteNonQueryAsync(ct);
        }
        await using (var c = conn.CreateCommand())
        {
            c.Transaction = tx;
            c.CommandText = @"INSERT INTO workflow_checkpoint (case_id, seq, step, state_json, created_at)
                              SELECT @id, ISNULL(MAX(seq),0)+1, @step, @json, SYSUTCDATETIME() FROM workflow_checkpoint WHERE case_id=@id";
            c.Parameters.AddWithValue("@id", s.CaseId);
            c.Parameters.AddWithValue("@step", step);
            c.Parameters.AddWithValue("@json", Ser(s));
            await c.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<CaseSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "SELECT case_id, entity_name, file_name, submitted_by, submitted_at, status, risk_score, final_decision FROM cases ORDER BY submitted_at DESC";
        var list = new List<CaseSummary>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new CaseSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
                new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc)),
                Enum.Parse<CaseStatus>(r.GetString(5)), r.IsDBNull(6) ? null : r.GetDecimal(6),
                r.IsDBNull(7) ? null : Enum.Parse<Outcome>(r.GetString(7))));
        return list;
    }

    public async Task<IReadOnlyList<Guid>> ListByStatusAsync(CaseStatus status, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "SELECT case_id FROM cases WHERE status=@s";
        c.Parameters.AddWithValue("@s", status.ToString());
        var list = new List<Guid>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetGuid(0));
        return list;
    }

    public async Task AppendAuditAsync(Guid caseId, AuditEvent e, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = @"INSERT INTO audit_event (case_id, at, actor, step, model, decision_model_version, input_hash, summary, tokens, latency_ms)
                          VALUES (@id,@at,@actor,@step,@model,@dmv,@hash,@sum,@tok,@lat)";
        c.Parameters.AddWithValue("@id", caseId);
        c.Parameters.AddWithValue("@at", e.At.UtcDateTime);
        c.Parameters.AddWithValue("@actor", e.Actor);
        c.Parameters.AddWithValue("@step", e.Step);
        c.Parameters.AddWithValue("@model", (object?)e.Model ?? DBNull.Value);
        c.Parameters.AddWithValue("@dmv", (object?)e.DecisionModelVersion ?? DBNull.Value);
        c.Parameters.AddWithValue("@hash", (object?)e.InputHash ?? DBNull.Value);
        c.Parameters.AddWithValue("@sum", e.Summary);
        c.Parameters.AddWithValue("@tok", (object?)e.Tokens ?? DBNull.Value);
        c.Parameters.AddWithValue("@lat", (object?)e.LatencyMs ?? DBNull.Value);
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<AuditEvent>> GetAuditAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "SELECT at, actor, step, summary, model, decision_model_version, input_hash, tokens, latency_ms FROM audit_event WHERE case_id=@id ORDER BY event_id";
        c.Parameters.AddWithValue("@id", caseId);
        var list = new List<AuditEvent>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new AuditEvent(new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc)), r.GetString(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetInt32(7), r.IsDBNull(8) ? null : r.GetInt32(8)));
        return list;
    }

    public async Task AddQaLogAsync(QaLogEntry e, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = @"INSERT INTO qa_log (qa_id, case_id, asked_by, asked_at, question, refused, refusal_reason, answer, citations_json, chunk_ids_json)
                          VALUES (@id,@case,@by,@at,@q,@ref,@reason,@ans,@cit,@chunks)";
        c.Parameters.AddWithValue("@id", e.QaId);
        c.Parameters.AddWithValue("@case", e.CaseId);
        c.Parameters.AddWithValue("@by", e.AskedBy);
        c.Parameters.AddWithValue("@at", e.AskedAt.UtcDateTime);
        c.Parameters.AddWithValue("@q", e.Question.Length > 1000 ? e.Question[..1000] : e.Question);
        c.Parameters.AddWithValue("@ref", e.Refused);
        c.Parameters.AddWithValue("@reason", e.Reason == RefusalReason.None ? DBNull.Value : e.Reason.ToString());
        c.Parameters.AddWithValue("@ans", (object?)e.Answer ?? DBNull.Value);
        c.Parameters.AddWithValue("@cit", Ser(e.Citations));
        c.Parameters.AddWithValue("@chunks", Ser(e.ChunkIds));
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<QaLogEntry>> GetQaLogAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "SELECT qa_id, asked_by, asked_at, question, refused, refusal_reason, answer, citations_json, chunk_ids_json FROM qa_log WHERE case_id=@id ORDER BY asked_at";
        c.Parameters.AddWithValue("@id", caseId);
        var list = new List<QaLogEntry>();
        await using var r = await c.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new QaLogEntry(r.GetGuid(0), caseId, r.GetString(1), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)), r.GetString(3),
                r.GetBoolean(4), r.IsDBNull(5) ? RefusalReason.None : Enum.Parse<RefusalReason>(r.GetString(5)), r.IsDBNull(6) ? null : r.GetString(6),
                De<List<Citation>>(r.IsDBNull(7) ? "[]" : r.GetString(7)) ?? new(), De<List<string>>(r.IsDBNull(8) ? "[]" : r.GetString(8)) ?? new()));
        return list;
    }

    public async Task<PolicyParameters> GetActivePolicyAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "SELECT TOP 1 version, approve_below, deny_above, min_confidence FROM policy_version WHERE is_active=1 ORDER BY approved_at DESC";
        await using var r = await c.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new PolicyParameters(r.GetString(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3)) : PolicyParameters.Default;
    }

    public async Task<bool> TryLockAsync(Guid caseId, TimeSpan staleAfter, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "UPDATE cases SET run_lock=SYSUTCDATETIME() WHERE case_id=@id AND (run_lock IS NULL OR run_lock < DATEADD(SECOND, -@s, SYSUTCDATETIME()))";
        c.Parameters.AddWithValue("@id", caseId);
        c.Parameters.AddWithValue("@s", (int)staleAfter.TotalSeconds);
        return await c.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task UnlockAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = "UPDATE cases SET run_lock=NULL WHERE case_id=@id";
        c.Parameters.AddWithValue("@id", caseId);
        await c.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteCaseAsync(Guid caseId, CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct);
        await using var c = conn.CreateCommand();
        c.CommandText = @"DELETE FROM qa_log WHERE case_id=@id;
                          DELETE FROM workflow_checkpoint WHERE case_id=@id;
                          DELETE FROM cases WHERE case_id=@id;";
        c.Parameters.AddWithValue("@id", caseId);
        await c.ExecuteNonQueryAsync(ct);
    }
}
