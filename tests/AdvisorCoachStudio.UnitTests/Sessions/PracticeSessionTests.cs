using AdvisorCoachStudio.UnitTests.TestSupport;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Sessions;
using AdvisorCoachStudio.Web.Prompts;

namespace AdvisorCoachStudio.UnitTests.Sessions;

public sealed class PracticeSessionTests
{
    private static readonly DateTime T0 = TestTranscripts.BaseTimeUtc;

    [Fact]
    public void TryTransitionFollowsTheLifecycleAndRecordsTimestamps()
    {
        var session = CreateSession();

        Assert.False(session.TryTransition(ConversationState.Active, T0));
        Assert.True(session.TryTransition(ConversationState.Connecting, T0));
        Assert.True(session.TryTransition(ConversationState.Active, T0.AddSeconds(2)));
        Assert.False(session.TryTransition(ConversationState.Ended, T0.AddSeconds(3)));
        Assert.True(session.TryTransition(ConversationState.Ending, T0.AddSeconds(60), SessionEndReason.UserEnded));
        Assert.True(session.TryTransition(ConversationState.TranscriptFinalizing, T0.AddSeconds(61), SessionEndReason.Disconnected));
        Assert.True(session.TryTransition(ConversationState.Ended, T0.AddSeconds(62)));

        var snapshot = session.Snapshot();
        Assert.Equal(ConversationState.Ended, snapshot.State);
        Assert.Equal(SessionEndReason.UserEnded, snapshot.EndReason);
        Assert.Equal(T0.AddSeconds(2), snapshot.StartedAtUtc);
        Assert.Equal(T0.AddSeconds(62), snapshot.EndedAtUtc);
    }

    [Fact]
    public void DuplicateEndedTransitionIsANoOp()
    {
        var session = EndedSession();
        var before = session.Snapshot();

        Assert.False(session.TryTransition(ConversationState.Ended, T0.AddMinutes(5), SessionEndReason.DurationLimit));

        Assert.Equal(before, session.Snapshot());
    }

    [Fact]
    public void FailureCodeIsRecordedOnFailedConnection()
    {
        var session = CreateSession();
        Assert.True(session.TryTransition(ConversationState.Connecting, T0));

        Assert.True(session.TryTransition(ConversationState.Failed, T0.AddSeconds(1), SessionEndReason.ProviderError, "provider_unavailable"));

        var snapshot = session.Snapshot();
        Assert.Equal("provider_unavailable", snapshot.FailureCode);
        Assert.Equal(T0.AddSeconds(1), snapshot.EndedAtUtc);
        Assert.Null(snapshot.StartedAtUtc);
    }

    [Fact]
    public void FreezeTranscriptOnlyTakesEffectOnce()
    {
        var session = EndedSession();
        var first = TestTranscripts.Conversation(session.Id);
        var second = new[] { TestTranscripts.Turn(session.Id, 1, TurnSpeaker.Advisor, "A different, later transcript.") };

        Assert.True(session.FreezeTranscript(first, complete: true));
        var frozen = session.Snapshot();
        Assert.False(session.FreezeTranscript(second, complete: false));

        Assert.Equal(first, session.Turns());
        var after = session.Snapshot();
        Assert.Equal(1, after.TranscriptRevision);
        Assert.Equal(TranscriptStatus.Complete, after.TranscriptStatus);
        Assert.NotNull(after.TranscriptHash);
        Assert.Equal(frozen.TranscriptHash, after.TranscriptHash);
    }

    [Fact]
    public void RecordLiveTurnReplacesTheSameItemAndIsIgnoredAfterFreeze()
    {
        var session = CreateSession();
        var partial = TestTranscripts.Turn(session.Id, 1, TurnSpeaker.Advisor, "I can hear", TurnStatus.Pending);
        var final = partial with { Text = TestTranscripts.T2Advisor, Status = TurnStatus.Final };

        session.RecordLiveTurn(partial);
        session.RecordLiveTurn(final);
        Assert.Equal(final, Assert.Single(session.Turns()));

        Assert.True(session.FreezeTranscript([final], complete: true));
        session.RecordLiveTurn(TestTranscripts.Turn(session.Id, 2, TurnSpeaker.Client, "A late client turn."));

        Assert.Equal(final, Assert.Single(session.Turns()));
    }

