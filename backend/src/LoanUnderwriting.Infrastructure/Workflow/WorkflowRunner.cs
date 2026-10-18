using System.Diagnostics;
using LoanUnderwriting.Core;
using Microsoft.Extensions.Logging;

namespace LoanUnderwriting.Infrastructure.Workflow;

/// <summary>
/// Runs the case through the steps, writing a checkpoint and an audit event after each one. State is immutable (each step returns a
/// new record). Human review is a pause: the case is saved as AwaitingReview and the run stops; <see cref="ResumeAsync"/> continues
/// from the saved state. This runner sits behind the design's workflow port, so the Microsoft Agent Framework can replace it later.
/// </summary>
public sealed class WorkflowRunner(ICaseRepository repo, IEnumerable<IWorkflowStep> steps, IProgressNotifier notifier, ILogger<WorkflowRunner> log)
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMinutes(10);
    private readonly IReadOnlyList<IWorkflowStep> _steps = steps.ToList();

    public static IReadOnlyList<StepInfo> NewSteps() => new List<StepInfo>
    {
        new("upload", "Upload", "done", "File accepted"),
        new("ingest", "PDF read and ingest to vector DB", "pending"),
        new("n1", "Node 1: Extraction and verification", "pending"),
        new("n2", "Node 2: Fraud and compliance", "pending"),
        new("score", "Scorecard", "pending"),
        new("n3", "Node 3: Risk synthesis", "pending"),
        new("decision", "Decision model", "pending"),
        new("hitl", "Human review", "pending"),
        new("final", "Final decision", "pending"),
    };

    private static LoanState SetStep(LoanState s, string key, Func<StepInfo, StepInfo> f) =>
        s with { Steps = s.Steps.Select(x => x.Key == key ? f(x) : x).ToList() };

    public async Task RunAsync(Guid caseId, CancellationToken ct = default)
    {
        if (!await repo.TryLockAsync(caseId, LockTimeout, ct)) { log.LogInformation("Case {Case} is already running.", caseId); return; }
        try
        {
            var state = await repo.LoadAsync(caseId, ct) ?? throw new InvalidOperationException("Case not found.");
            if (state.Status is CaseStatus.Approved or CaseStatus.Denied or CaseStatus.Error) return;

            foreach (var step in _steps)
            {
                if (state.Steps.First(x => x.Key == step.Key).Status is "done" or "skipped") continue;

                // Human-review gate: pause before the final step until an underwriter has decided.
                if (step.Key == "final" && state.Decision?.Decision == Outcome.Escalate && state.Human is null)
                {
                    state = SetStep(state with { Status = CaseStatus.AwaitingReview }, "hitl", x => x with { Status = "running", Summary = "Waiting for an underwriter" });
                    await repo.SaveAsync(state, "hitl-wait", ct);
                    await notifier.StepChangedAsync(caseId, state.Steps.First(x => x.Key == "hitl"));
                    await notifier.StatusChangedAsync(caseId, state.Status);
                    return;
                }

                state = SetStep(state, step.Key, x => x with { Status = "running" });
                await repo.SaveAsync(state, step.Key + "-start", ct);
                await notifier.StepChangedAsync(caseId, state.Steps.First(x => x.Key == step.Key));

                var sw = Stopwatch.StartNew();
                StepResult result;
                try
                {
                    result = await step.ExecuteAsync(state, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Step {Step} failed for case {Case}", step.Key, caseId);
                    state = SetStep(state with { Status = CaseStatus.Error, Error = ex.Message }, step.Key, x => x with { Status = "error", Summary = ex.Message });
                    await repo.SaveAsync(state, step.Key + "-error", ct);
                    await repo.AppendAuditAsync(caseId, new AuditEvent(DateTimeOffset.UtcNow, "system", step.Label, "Failed: " + ex.Message), ct);
                    await notifier.StepChangedAsync(caseId, state.Steps.First(x => x.Key == step.Key));
                    await notifier.StatusChangedAsync(caseId, CaseStatus.Error);
                    return;
                }
                sw.Stop();

                var ms = (int)sw.ElapsedMilliseconds;
                state = SetStep(result.State, step.Key, x => x with
                {
                    Status = result.Skipped ? "skipped" : "done", Summary = result.Summary, Model = result.Model, LatencyMs = ms, Tokens = result.Tokens,
                });
                if (step.Key == "final")
                    state = SetStep(state, "hitl", x => x.Status == "pending" ? x with { Status = "skipped", Summary = "Not required" } : x);
                await repo.SaveAsync(state, step.Key, ct);
                await repo.AppendAuditAsync(caseId, new AuditEvent(DateTimeOffset.UtcNow, "system", step.Label, result.Summary, result.Model,
                    step.Key == "decision" ? state.Decision?.ModelVersion : null, result.InputHash, result.Tokens, ms), ct);
                await notifier.StepChangedAsync(caseId, state.Steps.First(x => x.Key == step.Key));
            }
            await notifier.StatusChangedAsync(caseId, state.Status);
        }
        finally
        {
            await repo.UnlockAsync(caseId, CancellationToken.None);
        }
    }

    /// <summary>Applies the underwriter's decision to a paused case and runs the remaining steps.</summary>
    public async Task ResumeAsync(Guid caseId, HumanDecision decision, CancellationToken ct = default)
    {
        var state = await repo.LoadAsync(caseId, ct) ?? throw new InvalidOperationException("Case not found.");
        if (state.Status != CaseStatus.AwaitingReview || state.Human is not null)
            throw new InvalidOperationException("The case is not waiting for review.");
        if (decision.Final == Outcome.Escalate) throw new ArgumentException("The final decision must be Approve or Deny.");

        state = SetStep(state with { Human = decision }, "hitl", x => x with { Status = "done", Summary = $"{decision.By}: {decision.Action} ({decision.Final})" });
        await repo.SaveAsync(state, "hitl-done", ct);
        await repo.AppendAuditAsync(caseId, new AuditEvent(decision.At, decision.By, "Human review",
            $"{decision.Action}; final {decision.Final}. AI recommendation: {state.LlmRecommendation?.Outcome}; decision model: {state.Decision?.Decision}. Reason: {(string.IsNullOrWhiteSpace(decision.Reason) ? "none" : decision.Reason)}",
            DecisionModelVersion: state.Decision?.ModelVersion), ct);
        await notifier.StepChangedAsync(caseId, state.Steps.First(x => x.Key == "hitl"));
        await RunAsync(caseId, ct);
    }
}
