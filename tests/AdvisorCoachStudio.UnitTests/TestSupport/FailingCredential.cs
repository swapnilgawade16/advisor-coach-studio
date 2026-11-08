using Azure.Core;

namespace AdvisorCoachStudio.UnitTests.TestSupport;

/// <summary>Entra credential double whose token acquisition always fails with the supplied exception.</summary>
internal sealed class FailingCredential(Exception error) : TokenCredential
{
    public int Calls { get; private set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Calls++;
        throw error;
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        Calls++;
        return ValueTask.FromException<AccessToken>(error);
    }
}

/// <summary>HTTP client factory for paths that must fail before any HTTP request is attempted.</summary>
internal sealed class UnusedHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP request was expected.");
}