    [Fact]
    public void TryQueueReportRequiresAFrozenTranscript()
    {
        var session = EndedSession();

        Assert.False(session.TryQueueReport());
        Assert.Equal(ReportStatus.NotRequested, session.ReportStatus);
    }

    [Fact]
    public void TryQueueReportPreventsDuplicatesWhileQueuedGeneratingOrReady()
    {
        var session = FrozenSession();

        Assert.True(session.TryQueueReport());
        Assert.False(session.TryQueueReport());
        Assert.Equal(ReportStatus.Queued, session.ReportStatus);

        Assert.True(session.TryMarkReportGenerating(1));
        Assert.False(session.TryMarkReportGenerating(1));
        Assert.False(session.TryQueueReport());
        Assert.Equal(ReportStatus.Generating, session.ReportStatus);

        Assert.True(session.TryStoreReport(1, Report(session)));
        Assert.False(session.TryQueueReport());
        Assert.Equal(ReportStatus.Ready, session.ReportStatus);
        Assert.NotNull(session.Report);
    }

    [Fact]
    public void FailedReportCanBeQueuedAgainForTheSameRevision()
    {
        var session = FrozenSession();
        Assert.True(session.TryQueueReport());
        Assert.True(session.TryMarkReportGenerating(1));

        Assert.True(session.TryMarkReportFailed(1, "report_unavailable"));
        Assert.Equal("report_unavailable", session.Snapshot().ReportErrorCode);

        Assert.True(session.TryQueueReport(userRetry: true));
        Assert.Null(session.Snapshot().ReportErrorCode);
        Assert.Equal(ReportStatus.Queued, session.ReportStatus);
        Assert.Equal(2, session.ReportRequestCount);
    }

    [Theory]
    [InlineData("report_refused")]
    [InlineData("report_content_filtered")]
    public void UserRetryIsRefusedForPermanentReportFailures(string errorCode)
    {
        var session = FrozenSession();
        Assert.True(session.TryQueueReport());
        FailCurrentReport(session, errorCode);

        Assert.False(session.TryQueueReport(userRetry: true));

        Assert.False(PracticeSession.IsRetryableReportError(errorCode));
        Assert.Equal(ReportStatus.Failed, session.ReportStatus);
        Assert.Equal(errorCode, session.Snapshot().ReportErrorCode);
        Assert.Equal(1, session.ReportRequestCount);
    }

    [Fact]
    public void UserRetriesStopAfterTheMaximumNumberOfReportRequests()
    {
        var session = FrozenSession();
        Assert.True(session.TryQueueReport());
        for (var request = 2; request <= PracticeSession.MaxReportRequests; request++)
        {
            FailCurrentReport(session, "invalid_report_output");
            Assert.True(session.TryQueueReport(userRetry: true), $"Request {request} should be accepted.");
        }

        FailCurrentReport(session, "invalid_report_output");

        Assert.False(session.TryQueueReport(userRetry: true));
        Assert.Equal(PracticeSession.MaxReportRequests, session.ReportRequestCount);
        Assert.Equal(ReportStatus.Failed, session.ReportStatus);
    }

    [Fact]
    public void TryStoreReportRejectsAWrongRevision()
    {
        var session = FrozenSession();
        Assert.True(session.TryQueueReport());
        Assert.False(session.TryMarkReportGenerating(2));
        Assert.True(session.TryMarkReportGenerating(1));

        Assert.False(session.TryStoreReport(2, Report(session)));

        Assert.Equal(ReportStatus.Generating, session.ReportStatus);
        Assert.Null(session.Report);
    }

