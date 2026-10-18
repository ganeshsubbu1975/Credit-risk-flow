using System.Text.RegularExpressions;

namespace LoanUnderwriting.Core;

public sealed record GuardResult(bool Allowed, RefusalReason Reason = RefusalReason.None);

/// <summary>Input-side guardrails (G1 and G2): size, prompt injection and decision-integrity blocking.</summary>
public static class InputGuard
{
    public const int MaxQuestionLength = 500;

    private static readonly Regex Injection = new(
        @"(ignore|disregard|forget|override).{0,30}(instruction|previous|above|prompt|rules|guardrail)|system\s*prompt|\bact as\b|\bpretend\b|role[- ]?play|jailbreak|developer mode|\bDAN\b|reveal your (prompt|instructions)|you are now",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DecisionIntegrity = new(
        @"risk\s*score|recommend|should (we|i) (approve|lend|deny|reject)|approve (this|the) (loan|case|application)|(deny|reject) (this|the) (loan|case|application)|decision model|credit\s*worth|override|compliance flag|fraud|\bAML\b|\bKYC\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="allowDecisionWords">True for questions routed to the stored analyst commentary, which may use words such as "recommend".</param>
    public static GuardResult Check(string? question, bool allowDecisionWords = false)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Trim().Length < 3 || question.Length > MaxQuestionLength)
            return new(false, RefusalReason.Blocked);
        if (Injection.IsMatch(question)) return new(false, RefusalReason.Injection);
        if (!allowDecisionWords && DecisionIntegrity.IsMatch(question)) return new(false, RefusalReason.Blocked);
        return new(true);
    }
}

/// <summary>Output-side guardrail (G5): every material number in an answer must appear in the cited excerpts.</summary>
public static class GroundingGuard
{
    private static readonly Regex Number = new(@"\$?\d[\d,]*(\.\d+)?(%|x)?", RegexOptions.Compiled);

    public static bool NumbersAreGrounded(string answer, IEnumerable<string> citedTexts)
    {
        var haystack = Normalise(string.Join(" ", citedTexts));
        foreach (Match m in Number.Matches(answer))
        {
            var raw = m.Value;
            var token = Normalise(raw);
            // Small bare integers (page or line references such as "1c", "7") are not figures.
            var material = raw.Contains('$') || raw.Contains('%') || raw.EndsWith('x') || token.Length >= 3;
            if (!material) continue;
            if (!haystack.Contains(token)) return false;
        }
        return true;
    }

    private static string Normalise(string s) => s.Replace(",", "").Replace("$", "").Replace("%", "").Replace("x", "").ToLowerInvariant();
}
