using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LoanUnderwriting.Core;

namespace LoanUnderwriting.Infrastructure.Llm;

/// <summary>
/// OFFLINE TEST DOUBLE, not a model. Rules-based stand-in for the LLM and embedding endpoints so the whole pipeline can be run
/// and tested without Foundry. It understands the labelled line format used by the sample PDFs in /samples
/// (for example "Net revenue (Line 1c): $4,850,200"). Never use it for real decisions.
/// </summary>
public sealed class StubLlmClient : ILlmClient
{
    private static readonly (string Key, string Pattern)[] Fields =
    {
        ("grossReceipts", @"Line\s*1a\b"), ("returns", @"Line\s*1b\b"), ("netRevenue", @"Line\s*1c\b"), ("officerComp", @"Line\s*7\b"),
        ("depreciation", @"Line\s*14\b"), ("interest", @"Line\s*13\b"), ("taxableIncome", @"Line\s*21\b"),
        ("shortTermDebt", @"Line\s*17d\b"), ("longTermDebt", @"Line\s*19d\b"),
        ("bankCredits", @"bank\s+statement\s+total\s+credits"), ("debtService", @"annual\s+debt\s+service"), ("statedEbitda", @"calculated\s+ebitda"),
    };

    private static readonly HashSet<string> Finance = new(StringComparer.OrdinalIgnoreCase)
    {
        "revenue","sales","receipts","debt","interest","depreciation","amortization","income","taxable","officer","compensation","returns",
        "allowances","ebitda","ratio","line","form","page","balance","sheet","notes","mortgages","payable","net","gross","total","document",
        "pdf","tax","statement","fiscal","year","company","entity","profit","leverage","borrowings","salary","bank","credits","short","long","term","debts",
    };

    public Task<LlmResponse> CompleteAsync(ModelRole role, string system, string user, CancellationToken ct = default)
    {
        var text = role switch
        {
            ModelRole.Extraction => Extract(user),
            ModelRole.Compliance => "{\"flags\":[]}",
            ModelRole.Risk => Risk(user),
            ModelRole.ScopeCheck => Scope(user),
            ModelRole.QaAnswer => Answer(user),
            _ => "{}",
        };
        return Task.FromResult(new LlmResponse(text, null, "stub"));
    }

    private static string Extract(string user)
    {
        var metrics = new Dictionary<string, decimal?>();
        var conf = new Dictionary<string, double>();
        foreach (var (key, pattern) in Fields)
        {
            var m = Regex.Match(user, pattern + @"[^\d$\r\n]{0,40}\$?\s*(-?[\d,]+(?:\.\d+)?)", RegexOptions.IgnoreCase);
            if (m.Success && decimal.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
            {
                metrics[key] = v;
                conf[key] = 0.95;
            }
        }
        var firstLine = user.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("DOCUMENT", StringComparison.OrdinalIgnoreCase) && l != "<<<") ?? "";
        var name = Regex.Match(firstLine, @"^[^-:(]+").Value.Trim();
        return JsonSerializer.Serialize(new { entityName = name, metrics, confidence = conf }, JsonUtil.Options);
    }

    private static string Risk(string user)
    {
        var m = Regex.Match(user, @"Risk score:\s*(\d+(?:\.\d+)?)");
        var score = m.Success ? decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 50m;
        var rec = score < 30 ? "Approve" : score > 70 ? "Deny" : "Escalate";
        return JsonSerializer.Serialize(new { recommendation = rec, rationale = $"Stub rationale: risk score {score} maps to {rec}." });
    }

    private static string Scope(string user)
    {
        var q = Regex.Match(user, @"Question:\s*(.*)", RegexOptions.Singleline).Groups[1].Value;
        var doc = Regex.Match(user, @"Document:\s*(.*)").Groups[1].Value.ToLowerInvariant();
        var lower = q.ToLowerInvariant();
        var otherEntity = new[] { "apex", "nexus", "summit", "vantage" }.Any(n => lower.Contains(n) && !doc.Contains(n));
        var inScope = !otherEntity && Regex.Matches(q, @"[A-Za-z]+").Any(w => Finance.Contains(w.Value));
        return $"{{\"in_scope\": {(inScope ? "true" : "false")}, \"category\": \"stub\"}}";
    }

