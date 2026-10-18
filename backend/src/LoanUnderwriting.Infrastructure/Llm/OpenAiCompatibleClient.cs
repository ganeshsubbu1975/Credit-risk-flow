using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using LoanUnderwriting.Core;
using Microsoft.Extensions.Logging;

namespace LoanUnderwriting.Infrastructure.Llm;

/// <summary>
/// Chat and embeddings over the OpenAI-compatible REST API. The same client talks to Foundry Local (on-prem)
/// and to a Microsoft Foundry deployment (Azure); only the profile (base URL, auth, model names) differs.
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmClient, IEmbeddingClient
{
    private readonly HttpClient _http;
    private readonly ModelProfile _p;
    private readonly ILogger<OpenAiCompatibleClient> _log;
    private readonly TokenCredential? _credential;

    public OpenAiCompatibleClient(HttpClient http, ModelProfile profile, ILogger<OpenAiCompatibleClient> log)
    {
        _http = http;
        _p = profile;
        _log = log;
        _http.BaseAddress = new Uri(profile.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(profile.TimeoutSeconds);
        if (profile.AuthMode.Equals("AzureAd", StringComparison.OrdinalIgnoreCase)) _credential = AzureCredentials.Create();
    }

    public int Dimensions => _p.EmbeddingDimensions;
    public string ModelName => _p.EmbeddingModel;

    private async Task<HttpRequestMessage> BuildAsync(string path, object body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        switch (_p.AuthMode.ToLowerInvariant())
        {
            case "apikey":
                req.Headers.Add("api-key", _p.ApiKey);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _p.ApiKey);
                break;
            case "azuread":
                var token = await _credential!.GetTokenAsync(new TokenRequestContext(new[] { _p.AzureScope }), ct);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                break;
        }
        return req;
    }

    public async Task<LlmResponse> CompleteAsync(ModelRole role, string system, string user, CancellationToken ct = default)
    {
        if (!_p.Models.TryGetValue(role.ToString(), out var model) || string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException($"No model configured for role {role}.");

        var body = new
        {
            model,
            temperature = 0,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
        };
        using var req = await BuildAsync("chat/completions", body, ct);
        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var detail = json.Length > 300 ? json[..300] : json;
            _log.LogError("Model call failed for {Role} ({Model}): {Status} {Detail}", role, model, (int)resp.StatusCode, detail);
            throw new HttpRequestException($"Model endpoint returned {(int)resp.StatusCode} for role {role}: {detail}");
        }
        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        int? tokens = doc.RootElement.TryGetProperty("usage", out var u) && u.TryGetProperty("total_tokens", out var t) ? t.GetInt32() : null;
        return new LlmResponse(text, tokens, model);
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_p.EmbeddingModel)) throw new InvalidOperationException("No embedding model configured.");
        var all = new List<float[]>();
        foreach (var batch in texts.Chunk(16))
        {
            using var req = await BuildAsync("embeddings", new { model = _p.EmbeddingModel, input = batch }, ct);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Embedding endpoint returned {(int)resp.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray().OrderBy(d => d.GetProperty("index").GetInt32()))
                all.Add(item.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray());
        }
        if (all.Count > 0 && all[0].Length != Dimensions)
            throw new InvalidOperationException($"Embedding model returned {all[0].Length} dimensions but {Dimensions} are configured.");
        return all.ToArray();
    }
}
