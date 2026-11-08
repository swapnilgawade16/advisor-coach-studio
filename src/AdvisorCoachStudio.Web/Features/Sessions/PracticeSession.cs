using System.Security.Cryptography;
using System.Text;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Coaching;
using AdvisorCoachStudio.Web.Features.Scenarios;

namespace AdvisorCoachStudio.Web.Features.Sessions;

/// <summary>
/// One in-memory practice session. All mutable state is guarded by a lock; nothing is persisted.
/// Discarding cancels <see cref="Lifetime"/>, which stops any live relay and in-flight report work.
/// </summary>
public sealed class PracticeSession
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationTokenSource _endRequested = new();
    private readonly Dictionary<(string ItemId, int ContentIndex), TranscriptTurn> _liveTurns = [];
    private IReadOnlyList<TranscriptTurn> _frozenTurns = [];

    public PracticeSession(Guid ownerId, DateTimeOffset ownerExpiresAtUtc, ScenarioDefinition scenario, Difficulty difficulty, string promptVersion, bool isSimulated, DateTime createdAtUtc)
    {
        Id = Guid.NewGuid();
        OwnerId = ownerId;
        OwnerExpiresAtUtc = ownerExpiresAtUtc;
        Scenario = scenario;
        Difficulty = difficulty;
        PromptVersion = promptVersion;
        IsSimulated = isSimulated;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }
    public Guid OwnerId { get; }
    public DateTimeOffset OwnerExpiresAtUtc { get; }

    /// <summary>Private scenario snapshot captured at creation (immutable).</summary>
    public ScenarioDefinition Scenario { get; }

    public Difficulty Difficulty { get; }
    public string PromptVersion { get; }
    public bool IsSimulated { get; }
    public DateTime CreatedAtUtc { get; }
    public CancellationToken Lifetime => _lifetime.Token;

    /// <summary>Signalled by the HTTP end fallback; the live relay treats it like a user End.</summary>
    public CancellationToken EndRequested => _endRequested.Token;

    public void RequestEnd()
    {
        try
        {
            _endRequested.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public ConversationState State { get { lock (_gate) { return _state; } } }
    public ReportStatus ReportStatus { get { lock (_gate) { return _reportStatus; } } }
    public int TranscriptRevision { get { lock (_gate) { return _transcriptRevision; } } }
    public bool IsDiscarded { get { lock (_gate) { return _discarded; } } }

    private ConversationState _state = ConversationState.Created;
    private SessionEndReason _endReason = SessionEndReason.None;
    private TranscriptStatus _transcriptStatus = TranscriptStatus.NotFinalized;
    private int _transcriptRevision;
    private string? _transcriptHash;
    private ReportStatus _reportStatus = ReportStatus.NotRequested;
    private string? _reportErrorCode;
    private CoachingReport? _report;
    private string? _failureCode;
    private DateTime? _startedAtUtc;
    private DateTime? _endedAtUtc;
    private bool _discarded;
    private DemonstrationSession? _demonstration;

    /// <summary>Atomically moves to <paramref name="to"/> if the transition is allowed from the current state.</summary>
    public bool TryTransition(ConversationState to, DateTime nowUtc, SessionEndReason? reason = null, string? failureCode = null)
    {
        lock (_gate)
        {
            if (_discarded || !SessionStateMachine.CanTransition(_state, to))
            {
                return false;
            }

            _state = to;
            if (to == ConversationState.Active)
            {
                _startedAtUtc = nowUtc;
            }

            if (reason is { } r && _endReason == SessionEndReason.None)
            {
                _endReason = r;
            }

            if (failureCode is not null)
            {
                _failureCode = failureCode;
            }

            if (to is ConversationState.Ended or ConversationState.Failed or ConversationState.Expired)
            {
                _endedAtUtc ??= nowUtc;
            }

            return true;
        }
    }

    /// <summary>Records a finalized (or updated) live turn so the transcript API reflects progress before the freeze.</summary>
    public void RecordLiveTurn(TranscriptTurn turn)
    {
        lock (_gate)
        {
            if (_discarded || _transcriptRevision > 0)
            {
                return;
            }

            _liveTurns[(turn.ProviderItemId, turn.ContentIndex)] = turn;
        }
    }

    /// <summary>Freezes transcript revision 1. Later calls are ignored so a frozen revision never changes.</summary>
    public bool FreezeTranscript(IReadOnlyList<TranscriptTurn> orderedTurns, bool complete)
    {
        lock (_gate)
        {
            if (_discarded || _transcriptRevision > 0)
            {
                return false;
            }

            _frozenTurns = orderedTurns.ToArray();
            _liveTurns.Clear();
            _transcriptRevision = 1;
            _transcriptStatus = complete ? TranscriptStatus.Complete : TranscriptStatus.Incomplete;
            _transcriptHash = ComputeHash(_frozenTurns);
            return true;
        }
    }

    public IReadOnlyList<TranscriptTurn> Turns()
    {
        lock (_gate)
        {
            return _transcriptRevision > 0
                ? _frozenTurns
                : _liveTurns.Values.OrderBy(t => t.Sequence).ThenBy(t => t.CreatedAtUtc).ToArray();
        }
    }

    public void SetReportSetupRequired()
    {
        lock (_gate)
        {
            if (!_discarded && _reportStatus is ReportStatus.NotRequested or ReportStatus.SetupRequired)
            {
                _reportStatus = ReportStatus.SetupRequired;
            }
        }
    }

    /// <summary>Queues report work for the frozen revision. Returns false if work is already queued/running or a report exists.</summary>
    /// <remarks>User retries are refused for permanent failures and after <see cref="MaxReportRequests"/> requests.</remarks>
    public bool TryQueueReport(bool userRetry = false)
    {
        lock (_gate)
        {
            if (_discarded || _transcriptRevision == 0 || _reportStatus is ReportStatus.Queued or ReportStatus.Generating or ReportStatus.Ready)
            {
                return false;
            }

            if (userRetry && (_reportRequests >= MaxReportRequests || !IsRetryableReportError(_reportErrorCode)))
            {
                return false;
            }

            _reportRequests++;
            _reportStatus = ReportStatus.Queued;
            _reportErrorCode = null;
            return true;
        }
    }

    /// <summary>The initial request plus up to three user retries per session.</summary>
    public const int MaxReportRequests = 4;

    public int ReportRequestCount { get { lock (_gate) { return _reportRequests; } } }

    /// <summary>Refusals and content-filter outcomes are permanent for a given transcript.</summary>
    public static bool IsRetryableReportError(string? code) => code is not ("report_refused" or "report_content_filtered");

    private int _reportRequests;

    public bool TryMarkReportGenerating(int revision)
    {
        lock (_gate)
        {
            if (_discarded || revision != _transcriptRevision || _reportStatus != ReportStatus.Queued)
            {
                return false;
            }

            _reportStatus = ReportStatus.Generating;
            return true;
        }
    }

    /// <summary>Stores a validated report only if the session is still current and the revision still matches.</summary>
    public bool TryStoreReport(int revision, CoachingReport report)
    {
        lock (_gate)
        {
            if (_discarded || revision != _transcriptRevision || _reportStatus != ReportStatus.Generating)
            {
                return false;
            }

            _report = report;
            _reportStatus = ReportStatus.Ready;
            _reportErrorCode = null;
            return true;
        }
    }

    public bool TryMarkReportFailed(int revision, string errorCode)
    {
        lock (_gate)
        {
            if (_discarded || revision != _transcriptRevision || _reportStatus != ReportStatus.Generating)
            {
                return false;
            }

            _reportStatus = ReportStatus.Failed;
            _reportErrorCode = errorCode;
            return true;
        }
    }

    public CoachingReport? Report { get { lock (_gate) { return _report; } } }

    public DemonstrationSession? Demonstration { get { lock (_gate) { return _demonstration; } } }

    /// <summary>Creates a new demonstration unless one is currently live.</summary>
    public DemonstrationSession? TryCreateDemonstration(string promptVersion, bool simulated, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_discarded || _state != ConversationState.Ended)
            {
                return null;
            }

            if (_demonstration is { IsLive: true })
            {
                return null;
            }

            _demonstration = new DemonstrationSession(Id, promptVersion, simulated, nowUtc);
            return _demonstration;
        }
    }

    public void MarkDiscarded()
    {
        lock (_gate)
        {
            if (_discarded)
            {
                return;
            }

            _discarded = true;
            _frozenTurns = [];
            _liveTurns.Clear();
            _report = null;
            _demonstration = null;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public SessionSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new SessionSnapshot(
                Id, Scenario.Id, Scenario.Version, Scenario.Title, Scenario.ClientDisplayName, Scenario.ClientInitials, Difficulty,
                _state, _endReason, _transcriptStatus, _transcriptRevision, _transcriptHash, _reportStatus, _reportErrorCode,
                _failureCode, IsSimulated, CreatedAtUtc, _startedAtUtc, _endedAtUtc);
        }
    }

    private static string ComputeHash(IReadOnlyList<TranscriptTurn> turns)
    {
        var builder = new StringBuilder();
        foreach (var turn in turns)
        {
            builder.Append(turn.Sequence).Append('|').Append(turn.Speaker).Append('|').Append(turn.Status).Append('|').Append(turn.Text).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}

public sealed record SessionSnapshot(
    Guid Id,
    string ScenarioId,
    string ScenarioVersion,
    string ScenarioTitle,
    string ClientDisplayName,
    string ClientInitials,
    Difficulty Difficulty,
    ConversationState State,
    SessionEndReason EndReason,
    TranscriptStatus TranscriptStatus,
    int TranscriptRevision,
    string? TranscriptHash,
    ReportStatus ReportStatus,
    string? ReportErrorCode,
    string? FailureCode,
    bool IsSimulated,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? EndedAtUtc);
