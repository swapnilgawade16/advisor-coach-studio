using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AdvisorCoachStudio.Web.Infrastructure.AzureReports;

public sealed record ReportChatMessage(string Role, string Content);

public sealed record ReportModelResult(string? Content, string? FinishReason, string? Refusal);

public enum ReportFailure
{
    NotConfigured,
    Authentication,
    Throttled,
    Transient,
    BadRequest,
    ContentFilter,
}

public sealed class ReportProviderException(ReportFailure failure, string message, TimeSpan? retryAfter = null, Exception? inner = null) : Exception(message, inner)
{
    public ReportFailure Failure { get; } = failure;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public bool IsTransient => Failure is ReportFailure.Throttled or ReportFailure.Transient;
}

/// <summary>Text model used for structured coaching reports (separate from the realtime deployment).</summary>
public interface IReportModelClient
{
    string DeploymentName { get; }

    Task<ReportModelResult> CompleteAsync(IReadOnlyList<ReportChatMessage> messages, JsonObject schema, CancellationToken cancellationToken);
}

/// <summary>
/// Azure OpenAI v1 chat completions with strict JSON-schema structured output:
/// POST {endpoint}/openai/v1/chat/completions (model = deployment name, no api-version).
/// </summary>
public sealed class AzureChatReportClient(IHttpClientFactory httpFactory, IOptionsMonitor<AzureReportsOptions> options, AzureTokenProvider tokens, ILogger<AzureChatReportClient> logger)
    : IReportModelClient
{
    public const string HttpClientName = "azure-reports";

    public string DeploymentName => options.CurrentValue.Deployment ?? "";

    public async Task<ReportModelResult> CompleteAsync(IReadOnlyList<ReportChatMessage> messages, JsonObject schema, CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (current.Validate().Count > 0)
        {
            throw new ReportProviderException(ReportFailure.NotConfigured, "Report configuration is incomplete.");
        }

        var body = new JsonObject
        {
            ["model"] = current.Deployment,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "coaching_report",
                    ["strict"] = true,
                    ["schema"] = schema.DeepClone(),
                },
            },
            ["max_completion_tokens"] = current.MaxOutputTokens,
        };
        if (!string.IsNullOrWhiteSpace(current.ReasoningEffort))
        {
            body["reasoning_effort"] = current.ReasoningEffort.Trim();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(current.Endpoint!.TrimEnd('/') + "/"), "openai/v1/chat/completions"))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (current.Authentication == AzureAuthenticationMode.ApiKey)
        {
            request.Headers.Add("api-key", current.ApiKey);
        }
        else
        {
            string token;
            try
            {
                token = await tokens.GetTokenAsync(current.TokenScope, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Entra token acquisition for reports failed: {Error}.", ex.GetType().Name);
                throw new ReportProviderException(ReportFailure.Authentication, "Could not obtain a token for the report service.", inner: ex);
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(current.TimeoutSeconds, 10, 300)));
        var started = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            var http = httpFactory.CreateClient(HttpClientName);
            response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ReportProviderException(ReportFailure.Transient, "The report request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new ReportProviderException(ReportFailure.Transient, "The report service could not be reached.", inner: ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw MapError(response, text);
            }

            using var document = JsonDocument.Parse(text);
            var choice = document.RootElement.GetProperty("choices")[0];
            var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            var message = choice.GetProperty("message");
            var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var refusal = message.TryGetProperty("refusal", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            LogUsage(document.RootElement, current.Deployment!, finish, Stopwatch.GetElapsedTime(started));
            return new ReportModelResult(content, finish, refusal);
        }
    }

    /// <summary>Metadata only (timing and token counts), so cost and latency can be tuned without logging content.</summary>
    private void LogUsage(JsonElement root, string deployment, string? finishReason, TimeSpan elapsed)
    {
        var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var details = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        logger.LogInformation(
            "Report completion from {Deployment} in {ElapsedMs} ms: finish {FinishReason}, {PromptTokens} prompt and {CompletionTokens} completion tokens ({ReasoningTokens} reasoning).",
            deployment, (long)elapsed.TotalMilliseconds, finishReason ?? "unknown", ReadInt(usage, "prompt_tokens"), ReadInt(usage, "completion_tokens"), ReadInt(details, "reasoning_tokens"));
    }

    private static int? ReadInt(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    private ReportProviderException MapError(HttpResponseMessage response, string body)
    {
        var code = TryReadErrorCode(body);
        logger.LogWarning("Report request failed with HTTP {Status} ({Code}).", (int)response.StatusCode, code ?? "none");
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ReportProviderException(ReportFailure.Authentication, "The report service rejected the credentials."),
            HttpStatusCode.TooManyRequests => new ReportProviderException(ReportFailure.Throttled, "The report service is busy.", RetryAfter(response)),
            HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError => new ReportProviderException(ReportFailure.Transient, "The report service is temporarily unavailable.", RetryAfter(response)),
            _ when code is "content_filter" or "ResponsibleAIPolicyViolation" => new ReportProviderException(ReportFailure.ContentFilter, "The request was blocked by content filtering."),
            _ => new ReportProviderException(ReportFailure.BadRequest, "The report request was rejected."),
        };
    }

    private static string? TryReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Honors Azure's retry-after-ms header, then the standard Retry-After header.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("retry-after-ms", out var values) &&
            double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        return response.Headers.RetryAfter?.Delta;
    }
}
