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

    /// <summary>Steps that read only the document and write disjoint fields of the state, so they can run at the same time.</summary>
    private static readonly string[] ParallelKeys = { "ingest", "n1" };

    /// <summary>The step list grouped into stages; the steps of a stage run together, stages run in order.</summary>
    private IEnumerable<IReadOnlyList<IWorkflowStep>> Stages()
    {
        var group = _steps.Where(s => ParallelKeys.Contains(s.Key)).ToList();
        foreach (var step in _steps)
        {
            if (!ParallelKeys.Contains(step.Key)) yield return new[] { step };
            else if (step == group[0]) yield return group;
        }
    }

    private sealed record StepRun(IWorkflowStep Step, StepResult? Result, Exception? Error, int Ms);

    private async Task<StepRun> RunStepAsync(IWorkflowStep step, LoanState state, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await step.ExecuteAsync(state, ct);
            return new StepRun(step, result, null, (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Step {Step} failed for case {Case}", step.Key, state.CaseId);
            return new StepRun(step, null, ex, (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Folds a step's result into the current state. Parallel steps were started from the same snapshot, so the current step list is kept
    /// and ingest contributes only the chunk count, which extraction does not set.
    /// </summary>
    private static LoanState Merge(LoanState current, string key, LoanState produced) =>
        key == "ingest"
            ? current with { ChunkCount = produced.ChunkCount }
            : produced with { Steps = current.Steps, ChunkCount = current.ChunkCount };

    public async Task RunAsync(Guid caseId, CancellationToken ct = default)
    {
        if (!await repo.TryLockAsync(caseId, LockTimeout, ct)) { log.LogInformation("Case {Case} is already running.", caseId); return; }
        try
        {
            var state = await repo.LoadAsync(caseId, ct) ?? throw new InvalidOperationException("Case not found.");
            if (state.Status is CaseStatus.Approved or CaseStatus.Denied or CaseStatus.Error) return;

            foreach (var stage in Stages())
            {
                var pending = stage.Where(s => state.Steps.First(x => x.Key == s.Key).Status is not ("done" or "skipped")).ToList();
                if (pending.Count == 0) continue;

                // Human-review gate: pause before the final step until an underwriter has decided.
                if (pending[0].Key == "final" && state.Decision?.Decision == Outcome.Escalate && state.Human is null)
                {
                    state = SetStep(state with { Status = CaseStatus.AwaitingReview }, "hitl", x => x with { Status = "running", Summary = "Waiting for an underwriter" });
                    await repo.SaveAsync(state, "hitl-wait", ct);
                    await notifier.StepChangedAsync(caseId, state.Steps.First(x => x.Key == "hitl"));
                    await notifier.StatusChangedAsync(caseId, state.Status);
                    return;
                }

                foreach (var step in pending) state = SetStep(state, step.Key, x => x with { Status = "running" });
                await repo.SaveAsync(state, string.Join("+", pending.Select(p => p.Key)) + "-start", ct);
                foreach (var step in pending) await notifier.StepChangedAsync(caseId, state.Steps.First(x => x.Key == step.Key));

                // Only ExecuteAsync overlaps; checkpoints, audit and notifications below stay sequential.
                var inputs = state;
                var runs = await Task.WhenAll(pending.Select(step => RunStepAsync(step, inputs, ct)));

                // Keep the work of steps that succeeded before reporting a failure.
                foreach (var run in runs.Where(r => r.Result is not null))
                {
                    var step = run.Step;
                    var result = run.Result!;
                    state = Merge(state, step.Key, result.State);
                    state = SetStep(state, step.Key, x => x with
                    {
                        Status = result.Skipped ? "skipped" : "done", Summary = result.Summary, Model = result.Model, LatencyMs = run.Ms, Tokens = result.Tokens,
                    });
                    if (step.Key == "final")
                        state = SetStep(state, "hitl", x => x.Status == "pending" ? x with { Status = "skipped", Summary = "Not required" } : x);
                    await repo.SaveAsync(state, step.Key, ct);
                    await repo.AppendAuditAsync(caseId, new AuditEvent(DateTimeOffset.UtcNow, "system", step.Label, result.Summary, result.Model,
                        step.Key == "decision" ? state.Decision?.ModelVersion : null, result.InputHash, result.Tokens, run.Ms), ct);
                    await notifier.StepChangedAsync(caseId, state.Steps.First(x => x.Key == step.Key));
                }

                var failed = runs.FirstOrDefault(r => r.Error is not null);
                if (failed is not null)
                {
                    var step = failed.Step;
                    var message = failed.Error!.Message;
                    state = SetStep(state with { Status = CaseStatus.Error, Error = message }, step.Key, x => x with { Status = "error", Summary = message });
                    await repo.SaveAsync(state, step.Key + "-error", ct);
                    await repo.AppendAuditAsync(caseId, new AuditEvent(DateTimeOffset.UtcNow, "system", step.Label, "Failed: " + message), ct);
                    await notifier.StepChangedAsync(caseId, state.Steps.First(x => x.Key == step.Key));
                    await notifier.StatusChangedAsync(caseId, CaseStatus.Error);
                    return;
                }
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
