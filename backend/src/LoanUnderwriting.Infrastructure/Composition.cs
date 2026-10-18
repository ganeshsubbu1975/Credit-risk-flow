using LoanUnderwriting.Core;
using LoanUnderwriting.Infrastructure.Llm;
using LoanUnderwriting.Infrastructure.Sql;
using LoanUnderwriting.Infrastructure.Storage;
using LoanUnderwriting.Infrastructure.Workflow;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LoanUnderwriting.Infrastructure;

public sealed record CheckResult(string Name, bool Ok, string Detail);

public static class Composition
{
    public static IServiceCollection AddLoanUnderwriting(this IServiceCollection services, IConfiguration config)
    {
        var deployment = config.GetSection("Deployment").Get<DeploymentOptions>() ?? new();
        var db = config.GetSection("Database").Get<DatabaseOptions>() ?? new();
        var storage = config.GetSection("Storage").Get<StorageOptions>() ?? new();
        var models = config.GetSection("Models").Get<ModelOptions>() ?? new();
        var workflow = config.GetSection("Workflow").Get<WorkflowOptions>() ?? new();
        var qa = config.GetSection("Qa").Get<QaOptions>() ?? new();
        var selfCheck = config.GetSection("SelfCheck").Get<SelfCheckOptions>() ?? new();
        var profile = models.Active(deployment.Mode);

        services.AddSingleton(deployment).AddSingleton(db).AddSingleton(storage).AddSingleton(models).AddSingleton(workflow)
                .AddSingleton(qa).AddSingleton(selfCheck).AddSingleton(profile);

        // Platform adapters, selected by Deployment:Mode (PRD FR-11). The Azure document store and Document Intelligence adapters are a listed task.
        services.AddSingleton<SqlDb>();
        services.AddSingleton<ICaseRepository, SqlCaseRepository>();
        services.AddSingleton<IPdfReader, PdfPigReader>();
        services.AddSingleton<IDocumentStore>(_ => new FileDocumentStore(storage.ResolvedFolder));
        services.AddSingleton(new VectorRuntime { UseNative = !db.VectorProvider.Equals("JsonFallback", StringComparison.OrdinalIgnoreCase) });
        services.AddSingleton(sp => new SchemaInitializer(db, profile.EmbeddingDimensions, sp.GetRequiredService<VectorRuntime>(), sp.GetRequiredService<ILogger<SchemaInitializer>>()));
        services.AddSingleton<IVectorStore>(sp => new AdaptiveVectorStore(sp.GetRequiredService<SqlDb>(), profile.EmbeddingDimensions, sp.GetRequiredService<VectorRuntime>()));

        if (profile.Provider.Equals("Stub", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ILlmClient, StubLlmClient>();
            services.AddSingleton<IEmbeddingClient>(_ => new StubEmbeddingClient(profile.EmbeddingDimensions));
        }
        else
        {
            services.AddSingleton(sp => new OpenAiCompatibleClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient(), profile,
                sp.GetRequiredService<ILogger<OpenAiCompatibleClient>>()));
            services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<OpenAiCompatibleClient>());
            services.AddSingleton<IEmbeddingClient>(sp => sp.GetRequiredService<OpenAiCompatibleClient>());
        }
        services.AddHttpClient();

        // Step order is the workflow order.
        services.AddSingleton<IWorkflowStep, IngestStep>();
        services.AddSingleton<IWorkflowStep, ExtractionStep>();
        services.AddSingleton<IWorkflowStep, ComplianceStep>();
        services.AddSingleton<IWorkflowStep, ScoreStep>();
        services.AddSingleton<IWorkflowStep, RiskStep>();
        services.AddSingleton<IWorkflowStep, DecisionStep>();
        services.AddSingleton<IWorkflowStep, FinalizeStep>();
        services.AddSingleton<WorkflowRunner>();
        services.AddSingleton<QaService>();
        services.AddSingleton<SelfCheck>();
        return services;
    }
}

/// <summary>Startup self-check: rejects mixed configurations and probes the database, vector support, storage and models.</summary>
public sealed class SelfCheck(DeploymentOptions deployment, DatabaseOptions db, StorageOptions storage, ModelProfile profile, SelfCheckOptions options,
    SqlDb sql, VectorRuntime vector, ILlmClient llm, IEmbeddingClient embeddings, ILogger<SelfCheck> log)
{
    public async Task<IReadOnlyList<CheckResult>> RunAsync(CancellationToken ct = default)
    {
        var results = new List<CheckResult>();
        void Add(string n, bool ok, string d) { results.Add(new(n, ok, d)); log.Log(ok ? LogLevel.Information : LogLevel.Error, "Self-check {Name}: {Detail}", n, d); }

        Add("Mode", true, $"Deployment:Mode={deployment.Mode}, models={profile.Provider}, vector={db.VectorProvider}");

        // Mixed-configuration rules.
        var url = profile.BaseUrl.ToLowerInvariant();
        if (deployment.Mode == DeploymentMode.Azure && profile.Provider.Equals("Stub", StringComparison.OrdinalIgnoreCase))
            Add("Configuration", false, "Azure mode must not use the Stub model provider.");
        else if (deployment.Mode == DeploymentMode.Azure && profile.AuthMode.Equals("None", StringComparison.OrdinalIgnoreCase))
            Add("Configuration", false, "Azure mode needs AuthMode ApiKey or AzureAd.");
        else if (deployment.Mode == DeploymentMode.OnPrem && (url.Contains("azure.com") || url.Contains("openai.com")))
            Add("Configuration", false, "OnPrem mode must not point at a cloud endpoint.");
        else Add("Configuration", true, "Mode and adapters are consistent.");

        try
        {
            await using var c = await sql.OpenAsync(ct);
            Add("Database", true, "Connected.");
            // The schema step already probed the server. Report the effective mode; the JSON fallback is development only.
            if (vector.UseNative) Add("Vector support", true, "Native VECTOR type available.");
            else Add("Vector support", deployment.Mode != DeploymentMode.Azure, "JSON fallback in use (development only): " + vector.Reason);
        }
        catch (Exception e) { Add("Database", false, e.Message); }

        try
        {
            Directory.CreateDirectory(storage.ResolvedFolder);
            var probe = Path.Combine(storage.ResolvedFolder, ".write-test");
            await File.WriteAllTextAsync(probe, "ok", ct);
            File.Delete(probe);
            Add("Document store", true, "Writable.");
        }
        catch (Exception e) { Add("Document store", false, e.Message); }

        if (options.ProbeModels && !profile.Provider.Equals("Stub", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var role in Enum.GetValues<ModelRole>())
            {
                try { await llm.CompleteAsync(role, "Reply with the word ok.", "ping", ct); Add($"Model {role}", true, profile.Models.GetValueOrDefault(role.ToString()) ?? "?"); }
                catch (Exception e) { Add($"Model {role}", false, e.Message); }
            }
            try { var v = await embeddings.EmbedAsync(new[] { "ping" }, ct); Add("Embedding model", v[0].Length == embeddings.Dimensions, $"{v[0].Length} dimensions"); }
            catch (Exception e) { Add("Embedding model", false, e.Message); }
        }
        return results;
    }
}
