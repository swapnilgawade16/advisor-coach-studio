using AdvisorCoachStudio.IntegrationTests.Infrastructure;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Sessions;

namespace AdvisorCoachStudio.IntegrationTests;

/// <summary>Discarding a session (explicitly or by starting a new one) stops live work and is never repopulated (spec 13.4, 15.4).</summary>
public sealed class DiscardTests
{
    [Fact]
    public async Task DiscardDuringReportGenerationStoresNothingEvenWhenTheModelRepliesLate()
    {
        await using var app = await TestApp.StartAsync();
        var gate = app.Reports.EnqueueGate(ignoreCancellation: true);
        using var user = await app.LoginAsync();
        await using var roleplay = await LiveRoleplay.StartAsync(app, user);
        await roleplay.PlayConversationAsync(exchanges: 1);
        await roleplay.EndAsync();

        var call = await Wait.ForAsync(gate.Started, "the report model call");
        Assert.Equal("generating", (await user.GetReportAsync(roleplay.SessionId)).String("status"));
        var store = app.Service<PracticeSessionStore>();
        var session = store.Get(roleplay.SessionId)!;

        var discard = await user.DeleteAsync($"/api/sessions/{roleplay.SessionId}");
        Assert.Equal(204, discard.StatusCode);

        // The provider reply arrives after the discard (it ignores cancellation): it must not be stored.
        gate.Release(ScriptedReportModelClient.Content(ReportContent.Valid(ReportContent.FromCall(call))));
        await app.Logs.WaitForAsync(
            entry => entry.Category.EndsWith("ReportWorker", StringComparison.Ordinal) && entry.Message.Contains("was not stored", StringComparison.Ordinal),
            "the report worker to refuse storing the late report");

        Assert.True(session.IsDiscarded);
        Assert.Null(session.Report);
        Assert.NotEqual(ReportStatus.Ready, session.ReportStatus);
        Assert.Empty(session.Turns());
        Assert.Null(store.Get(roleplay.SessionId));
        Assert.Equal(0, store.Count);
        Assert.Equal(404, (await user.GetSessionAsync(roleplay.SessionId)).StatusCode);
        Assert.Equal(404, (await user.GetReportAsync(roleplay.SessionId)).StatusCode);
        Assert.Equal(404, (await user.GetTranscriptAsync(roleplay.SessionId)).StatusCode);
        Assert.Equal(404, (await user.DeleteAsync($"/api/sessions/{roleplay.SessionId}")).StatusCode);
        Assert.Single(app.Reports.Calls);
    }

    [Fact]
    public async Task StartingANewSessionDiscardsThePreviousOneAndClosesItsLiveSocket()
    {
        await using var app = await TestApp.StartAsync();
        using var user = await app.LoginAsync();
        await using var first = await LiveRoleplay.StartAsync(app, user);
        await first.HearAsync(Scripts.Opening);

        var replacement = await user.CreateSessionAsync("fees-and-trust", "advanced");

        var discarded = await first.Socket.WaitForAsync("error");
        Assert.Equal("session_discarded", discarded.String("code"));
        await first.Socket.WaitForClosedAsync();
        await first.Provider.WaitForClosedAsync();
        Assert.DoesNotContain(first.Socket.Messages, m => m.Type == "session.ended");
        Assert.Equal(404, (await user.GetSessionAsync(first.SessionId)).StatusCode);
        Assert.Equal(404, (await user.GetTranscriptAsync(first.SessionId)).StatusCode);
        Assert.Null(app.Service<PracticeSessionStore>().Get(first.SessionId));

        var current = await user.GetSessionAsync(replacement);
        Assert.Equal(200, current.StatusCode);
        Assert.Equal("created", current.String("state"));
        Assert.Equal("fees-and-trust", current.String("scenarioId"));

        // The released slot lets the new session connect right away.
        await using var second = await LiveRoleplay.ConnectAsync(app, user, replacement);
        Assert.Equal(1, second.Provider.Index);
        await second.HearAsync(Scripts.Opening);
        Assert.Equal(0, app.Reports.CallCount);
    }
}
