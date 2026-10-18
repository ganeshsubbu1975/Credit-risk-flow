namespace LoanUnderwriting.Core;

public enum ModelRole { Extraction, Compliance, Risk, ScopeCheck, QaAnswer }

public sealed record LlmResponse(string Text, int? Tokens, string Model);

/// <summary>Chat completion. Implementations: OpenAI-compatible endpoint (Foundry Local / Azure Foundry) or the offline test stub.</summary>
public interface ILlmClient
{
    Task<LlmResponse> CompleteAsync(ModelRole role, string system, string user, CancellationToken ct = default);
}

public interface IEmbeddingClient
{
    int Dimensions { get; }
    string ModelName { get; }
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}

public interface IDocumentStore
{
    Task<DocumentRef> SaveAsync(Stream content, string fileName, CancellationToken ct = default);
    Task<Stream> OpenAsync(DocumentRef doc, CancellationToken ct = default);
    Task DeleteAsync(DocumentRef doc, CancellationToken ct = default);
}

public sealed record PdfPage(int Number, string Text);

public sealed record PdfContent(IReadOnlyList<PdfPage> Pages)
{
    public bool HasText => Pages.Any(p => !string.IsNullOrWhiteSpace(p.Text));
    public string FullText => string.Join("\n", Pages.Select(p => p.Text));
}

public interface IPdfReader
{
    PdfContent Read(Stream pdf);
}

public sealed record ChunkEmbedding(string ChunkId, int Page, int Index, string Text, string ContentHash, float[] Embedding);

public sealed record ScoredChunk(string ChunkId, int Page, string Text, double Distance);

public interface IVectorStore
{
    /// <summary>Replaces all chunks of the case (idempotent re-ingestion).</summary>
    Task ReplaceAsync(Guid caseId, IReadOnlyList<ChunkEmbedding> chunks, CancellationToken ct = default);

    /// <summary>The case id is a required argument: a search without a case filter is not possible.</summary>
    Task<IReadOnlyList<ScoredChunk>> SearchAsync(Guid caseId, float[] query, int k, CancellationToken ct = default);

    Task DeleteCaseAsync(Guid caseId, CancellationToken ct = default);
    Task<int> CountAsync(Guid caseId, CancellationToken ct = default);
}

public sealed record QaLogEntry(Guid QaId, Guid CaseId, string AskedBy, DateTimeOffset AskedAt, string Question,
    bool Refused, RefusalReason Reason, string? Answer, IReadOnlyList<Citation> Citations, IReadOnlyList<string> ChunkIds);

public interface ICaseRepository
{
    Task CreateAsync(LoanState state, CancellationToken ct = default);
    Task<LoanState?> LoadAsync(Guid caseId, CancellationToken ct = default);
    /// <summary>Saves the latest state and appends a checkpoint row for the step.</summary>
    Task SaveAsync(LoanState state, string step, CancellationToken ct = default);
    Task<IReadOnlyList<CaseSummary>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> ListByStatusAsync(CaseStatus status, CancellationToken ct = default);
    Task AppendAuditAsync(Guid caseId, AuditEvent e, CancellationToken ct = default);
    Task<IReadOnlyList<AuditEvent>> GetAuditAsync(Guid caseId, CancellationToken ct = default);
    Task AddQaLogAsync(QaLogEntry e, CancellationToken ct = default);
    Task<IReadOnlyList<QaLogEntry>> GetQaLogAsync(Guid caseId, CancellationToken ct = default);
    Task<PolicyParameters> GetActivePolicyAsync(CancellationToken ct = default);
    Task<bool> TryLockAsync(Guid caseId, TimeSpan staleAfter, CancellationToken ct = default);
    Task UnlockAsync(Guid caseId, CancellationToken ct = default);
    /// <summary>Removes the case, its checkpoints and its Q&amp;A log. The audit trail is retained.</summary>
    Task DeleteCaseAsync(Guid caseId, CancellationToken ct = default);
}

public interface IProgressNotifier
{
    Task StepChangedAsync(Guid caseId, StepInfo step);
    Task StatusChangedAsync(Guid caseId, CaseStatus status);
}

public sealed class NullProgressNotifier : IProgressNotifier
{
    public Task StepChangedAsync(Guid caseId, StepInfo step) => Task.CompletedTask;
    public Task StatusChangedAsync(Guid caseId, CaseStatus status) => Task.CompletedTask;
}
