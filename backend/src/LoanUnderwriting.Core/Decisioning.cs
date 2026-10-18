namespace LoanUnderwriting.Core;

public sealed record DecisionInput(
    decimal? RiskScore, bool Complete, decimal MinConfidence, bool ReconciliationOk,
    IReadOnlyList<ComplianceFlag> Flags, Outcome LlmRecommendation);

/// <summary>
/// Ordered decision table, first match wins (PRD FR-5). The LLM recommendation is an input, never the decider.
/// </summary>
public static class DecisionEvaluator
{
    public static DecisionResult Evaluate(DecisionInput i, PolicyParameters p)
    {
        DecisionResult Mk(Outcome d, string rule, string code, string why) => new(d, rule, new[] { code }, why, p.Version);
        var worst = i.Flags.Count == 0 ? -1 : i.Flags.Max(f => (int)f.Severity);

        if (!i.Complete || i.RiskScore is null)
            return Mk(Outcome.Escalate, "R1", "INCOMPLETE_DATA", "Required fields are missing after retry.");
        if (worst >= (int)Severity.Critical)
            return Mk(Outcome.Escalate, "R2", "CRITICAL_COMPLIANCE_FLAG", "A critical compliance flag requires compliance review; never auto-approved.");
        if (i.MinConfidence < p.MinConfidence)
            return Mk(Outcome.Escalate, "R3", "LOW_CONFIDENCE", $"Minimum extraction confidence {i.MinConfidence:0.00} is below {p.MinConfidence:0.00}.");
        if (!i.ReconciliationOk)
            return Mk(Outcome.Escalate, "R3", "RECONCILIATION_FAILED", "Extracted figures do not reconcile arithmetically.");

        var score = i.RiskScore.Value;
        if (score > p.DenyAbove)
            return Mk(Outcome.Deny, "R4", "HIGH_RISK_SCORE", $"Risk score {score} is above {p.DenyAbove}.");
        if (score < p.ApproveBelow && worst < (int)Severity.High && i.LlmRecommendation != Outcome.Deny)
            return Mk(Outcome.Approve, "R5", "LOW_RISK_CLEAN", $"Risk score {score} is below {p.ApproveBelow} with no high-severity flags.");

        var disagree = score < p.ApproveBelow && i.LlmRecommendation == Outcome.Deny;
        return disagree
            ? Mk(Outcome.Escalate, "R6", "MODEL_DISAGREEMENT", "The AI recommendation disagrees with the scorecard.")
            : Mk(Outcome.Escalate, "R6", worst >= (int)Severity.High ? "HIGH_SEVERITY_FLAG" : "MEDIUM_RISK",
                 worst >= (int)Severity.High
                     ? "A high-severity flag prevents automatic approval; human review required."
                     : $"Risk score {score} is between {p.ApproveBelow} and {p.DenyAbove}; human review required.");
    }
}

/// <summary>Deterministic compliance checks that run in code alongside the model's findings.</summary>
public static class DeterministicChecks
{
    public static IReadOnlyList<ComplianceFlag> Run(ExtractedMetrics m)
    {
        var flags = new List<ComplianceFlag>();
        if (m.BankCredits is not null && m.GrossReceipts > 0)
        {
            var v = Math.Abs(m.GrossReceipts - m.BankCredits.Value) / m.GrossReceipts;
            if (v > 0.05m) flags.Add(new("TB-HIGH", Severity.High, "Tax-to-bank variance", $"Tax gross receipts and bank credits differ by {v * 100:0.0}%."));
            else if (v > 0.01m) flags.Add(new("TB-MED", Severity.Medium, "Tax-to-bank variance", $"Tax gross receipts and bank credits differ by {v * 100:0.0}%."));
            else flags.Add(new("TB-LOW", Severity.Low, "Tax-to-bank variance", $"Variance of {v * 100:0.00}% is within tolerance."));
        }
        if (m.GrossReceipts > 0 && m.Returns / m.GrossReceipts > 0.05m)
            flags.Add(new("RET-01", Severity.Medium, "Returns and allowances", $"Returns are {m.Returns / m.GrossReceipts * 100:0.0}% of gross receipts."));
        if (m.TaxableIncome <= 0)
            flags.Add(new("INC-01", Severity.Medium, "Taxable income", "Taxable income is zero or negative."));
        else if (m.NetRevenue > 0 && m.TaxableIncome / m.NetRevenue < 0.02m && (m.ShortTermDebt + m.LongTermDebt) > m.NetRevenue * 0.4m)
            flags.Add(new("CF-01", Severity.Medium, "Thin profitability against debt", $"Taxable income is {m.TaxableIncome / m.NetRevenue * 100:0.0}% of revenue while debt is {(m.ShortTermDebt + m.LongTermDebt) / m.NetRevenue * 100:0}% of revenue."));
        return flags;
    }
}
