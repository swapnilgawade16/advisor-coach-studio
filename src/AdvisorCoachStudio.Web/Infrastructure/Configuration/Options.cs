using AdvisorCoachStudio.Web.Domain;

namespace AdvisorCoachStudio.Web.Infrastructure.Configuration;

public sealed class AiOptions
{
    public const string Section = "Ai";
    public AiMode Mode { get; set; } = AiMode.Unconfigured;
}

public sealed class AccessGateOptions
{
    public const string Section = "AccessGate";

    /// <summary>Shared access code. Secret: supply through user-secrets, environment variables, or Key Vault.</summary>
    public string? SharedCode { get; set; }

    /// <summary>Changing this value invalidates every previously issued access cookie.</summary>
    public string CodeVersion { get; set; } = "1";

    public int CookieHours { get; set; } = 8;
}

public enum AzureAuthenticationMode
{
    ApiKey,
    EntraId,
}

public abstract class AzureEndpointOptions
{
    /// <summary>HTTPS Azure resource root, for example https://my-resource.openai.azure.com.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Azure deployment name (may differ from the model family name).</summary>
    public string? Deployment { get; set; }

    /// <summary>Explicit choice; there is deliberately no default.</summary>
    public AzureAuthenticationMode? Authentication { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>Entra token scope. Microsoft's current quickstarts use https://ai.azure.com/.default.</summary>
    public string TokenScope { get; set; } = "https://ai.azure.com/.default";

    /// <summary>Returns a list of missing/invalid setting names. Never includes secret values.</summary>
    public virtual List<string> Validate()
    {
        var problems = new List<string>();
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            problems.Add("Endpoint (absolute https URL)");
        }
        else if (uri.AbsolutePath.Trim('/').Length > 0 || !string.IsNullOrEmpty(uri.Query))
        {
            problems.Add("Endpoint (resource root only, without a path or query)");
        }

        if (string.IsNullOrWhiteSpace(Deployment))
        {
            problems.Add("Deployment");
        }

        switch (Authentication)
        {
            case null:
                problems.Add("Authentication (ApiKey or EntraId)");
                break;
            case AzureAuthenticationMode.ApiKey when string.IsNullOrWhiteSpace(ApiKey):
                problems.Add("ApiKey");
                break;
        }

        return problems;
    }
}

public sealed class AzureRealtimeOptions : AzureEndpointOptions
{
    public const string Section = "AzureRealtime";

    /// <summary>Input transcription model or Azure transcription deployment name, for example whisper-1.</summary>
    public string? TranscriptionModel { get; set; }

    /// <summary>Optional output voice, for example alloy. When empty the provider default voice is used.</summary>
    public string? Voice { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public override List<string> Validate()
    {
        var problems = base.Validate();
        if (string.IsNullOrWhiteSpace(TranscriptionModel))
        {
            problems.Add("TranscriptionModel");
        }

        return problems;
    }
}

public sealed class AzureReportsOptions : AzureEndpointOptions
{
    public const string Section = "AzureReports";

    /// <summary>Accepted reasoning_effort values; which ones a deployment supports depends on its model.</summary>
    public static readonly IReadOnlyList<string> ReasoningEfforts = ["none", "minimal", "low", "medium", "high", "xhigh"];

    public int TimeoutSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Output token cap per request. For reasoning models this includes reasoning tokens.</summary>
    public int MaxOutputTokens { get; set; } = 4000;

    /// <summary>
    /// Optional reasoning_effort for reasoning-model deployments such as gpt-5.1. Leave empty for other models, which
    /// reject the parameter. When empty the parameter is not sent and the model's default applies.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    public override List<string> Validate()
    {
        var problems = base.Validate();
        if (!string.IsNullOrWhiteSpace(ReasoningEffort) && !ReasoningEfforts.Contains(ReasoningEffort.Trim(), StringComparer.Ordinal))
        {
            problems.Add($"ReasoningEffort ({string.Join(", ", ReasoningEfforts)}, or empty)");
        }

        return problems;
    }
}

public sealed class RealtimeOptions
{
    public const string Section = "Realtime";
    public int MaxSessionMinutes { get; set; } = 2;
    public int MaxConcurrentSessions { get; set; } = 5;
    public double VadThreshold { get; set; } = 0.5;
    public int VadPrefixPaddingMs { get; set; } = 300;
    public int VadSilenceDurationMs { get; set; } = 500;
    public int FinalizationDrainSeconds { get; set; } = 5;
    public int MaxQueuedInputMs { get; set; } = 1000;
    public int MaxQueuedOutputMs { get; set; } = 2000;
    public int MaxBufferedResponseSeconds { get; set; } = 60;
    public int DemonstrationMaxSeconds { get; set; } = 90;

    /// <summary>Created sessions that never connect are expired after this many minutes.</summary>
    public int UnstartedSessionMinutes { get; set; } = 10;
}

public sealed class DemonstrationOptions
{
    public const string Section = "Demonstration";
    public bool Enabled { get; set; } = true;
}

public sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>Exact allowed browser origins for WebSocket upgrades. Empty means "same origin as the request".</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Enable forwarded headers only behind trusted proxies (for example Azure App Service front ends).</summary>
    public bool UseForwardedHeaders { get; set; }

    public string[] KnownProxies { get; set; } = [];
}