    [Fact]
    public void TryStoreReportFailsAfterTheSessionIsDiscarded()
    {
        var session = FrozenSession();
        Assert.True(session.TryQueueReport());
        Assert.True(session.TryMarkReportGenerating(1));

        session.MarkDiscarded();

        Assert.False(session.TryStoreReport(1, Report(session)));
        Assert.False(session.TryMarkReportFailed(1, "report_unavailable"));
        Assert.Null(session.Report);
        Assert.Empty(session.Turns());
        Assert.True(session.IsDiscarded);
    }

    [Fact]
    public void MarkDiscardedCancelsTheLifetimeTokenAndBlocksFurtherChanges()
    {
        var session = CreateSession();
        Assert.False(session.Lifetime.IsCancellationRequested);

        session.MarkDiscarded();
        session.MarkDiscarded();

        Assert.True(session.Lifetime.IsCancellationRequested);
        Assert.False(session.TryTransition(ConversationState.Connecting, T0));
        Assert.False(session.FreezeTranscript(TestTranscripts.Conversation(session.Id), complete: true));
        Assert.False(session.TryQueueReport());
    }

    [Fact]
    public void RequestEndSignalsEndRequestedWithoutDiscarding()
    {
        var session = CreateSession();
        Assert.False(session.EndRequested.IsCancellationRequested);

        session.RequestEnd();
        session.RequestEnd();

        Assert.True(session.EndRequested.IsCancellationRequested);
        Assert.False(session.Lifetime.IsCancellationRequested);
        Assert.False(session.IsDiscarded);
    }

    private static PracticeSession CreateSession() => new(
        Guid.NewGuid(),
        new DateTimeOffset(T0.AddHours(8)),
        TestScenarios.Shipped(TestScenarios.MarketDecline),
        Difficulty.Intermediate,
        RoleplayPromptBuilder.Version,
        isSimulated: false,
        T0);

    private static PracticeSession EndedSession()
    {
        var session = CreateSession();
        Assert.True(session.TryTransition(ConversationState.Connecting, T0));
        Assert.True(session.TryTransition(ConversationState.Active, T0.AddSeconds(1)));
        Assert.True(session.TryTransition(ConversationState.Ending, T0.AddSeconds(90), SessionEndReason.UserEnded));
        Assert.True(session.TryTransition(ConversationState.TranscriptFinalizing, T0.AddSeconds(91)));
        Assert.True(session.TryTransition(ConversationState.Ended, T0.AddSeconds(92)));
        return session;
    }

    private static PracticeSession FrozenSession()
    {
        var session = EndedSession();
        Assert.True(session.FreezeTranscript(TestTranscripts.Conversation(session.Id), complete: true));
        return session;
    }

    private static void FailCurrentReport(PracticeSession session, string errorCode)
    {
        Assert.True(session.TryMarkReportGenerating(1));
        Assert.True(session.TryMarkReportFailed(1, errorCode));
    }

    private static CoachingReport Report(PracticeSession session) => new()
    {
        SchemaVersion = Rubric.SchemaVersion,
        SessionId = session.Id,
        ScenarioId = session.Scenario.Id,
        ScenarioVersion = session.Scenario.Version,
        TranscriptRevision = 1,
        TranscriptHash = session.Snapshot().TranscriptHash,
        GeneratedAtUtc = T0.AddMinutes(3),
        PromptVersion = CoachingPromptBuilder.Version,
        RubricVersion = Rubric.Version,
        ModelDeployment = "synthetic",
        AssessmentStatus = AssessmentStatus.SufficientEvidence,
        OverallScore = TestReportContent.ExpectedOverallScore,
        ScoredCategoryCount = TestReportContent.ExpectedScoredCategories,
        UsableAdvisorTurnCount = 3,
        IsSimulated = false,
        Content = TestReportContent.Valid(),
        TurnLabels = new Dictionary<string, Guid>(),
    };
}