    private static readonly HashSet<string> Generic = new(new[] { "company", "document", "entity", "total", "number", "name", "list", "give", "tell" });

    private static string Answer(string user)
    {
        var m = Regex.Match(user, @"\[S1\]\s*\(page \d+\)\s*(.*)");
        if (!m.Success) return "{\"refuse\": true}";

        // Refuse unless the best excerpt actually covers the question's content words (a real model judges this itself).
        var question = Regex.Match(user, @"Question:\s*(.*)").Groups[1].Value;
        var excerpt = new HashSet<string>(Regex.Matches(m.Groups[1].Value.ToLowerInvariant(), @"[a-z0-9]+").Select(x => x.Value));
        var words = Regex.Matches(question.ToLowerInvariant(), @"[a-z0-9]+").Select(x => x.Value)
            .Where(w => w.Length > 2 && !StubEmbeddingClient.IsStopWord(w) && !Generic.Contains(w)).Distinct().ToList();
        var covered = words.Count(w => excerpt.Contains(w) || StubEmbeddingClient.Synonyms(w).Any(excerpt.Contains));
        if (words.Count == 0 || covered * 3 < words.Count * 2) return "{\"refuse\": true}";

        // Answer with the single line of the excerpt that best covers the question (a real model would phrase this itself).
        var lines = Regex.Split(m.Groups[1].Value.Trim(), @"(?<=\$[\d,]+\.?)\s+(?=[A-Z])").Where(l => l.Length > 0).ToList();
        var best = lines.OrderByDescending(l =>
        {
            var lt = new HashSet<string>(Regex.Matches(l.ToLowerInvariant(), @"[a-z0-9]+").Select(x => x.Value));
            return words.Count(w => lt.Contains(w) || StubEmbeddingClient.Synonyms(w).Any(lt.Contains));
        }).First();
        return JsonSerializer.Serialize(new { answer = best, citations = new[] { "S1" } });
    }
}

/// <summary>Offline test double: hashed bag-of-words vectors with a tiny synonym map. Not a model.</summary>
public sealed class StubEmbeddingClient(int dimensions) : IEmbeddingClient
{
    private static readonly HashSet<string> Stop = new("a an the of in on for to and or is are was were what which how much many does do did me my is its this that with from by at as be can tell show give please about reported".Split(' '));
    private static readonly Dictionary<string, string[]> Syn = new()
    {
        ["revenue"] = new[] { "sales", "receipts" }, ["sales"] = new[] { "revenue", "receipts" }, ["receipts"] = new[] { "revenue", "sales" },
        ["debt"] = new[] { "notes", "mortgages", "payable" }, ["loan"] = new[] { "debt", "notes" }, ["interest"] = new[] { "interest" },
        ["officer"] = new[] { "compensation" }, ["salary"] = new[] { "officer", "compensation" },
    };

    internal static bool IsStopWord(string w) => Stop.Contains(w);
    internal static IEnumerable<string> Synonyms(string w) => Syn.TryGetValue(w, out var s) ? s : Array.Empty<string>();

    public int Dimensions => dimensions;
    public string ModelName => "stub-hashing";

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
        Task.FromResult(texts.Select(Embed).ToArray());

    private float[] Embed(string text)
    {
        var v = new float[dimensions];
        foreach (var raw in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+").Select(m => m.Value))
        {
            if (raw.Length < 2 || Stop.Contains(raw)) continue;
            Add(v, raw, 1f);
            if (Syn.TryGetValue(raw, out var syn)) foreach (var s in syn) Add(v, s, 0.5f);
        }
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    private void Add(float[] v, string token, float w)
    {
        uint h = 2166136261;
        foreach (var c in token) h = (h ^ c) * 16777619;
        v[h % (uint)dimensions] += w;
    }
}
