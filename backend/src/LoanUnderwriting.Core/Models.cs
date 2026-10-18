using System.Text.Json.Serialization;

namespace LoanUnderwriting.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaseStatus { Processing, AwaitingReview, Approved, Denied, Error }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Severity { Low, Medium, High, Critical }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Outcome { Approve, Deny, Escalate }

public sealed record DocumentRef(string Id, string FileName);

public sealed record ExtractedMetrics(
    decimal GrossReceipts, decimal Returns, decimal NetRevenue, decimal OfficerComp, decimal Depreciation,
    decimal Interest, decimal TaxableIncome, decimal ShortTermDebt, decimal LongTermDebt,
    decimal? BankCredits, decimal? DebtService, decimal? StatedEbitda);

public sealed record ComplianceFlag(string Id, Severity Severity, string Rule, string Evidence);

public sealed record Recommendation(Outcome Outcome, string Rationale);

public sealed record DerivedMetrics(
    decimal Ebitda, decimal EbitdaExOfficer, decimal TotalDebt, decimal Leverage, decimal Margin, decimal InterestCover);

public sealed record PillarScore(string Name, decimal Weight, string Display, decimal? Score);

public sealed record ScorecardResult(
    decimal RiskScore, decimal Creditworthiness, IReadOnlyList<PillarScore> Pillars, DerivedMetrics Derived);

public sealed record DecisionResult(
    Outcome Decision, string Rule, IReadOnlyList<string> ReasonCodes, string Explanation, string ModelVersion);

public sealed record Completeness(bool IsComplete, IReadOnlyList<string> Missing, decimal MinConfidence, bool ReconciliationOk);

public sealed record HumanDecision(string Action, Outcome Final, string Reason, string By, DateTimeOffset At);

public sealed record StepInfo(string Key, string Label, string Status, string? Summary = null, string? Model = null, int? LatencyMs = null, int? Tokens = null);

public sealed record AuditEvent(
    DateTimeOffset At, string Actor, string Step, string Summary, string? Model = null,
    string? DecisionModelVersion = null, string? InputHash = null, int? Tokens = null, int? LatencyMs = null);

/// <summary>Immutable workflow state. Every step returns a new copy via <c>with</c>.</summary>
public sealed record LoanState
{
    public Guid CaseId { get; init; }
    public string EntityName { get; init; } = "";
    public string FileName { get; init; } = "";
    public string SubmittedBy { get; init; } = "";
    public DateTimeOffset SubmittedAt { get; init; }
    public DocumentRef Document { get; init; } = new("", "");
    public CaseStatus Status { get; init; } = CaseStatus.Processing;
    public IReadOnlyList<StepInfo> Steps { get; init; } = Array.Empty<StepInfo>();
    public ExtractedMetrics? Metrics { get; init; }
    public IReadOnlyDictionary<string, double> FieldConfidence { get; init; } = new Dictionary<string, double>();
    public Completeness? Completeness { get; init; }
    public IReadOnlyList<ComplianceFlag> Flags { get; init; } = Array.Empty<ComplianceFlag>();
    public ScorecardResult? Scorecard { get; init; }
    public Recommendation? LlmRecommendation { get; init; }
    public DecisionResult? Decision { get; init; }
    public HumanDecision? Human { get; init; }
    public Outcome? Final { get; init; }
    public DateTimeOffset? FinalAt { get; init; }
    public string? Error { get; init; }
    public int ChunkCount { get; init; }
}

public sealed record Citation(string ChunkId, int Page, string Excerpt);

public enum RefusalReason { None, OutOfScope, NotFound, Blocked, Injection }

public sealed record QaResult(bool Refused, RefusalReason Reason, string Answer, IReadOnlyList<Citation> Citations);

public sealed record CaseSummary(
    Guid CaseId, string EntityName, string FileName, string SubmittedBy, DateTimeOffset SubmittedAt,
    CaseStatus Status, decimal? RiskScore, Outcome? Final);

public sealed record PolicyParameters(string Version, decimal ApproveBelow, decimal DenyAbove, decimal MinConfidence)
{
    public static PolicyParameters Default => new("DM-1.0.0", 30m, 70m, 0.80m);
}
