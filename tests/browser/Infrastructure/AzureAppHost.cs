using System.Text.Json.Nodes;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.AzureReports;
using AdvisorCoachStudio.Web.Infrastructure.Simulation;
using AdvisorCoachStudio.Web.Prompts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>
/// Host flavour (b): Ai:Mode=Azure with valid-looking (fake) Azure settings, so the application behaves exactly as a
/// configured deployment (no simulation labels). The realtime provider is the product's in-memory simulator and the
/// report model is <see cref="ScriptedReportModelClient"/>, which the tests complete, fail, or hold open.
/// No request ever leaves the machine.
/// </summary>
public sealed class AzureAppHost : AppHost
{
    private const string FakeEndpoint = "https://acs-browser-tests.openai.azure.com";

    public ScriptedReportModelClient Reports { get; } = new();

    protected override IEnumerable<KeyValuePair<string, string?>> Settings() =>
    [
        new("Ai:Mode", "Azure"),
        new("AzureRealtime:Endpoint", FakeEndpoint),
        new("AzureRealtime:Deployment", "browser-test-realtime"),
        new("AzureRealtime:Authentication", "ApiKey"),
        new("AzureRealtime:ApiKey", "not-a-real-key"),
        new("AzureRealtime:TranscriptionModel", "browser-test-transcribe"),
        new("AzureReports:Endpoint", FakeEndpoint),
        new("AzureReports:Deployment", "browser-test-reports"),
        new("AzureReports:Authentication", "ApiKey"),
        new("AzureReports:ApiKey", "not-a-real-key"),

        // One attempt per job keeps scripted failures deterministic (no automatic retry with back-off).
        new("AzureReports:MaxAttempts", "1"),
    ];

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<IRealtimeProviderConnector>();
        services.AddSingleton<IRealtimeProviderConnector, SimulatedRealtimeConnector>();
        services.RemoveAll<IReportModelClient>();
        services.AddSingleton<IReportModelClient>(Reports);
    }

    /// <summary>
    /// Creates an ended session for an unrelated owner (directly through the in-memory store) whose report job reaches
    /// the scripted model and waits there. Two of these occupy both report worker slots, which keeps the next job queued.
    /// </summary>
    public BackgroundSession QueueBackgroundReport(string advisorText)
    {
        var store = Services.GetRequiredService<PracticeSessionStore>();
        var catalog = Services.GetRequiredService<ScenarioCatalog>();
        if (!catalog.TryGet("market-decline", out var scenario))
        {
            throw new InvalidOperationException("The market-decline scenario is missing.");
        }

        var owner = new AccessOwner(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(1));
        var session = store.Create(owner, scenario, Difficulty.Intermediate, RoleplayPromptBuilder.Version, simulated: false);
        var now = DateTime.UtcNow;
        var transitioned =
            session.TryTransition(ConversationState.Connecting, now) &&
            session.TryTransition(ConversationState.Active, now) &&
            session.TryTransition(ConversationState.Ending, now, SessionEndReason.UserEnded) &&
            session.TryTransition(ConversationState.TranscriptFinalizing, now);
        var turn = new TranscriptTurn
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            ProviderItemId = "item_background",
            Sequence = 1,
            Speaker = TurnSpeaker.Advisor,
            Text = advisorText,
            Status = TurnStatus.Final,
            Source = TurnSource.InputTranscription,
            CreatedAtUtc = now,
        };
        if (!transitioned || !session.FreezeTranscript([turn], complete: true) ||
            !session.TryTransition(ConversationState.Ended, now, SessionEndReason.UserEnded) || !session.TryQueueReport())
        {
            throw new InvalidOperationException("Could not prepare the background session.");
        }

        Services.GetRequiredService<ReportQueue>().Enqueue(session.Id);
        return new BackgroundSession(session.Id, owner.OwnerId);
    }

    /// <summary>Discards a background session; its in-flight report call is cancelled and its worker slot released.</summary>
    public void Discard(BackgroundSession session) =>
        Services.GetRequiredService<PracticeSessionStore>().Discard(session.SessionId, session.OwnerId);
}

public sealed record BackgroundSession(Guid SessionId, Guid OwnerId);
