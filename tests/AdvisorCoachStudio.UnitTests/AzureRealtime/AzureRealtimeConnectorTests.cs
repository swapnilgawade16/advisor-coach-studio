using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace AdvisorCoachStudio.UnitTests.AzureRealtime;

public sealed class AzureRealtimeConnectorTests
{
    [Fact]
    public void BuildUriTargetsTheGaRealtimeEndpointWithoutApiVersion()
    {
        var uri = AzureRealtimeConnector.BuildUri("https://res.openai.azure.com", "my deployment");

        Assert.Equal("wss://res.openai.azure.com/openai/v1/realtime?model=my%20deployment", uri.AbsoluteUri);
        Assert.Equal("wss", uri.Scheme);
        Assert.DoesNotContain("api-version", uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://res.openai.azure.com/")]
    [InlineData("https://RES.openai.azure.com")]
    [InlineData("https://res.openai.azure.com:443")]
    public void BuildUriNormalizesTheResourceRoot(string endpoint) =>
        Assert.Equal("wss://res.openai.azure.com/openai/v1/realtime?model=deployment", AzureRealtimeConnector.BuildUri(endpoint, "deployment").AbsoluteUri);

    [Fact]
    public void BuildUriKeepsANonDefaultPort() =>
        Assert.Equal("wss://localhost:8443/openai/v1/realtime?model=deployment", AzureRealtimeConnector.BuildUri("https://localhost:8443", "deployment").AbsoluteUri);

    [Fact]
    public void BuildUriEscapesTheDeploymentSoItCannotAddQueryParameters()
    {
        var uri = AzureRealtimeConnector.BuildUri("https://res.openai.azure.com", "dep&api-version=2024-10-01");

        var query = QueryHelpers.ParseQuery(uri.Query);

        var model = Assert.Single(query);
        Assert.Equal("model", model.Key);
        Assert.Equal("dep&api-version=2024-10-01", model.Value.ToString());
    }

    [Theory]
    [InlineData("http://res.openai.azure.com")]
    [InlineData("ws://res.openai.azure.com")]
    public void BuildUriRejectsNonHttpsEndpoints(string endpoint)
    {
        var error = Assert.Throws<ProviderConnectException>(() => AzureRealtimeConnector.BuildUri(endpoint, "deployment"));

        Assert.Equal(ProviderConnectFailure.NotConfigured, error.Failure);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task EntraTokenFailureIsReportedAsAuthenticationFailure()
    {
        var credential = new FailingCredential(new InvalidOperationException("Synthetic token failure."));
        var connector = Connector(EntraOptions(), credential);

        var error = await Assert.ThrowsAsync<ProviderConnectException>(() => connector.ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ProviderConnectFailure.Authentication, error.Failure);
        Assert.False(error.IsRetryable);
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(1, credential.Calls);
    }

    [Fact]
    public async Task CancelledTokenAcquisitionIsNotReportedAsAuthenticationFailure()
    {
        var connector = Connector(EntraOptions(), new FailingCredential(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connector.ConnectAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task IncompleteConfigurationFailsBeforeRequestingAToken()
    {
        var credential = new FailingCredential(new InvalidOperationException("Synthetic token failure."));
        var options = EntraOptions();
        options.Deployment = " ";

        var error = await Assert.ThrowsAsync<ProviderConnectException>(() => Connector(options, credential).ConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ProviderConnectFailure.NotConfigured, error.Failure);
        Assert.Equal(0, credential.Calls);
    }

    private static AzureRealtimeOptions EntraOptions() => new()
    {
        Endpoint = "https://res.openai.azure.com",
        Deployment = "synthetic-realtime",
        Authentication = AzureAuthenticationMode.EntraId,
        TranscriptionModel = "whisper-1",
    };

    private static AzureRealtimeConnector Connector(AzureRealtimeOptions options, FailingCredential credential) =>
        new(new TestOptionsMonitor<AzureRealtimeOptions>(options), new AzureTokenProvider(credential), NullLogger<AzureRealtimeConnector>.Instance);
}
