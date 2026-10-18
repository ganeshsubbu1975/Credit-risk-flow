using LoanUnderwriting.Core;

namespace LoanUnderwriting.Tests;

public sealed class InMemoryRepo : ICaseRepository
{
    public readonly Dictionary<Guid, LoanState> States = new();
    public readonly Dictionary<Guid, List<AuditEvent>> Audit = new();
    public readonly List<QaLogEntry> QaLog = new();
    public readonly List<string> Checkpoints = new();
    private readonly HashSet<Guid> _locks = new();

    public Task CreateAsync(LoanState s, CancellationToken ct = default) { States[s.CaseId] = s; return Task.CompletedTask; }
    public Task<LoanState?> LoadAsync(Guid id, CancellationToken ct = default) => Task.FromResult(States.GetValueOrDefault(id));
    public Task SaveAsync(LoanState s, string step, CancellationToken ct = default) { States[s.CaseId] = s; Checkpoints.Add(step); return Task.CompletedTask; }
    public Task<IReadOnlyList<CaseSummary>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CaseSummary>>(
        States.Values.Select(s => new CaseSummary(s.CaseId, s.EntityName, s.FileName, s.SubmittedBy, s.SubmittedAt, s.Status, s.Scorecard?.RiskScore, s.Final)).ToList());
    public Task<IReadOnlyList<Guid>> ListByStatusAsync(CaseStatus st, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Guid>>(States.Values.Where(s => s.Status == st).Select(s => s.CaseId).ToList());
    public Task AppendAuditAsync(Guid id, AuditEvent e, CancellationToken ct = default)
    {
        if (!Audit.TryGetValue(id, out var l)) Audit[id] = l = new();
        l.Add(e);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<AuditEvent>> GetAuditAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditEvent>>(Audit.GetValueOrDefault(id) ?? new());
    public Task AddQaLogAsync(QaLogEntry e, CancellationToken ct = default) { QaLog.Add(e); return Task.CompletedTask; }
    public Task<IReadOnlyList<QaLogEntry>> GetQaLogAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<QaLogEntry>>(QaLog.Where(q => q.CaseId == id).ToList());
    public Task<PolicyParameters> GetActivePolicyAsync(CancellationToken ct = default) => Task.FromResult(PolicyParameters.Default);
    public Task<bool> TryLockAsync(Guid id, TimeSpan stale, CancellationToken ct = default) => Task.FromResult(_locks.Add(id));
    public Task UnlockAsync(Guid id, CancellationToken ct = default) { _locks.Remove(id); return Task.CompletedTask; }
    public Task DeleteCaseAsync(Guid id, CancellationToken ct = default) { States.Remove(id); QaLog.RemoveAll(q => q.CaseId == id); return Task.CompletedTask; }
}

public sealed class InMemoryVectors : IVectorStore
{
    private readonly Dictionary<Guid, List<ChunkEmbedding>> _data = new();
    public Task ReplaceAsync(Guid caseId, IReadOnlyList<ChunkEmbedding> chunks, CancellationToken ct = default) { _data[caseId] = chunks.ToList(); return Task.CompletedTask; }
    public Task<IReadOnlyList<ScoredChunk>> SearchAsync(Guid caseId, float[] q, int k, CancellationToken ct = default)
    {
        var rows = (_data.GetValueOrDefault(caseId) ?? new()).Select(c => new ScoredChunk(c.ChunkId, c.Page, c.Text, Dist(q, c.Embedding)))
            .OrderBy(x => x.Distance).Take(k).ToList();
        return Task.FromResult<IReadOnlyList<ScoredChunk>>(rows);
    }
    public Task DeleteCaseAsync(Guid caseId, CancellationToken ct = default) { _data.Remove(caseId); return Task.CompletedTask; }
    public Task<int> CountAsync(Guid caseId, CancellationToken ct = default) => Task.FromResult(_data.GetValueOrDefault(caseId)?.Count ?? 0);
    private static double Dist(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na == 0 || nb == 0 ? 1 : 1 - dot / Math.Sqrt(na * nb);
    }
}

public static class Samples
{
    public static string Folder
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
            return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("samples folder not found"), "samples");
        }
    }
    public static string Pdf(string prefix) => Directory.GetFiles(Folder, prefix + "*.pdf").Single();

    public static ExtractedMetrics Apex => new(4920000, 69800, 4850200, 210000, 115300, 48250, 446900, 350000, 900000, null, null, null);
    public static ExtractedMetrics Nexus => new(2350000, 39200, 2310800, 120000, 45000, 23100, 157000, 240000, 650000, null, null, null);
    public static ExtractedMetrics Summit => new(6200000, 79500, 6120500, 250000, 180000, 42000, 708000, 250000, 900000, null, null, null);
    public static ExtractedMetrics Vantage => new(1910000, 60000, 1850000, 90000, 35000, 45000, 15000, 275000, 650000, null, null, null);
}
