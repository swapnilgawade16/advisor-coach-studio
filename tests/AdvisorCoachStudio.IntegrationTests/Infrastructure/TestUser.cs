using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>An HTTP response captured for assertions (status, body, headers, parsed JSON).</summary>
public sealed class ApiResponse
{
    private JsonElement? _json;

    public required int StatusCode { get; init; }

    public required string Body { get; init; }

    public required HttpResponseHeaders Headers { get; init; }

    public string? ContentType { get; init; }

    public JsonElement Json => _json ??= Parse(Body);

    /// <summary>Stable problem code from a Problem Details body, if any.</summary>
    public string? ProblemCode => Body.Length > 0 && Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("code", out var code) ? code.GetString() : null;

    public bool IsNoStore => Headers.CacheControl?.NoStore == true;

    public string String(string name) => Json.GetProperty(name).GetString()!;

    /// <summary>Problem body without per-request identifiers, for comparing response shapes.</summary>
    public string NormalizedProblem()
    {
        var node = JsonNode.Parse(Body)!.AsObject();
        node.Remove("correlationId");
        node.Remove("traceId");
        node.Remove("instance");
        return node.ToJsonString();
    }

    public static async Task<ApiResponse> ReadAsync(HttpResponseMessage response) => new()
    {
        StatusCode = (int)response.StatusCode,
        Body = await response.Content.ReadAsStringAsync(),
        Headers = response.Headers,
        ContentType = response.Content.Headers.ContentType?.MediaType,
    };

    public override string ToString() => $"HTTP {StatusCode}: {Body}";

    private static JsonElement Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// A browser-like user: its own cookie jar, the antiforgery token flow used by the pages, JSON API helpers, and
/// WebSocket connections that carry the access cookie and Origin like a browser would.
/// </summary>
public sealed partial class TestUser : IDisposable
{
    public const string BaseAddress = "https://localhost";
    public const string Subprotocol = "acs.v1";
    private static readonly Uri BaseUri = new(BaseAddress);

    private readonly TestApp _app;
    private readonly CookieContainerHandler _cookies = new();

    internal TestUser(TestApp app)
    {
        _app = app;
        Http = app.Factory.CreateDefaultClient(BaseUri, _cookies);
    }

    public HttpClient Http { get; }

    /// <summary>Antiforgery request token for the current identity (sent as X-CSRF-TOKEN).</summary>
    public string? CsrfToken { get; private set; }

    public string CookieHeader => _cookies.Container.GetCookieHeader(BaseUri);

    /// <summary>GET /access, validate the shared code with the anonymous token, then fetch the authenticated token from /.</summary>
    public async Task LoginAsync(string code = TestAppFactory.AccessCode)
    {
        var anonymousToken = await FetchCsrfTokenAsync("/access");
        var response = await PostAsync("/api/access/validate", new { code }, anonymousToken);
        if (response.StatusCode != 200)
        {
            throw new InvalidOperationException($"Login failed: {response}");
        }

        CsrfToken = await FetchCsrfTokenAsync("/");
    }

    public async Task<string> FetchCsrfTokenAsync(string path)
    {
        using var response = await Http.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"GET {path} returned {(int)response.StatusCode}; cannot read the antiforgery token.");
        }

        var match = CsrfMetaTag().Match(html);
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups[1].Value)
            : throw new InvalidOperationException($"GET {path} did not render a csrf-token meta tag.");
    }

    public Task<ApiResponse> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null, null);

    public Task<ApiResponse> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body, CsrfToken);

    public Task<ApiResponse> PostAsync(string path, object? body, string? csrfToken) => SendAsync(HttpMethod.Post, path, body, csrfToken);

    public Task<ApiResponse> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null, CsrfToken);

    public async Task<ApiResponse> SendAsync(HttpMethod method, string path, object? body, string? csrfToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (csrfToken is not null)
        {
            request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        }

        using var response = await Http.SendAsync(request);
        return await ApiResponse.ReadAsync(response);
    }

    public async Task<Guid> CreateSessionAsync(string scenarioId = "market-decline", string difficulty = "intermediate")
    {
        var response = await PostAsync("/api/sessions", new { scenarioId, difficulty, acknowledgedNotice = true });
        return response.StatusCode == 201
            ? response.Json.GetProperty("sessionId").GetGuid()
            : throw new InvalidOperationException($"Session creation failed: {response}");
    }

    public Task<ApiResponse> GetSessionAsync(Guid sessionId) => GetAsync($"/api/sessions/{sessionId}");

    public Task<ApiResponse> GetTranscriptAsync(Guid sessionId) => GetAsync($"/api/sessions/{sessionId}/transcript");

    public Task<ApiResponse> GetReportAsync(Guid sessionId) => GetAsync($"/api/sessions/{sessionId}/report");

    /// <summary>Polls the report endpoint until it reports <paramref name="status"/>.</summary>
    public async Task<ApiResponse> WaitForReportStatusAsync(Guid sessionId, string status, TimeSpan? timeout = null)
    {
        ApiResponse? last = null;
        try
        {
            await Wait.UntilAsync(
                async () =>
                {
                    last = await GetReportAsync(sessionId);
                    return last.StatusCode == 200 && last.String("status") == status;
                },
                $"report status '{status}'",
                timeout);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException($"{ex.Message} Last response: {last}{Environment.NewLine}{_app.Diagnostics()}", ex);
        }

        return last!;
    }

    public void Dispose() => Http.Dispose();

    [GeneratedRegex("<meta\\s+name=\"csrf-token\"\\s+content=\"([^\"]*)\"")]
    private static partial Regex CsrfMetaTag();
}
