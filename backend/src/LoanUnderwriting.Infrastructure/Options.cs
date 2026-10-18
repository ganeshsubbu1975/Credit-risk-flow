namespace LoanUnderwriting.Infrastructure;

public enum DeploymentMode { OnPrem, Azure }

public sealed class DeploymentOptions
{
    public DeploymentMode Mode { get; set; } = DeploymentMode.OnPrem;
}

public sealed class DatabaseOptions
{
    public string ConnectionString { get; set; } = "";
    /// <summary>Native = SQL Server 2025 / Azure SQL VECTOR type. JsonFallback = embeddings as JSON, cosine in C# (older SQL Server, dev only).</summary>
    public string VectorProvider { get; set; } = "Native";
    public bool AutoCreateSchema { get; set; } = true;
}

public sealed class StorageOptions
{
    public string DocumentFolder { get; set; } = "data/documents";
    public int MaxUploadMb { get; set; } = 20;
    /// <summary>Relative folders resolve against the application folder (a Windows Service starts in System32). Use an absolute path in production.</summary>
    public string ResolvedFolder => Path.IsPathRooted(DocumentFolder) ? DocumentFolder : Path.Combine(AppContext.BaseDirectory, DocumentFolder);
}

/// <summary>One endpoint profile. OnPrem points at Foundry Local; Azure at a Microsoft Foundry deployment endpoint.</summary>
public sealed class ModelProfile
{
    /// <summary>Stub = offline rules-based test double (demo and tests only). OpenAiCompatible = real models.</summary>
    public string Provider { get; set; } = "OpenAiCompatible";
    public string BaseUrl { get; set; } = "";
    /// <summary>None | ApiKey | AzureAd</summary>
    public string AuthMode { get; set; } = "None";
    public string? ApiKey { get; set; }
    public string AzureScope { get; set; } = "https://cognitiveservices.azure.com/.default";
    public int TimeoutSeconds { get; set; } = 120;
    public Dictionary<string, string> Models { get; set; } = new();
    public string EmbeddingModel { get; set; } = "";
    public int EmbeddingDimensions { get; set; } = 1536;
}

public sealed class ModelOptions
{
    public ModelProfile OnPrem { get; set; } = new();
    public ModelProfile Azure { get; set; } = new();
    public ModelProfile Active(DeploymentMode mode) => mode == DeploymentMode.Azure ? Azure : OnPrem;
}

public sealed class WorkflowOptions
{
    public int MaxConcurrentRuns { get; set; } = 2;
    public int ExtractionRetries { get; set; } = 1;
}

public sealed class SelfCheckOptions
{
    public bool Enabled { get; set; } = true;
    public bool ProbeModels { get; set; } = true;
}
