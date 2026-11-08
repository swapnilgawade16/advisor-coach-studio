using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdvisorCoachStudio.UnitTests.Coaching;

/// <summary>Request shape, configuration validation, and failures before any HTTP request is sent.</summary>
public sealed class AzureChatReportClientTests
{
    [Fact]
    public async Task EntraTokenFailureIsReportedAsAuthenticationFailure()
    {
        var credential = new FailingCredential(new InvalidOperationException("Synthetic token failure."));
        var client = Client(EntraOptions(), credential);

        var error = await Assert.ThrowsAsync<ReportProviderException>(() => Complete(client));

        Assert.Equal(ReportFailure.Authentication, error.Failure);
        Assert.False(error.IsTransient);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public async Task IncompleteConfigurationFailsBeforeRequestingAToken()
    {
        var credential = new FailingCredential(new InvalidOperationException("Synthetic token failure."));
        var options = EntraOptions();
        options.Authentication = null;

        var error = await Assert.ThrowsAsync<ReportProviderException>(() => Complete(Client(options, credential)));

        Assert.Equal(ReportFailure.NotConfigured, error.Failure);
        Assert.Equal(0, credential.Calls);
    }

    [Fact]
    public void DeploymentNameComesFromConfiguration() =>
        Assert.Equal("synthetic-reports", Client(EntraOptions(), new FailingCredential(new InvalidOperationException())).DeploymentName);

    [Fact]
    public async Task RequestUsesStrictJsonSchemaAndOmitsReasoningEffortByDefault()
    {
        var handler = new CapturingHandler(CompletionBody);

        var result = await Complete(Client(ApiKeyOptions(), handler));

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://res.openai.azure.com/openai/v1/chat/completions", request.Uri);
        Assert.Equal("synthetic-key", request.ApiKey);
        Assert.Equal("synthetic-reports", request.Body["model"]!.GetValue<string>());
        Assert.Equal("json_schema", request.Body["response_format"]!["type"]!.GetValue<string>());
        Assert.True(request.Body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.Equal(4000, request.Body["max_completion_tokens"]!.GetValue<int>());
        Assert.False(request.Body.ContainsKey("reasoning_effort"));
        Assert.False(request.Body.ContainsKey("temperature"));
        Assert.Equal(("{}", "stop", null), (result.Content, result.FinishReason, result.Refusal));
    }

    [Fact]
    public async Task ConfiguredReasoningEffortAndOutputLimitAreSent()
    {
        var handler = new CapturingHandler(CompletionBody);
        var options = ApiKeyOptions();
        options.ReasoningEffort = " low ";
        options.MaxOutputTokens = 16000;

        await Complete(Client(options, handler));

        var body = Assert.Single(handler.Requests).Body;
        Assert.Equal("low", body["reasoning_effort"]!.GetValue<string>());
        Assert.Equal(16000, body["max_completion_tokens"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("none", true)]
    [InlineData("low", true)]
    [InlineData("xhigh", true)]
    [InlineData("Low", false)]
    [InlineData("fast", false)]
    public void ReasoningEffortMustBeAKnownValue(string? effort, bool valid)
    {
        var options = ApiKeyOptions();
        options.ReasoningEffort = effort;

        var problems = options.Validate();

        Assert.Equal(valid, problems.Count == 0);
        Assert.All(problems, problem => Assert.StartsWith("ReasoningEffort", problem, StringComparison.Ordinal));
    }

    private const string CompletionBody = """
        {"choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"{}"}}],
         "usage":{"prompt_tokens":10,"completion_tokens":5,"completion_tokens_details":{"reasoning_tokens":2}}}
        """;

    private static AzureReportsOptions ApiKeyOptions() => new()
    {
        Endpoint = "https://res.openai.azure.com",
        Deployment = "synthetic-reports",
        Authentication = AzureAuthenticationMode.ApiKey,
        ApiKey = "synthetic-key",
    };

    private static AzureChatReportClient Client(AzureReportsOptions options, CapturingHandler handler) => new(
        new SingleClientFactory(new HttpClient(handler)),
        new TestOptionsMonitor<AzureReportsOptions>(options),
        new AzureTokenProvider(new FailingCredential(new InvalidOperationException("No token expected."))),
        NullLogger<AzureChatReportClient>.Instance);

    private sealed record CapturedRequest(string Uri, string? ApiKey, JsonObject Body);

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString(), request.Headers.TryGetValues("api-key", out var keys) ? keys.Single() : null, body));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static Task<ReportModelResult> Complete(AzureChatReportClient client) =>
        client.CompleteAsync([new ReportChatMessage("user", "Synthetic request.")], new JsonObject(), TestContext.Current.CancellationToken);

    private static AzureReportsOptions EntraOptions() => new()
    {
        Endpoint = "https://res.openai.azure.com",
        Deployment = "synthetic-reports",
        Authentication = AzureAuthenticationMode.EntraId,
    };

    private static AzureChatReportClient Client(AzureReportsOptions options, FailingCredential credential) => new(
        new UnusedHttpClientFactory(),
        new TestOptionsMonitor<AzureReportsOptions>(options),
        new AzureTokenProvider(credential),
        NullLogger<AzureChatReportClient>.Instance);
}
