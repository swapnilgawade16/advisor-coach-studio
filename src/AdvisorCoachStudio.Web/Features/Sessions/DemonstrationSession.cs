using AdvisorCoachStudio.Web.Domain;

namespace AdvisorCoachStudio.Web.Features.Sessions;

/// <summary>In-memory AI-as-advisor demonstration attached to a completed practice session. Never scored.</summary>
public sealed class DemonstrationSession(Guid parentSessionId, string promptVersion, bool isSimulated, DateTime createdAtUtc)
{
    private readonly Lock _gate = new();
    private DemonstrationStatus _status = DemonstrationStatus.Created;
    private string _text = "";
    private bool _textComplete;
    private string? _failureCode;
    private DateTime? _startedAtUtc;
    private DateTime? _endedAtUtc;

    public Guid Id { get; } = Guid.NewGuid();
    public Guid ParentSessionId { get; } = parentSessionId;
    public string PromptVersion { get; } = promptVersion;
    public bool IsSimulated { get; } = isSimulated;
    public DateTime CreatedAtUtc { get; } = createdAtUtc;

    public bool IsLive
    {
        get { lock (_gate) { return _status is DemonstrationStatus.Connecting or DemonstrationStatus.Playing; } }
    }

    public bool TryStart(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (_status != DemonstrationStatus.Created)
            {
                return false;
            }

            _status = DemonstrationStatus.Connecting;
            _startedAtUtc = nowUtc;
            return true;
        }
    }

    public void MarkPlaying()
    {
        lock (_gate)
        {
            if (_status == DemonstrationStatus.Connecting)
            {
                _status = DemonstrationStatus.Playing;
            }
        }
    }

    public void Finish(DemonstrationStatus status, string text, bool textComplete, DateTime nowUtc, string? failureCode = null)
    {
        lock (_gate)
        {
            if (_status is DemonstrationStatus.Completed or DemonstrationStatus.Stopped or DemonstrationStatus.Failed)
            {
                return;
            }

            _status = status;
            _text = text;
            _textComplete = textComplete;
            _failureCode = failureCode;
            _endedAtUtc = nowUtc;
        }
    }

    public DemonstrationSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new DemonstrationSnapshot(Id, ParentSessionId, _status, PromptVersion, _text, _textComplete, _failureCode, IsSimulated, CreatedAtUtc, _startedAtUtc, _endedAtUtc);
        }
    }
}

public sealed record DemonstrationSnapshot(
    Guid Id,
    Guid ParentSessionId,
    DemonstrationStatus Status,
    string PromptVersion,
    string Text,
    bool TextComplete,
    string? FailureCode,
    bool IsSimulated,
    DateTime CreatedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? EndedAtUtc);
