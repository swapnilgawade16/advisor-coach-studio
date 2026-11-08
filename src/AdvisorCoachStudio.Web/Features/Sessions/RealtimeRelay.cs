using System.Net.WebSockets;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Channels;
using AdvisorCoachStudio.Web.Contracts;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Access;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.WebSockets;
using AdvisorCoachStudio.Web.Prompts;

namespace AdvisorCoachStudio.Web.Features.Sessions;

/// <summary>
/// Application-protocol relay between one browser socket and one Azure realtime socket. It is a protocol boundary,
/// not a transparent proxy: browser messages are validated and translated into an allow-listed set of provider events,
/// and provider events are normalized into safe application events. All state changes run on one event loop.
/// </summary>
public sealed partial class RealtimeRelay
{
    private const int MaxProviderMessageBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan[] ConnectBackoff = [TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1500)];

    private readonly RelayMode _mode;
    private readonly PracticeSession _session;
    private readonly DemonstrationSession? _demonstration;
    private readonly WebSocket _browser;
    private readonly ClaimsPrincipal _user;
    private readonly AccessOwner _owner;
    private readonly string _correlationId;
    private readonly RealtimeRelayFactory _deps;
    private readonly ILogger _logger;
    private readonly TranscriptAssembler _assembler;
    private readonly Channel<RelayEvent> _events = Channel.CreateBounded<RelayEvent>(new BoundedChannelOptions(2048)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private SerializedWebSocketSender _browserOut = null!;
    private SerializedWebSocketSender? _providerOut;
    private WebSocket? _providerSocket;

    private DateTimeOffset _startedAt;
    private DateTimeOffset _endsAt;
    private long _lastBrowserActivityTicks;
    private DateTimeOffset _lastAccessCheck;
    // Flags shared with the browser receive loop. The receive loop is the only writer of _muted and
    // _lastBrowserActivityTicks; the event loop is the only writer of the others.
    private volatile bool _active;
    private volatile bool _finalizing;
    private volatile bool _httpEndRequested;
    private bool _finished;
    private DateTimeOffset _finalizeDeadline;
    private SessionEndReason _endReason = SessionEndReason.None;
    private bool _browserClosed;
    private volatile bool _providerClosed;
    private volatile bool _muted;
    private string? _speechItemId;
    private bool _openingRequested;
    private string _lastState = "";
    private int _droppedWhileMuted;
    private static readonly TimeSpan AudioSendStallLimit = TimeSpan.FromSeconds(2);

    private abstract record RelayEvent;
    private sealed record BrowserEvent(BrowserMessage Message) : RelayEvent;
    private sealed record ProviderMessageEvent(ProviderEvent Event) : RelayEvent;
    private sealed record TickEvent : RelayEvent;
    private sealed record BrowserClosedEvent : RelayEvent;
    private sealed record ProviderClosedEvent : RelayEvent;
    private sealed record FaultEvent(SessionEndReason Reason, string Code, string Message, bool Retryable) : RelayEvent;

    internal RealtimeRelay(RelayMode mode, PracticeSession session, DemonstrationSession? demonstration, WebSocket browser, ClaimsPrincipal user, AccessOwner owner, string correlationId, RealtimeRelayFactory deps)
    {
        _mode = mode;
        _session = session;
        _demonstration = demonstration;
        _browser = browser;
        _user = user;
        _owner = owner;
        _correlationId = correlationId;
        _deps = deps;
        _logger = deps.Logger;
        _assembler = new TranscriptAssembler(session.Id, deps.Time);
    }

    private DateTimeOffset Now => _deps.Time.GetUtcNow();
    private string Speaker(TurnSpeaker speaker) => _mode == RelayMode.Demonstration ? "demonstration" : speaker == TurnSpeaker.Advisor ? "advisor" : "client";

    public async Task RunAsync(CancellationToken requestAborted)
    {
        // Browser disconnects are detected by the receive loop (close frame or socket error), which keeps the
        // finalization drain working; only discard and application shutdown cancel the relay outright.
        _ = requestAborted;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_session.Lifetime, _deps.ApplicationStopping);
        var ct = cts.Token;
        _browserOut = new SerializedWebSocketSender(_browser, maxQueuedMessages: 1024, maxQueuedAudioMs: _deps.Options.MaxQueuedOutputMs + 1000);
        Interlocked.Exchange(ref _lastBrowserActivityTicks, _deps.Time.GetUtcNow().UtcTicks);
        Task? browserLoop = null;
        Task? providerLoop = null;
        Task? tickLoop = null;
        try
        {
            SendState("connecting");
            if (!await ConnectProviderAsync(ct))
            {
                return;
            }

            browserLoop = Task.Run(() => BrowserReceiveLoopAsync(ct), CancellationToken.None);
            providerLoop = Task.Run(() => ProviderReceiveLoopAsync(ct), CancellationToken.None);
            tickLoop = Task.Run(() => TickLoopAsync(ct), CancellationToken.None);

            // The HTTP end fallback sets a flag that is checked after every event, so it can never be lost.
            using var endRegistration = _mode == RelayMode.Roleplay
                ? _session.EndRequested.Register(() =>
                {
                    _httpEndRequested = true;
                    _events.Writer.TryWrite(new TickEvent());
                })
                : default;
            await ProcessEventsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await HandleCancellationAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Realtime relay for session {SessionId} failed unexpectedly.", _session.Id);
            if (!_finished && _active)
            {
                RequestEnd(SessionEndReason.ProviderError, "internal_error", "Something went wrong. Your completed transcript turns were kept.", retryable: true);
                CompleteFinalization();
            }
            else if (!_finished)
            {
                FailSetup("internal_error", "The practice session could not be started. Please try again.", retryable: true);
            }
        }
        finally
        {
            // 1. Drain queued messages (e.g. session.ended) and send close frames.
            await CloseSocketsAsync();

            // 2. Let the peers complete the close handshake so the final frames are flushed. Cancelling a pending
            //    ReceiveAsync (or disposing the socket) earlier aborts the connection and can drop those frames.
            if (browserLoop is null)
            {
                await DrainUntilCloseAsync(_browser, TimeSpan.FromSeconds(2));
            }

            await WaitQuietlyAsync([browserLoop, providerLoop], TimeSpan.FromSeconds(3));

            // 3. Only then stop whatever is still running (unresponsive peers, the tick loop).
            await cts.CancelAsync();
            await WaitQuietlyAsync([browserLoop, providerLoop, tickLoop], TimeSpan.FromSeconds(3));

            _providerSocket?.Dispose();
            await _browserOut.DisposeAsync();
            if (_providerOut is not null)
            {
                await _providerOut.DisposeAsync();
            }
        }
    }

    private static async Task WaitQuietlyAsync(Task?[] tasks, TimeSpan timeout)
    {
        try
        {
            await Task.WhenAll(tasks.OfType<Task>()).WaitAsync(timeout, CancellationToken.None);
        }
        catch (Exception)
        {
            // Loop exceptions were already converted into relay events; timeouts fall through to cancellation.
        }
    }

    /// <summary>Waits (bounded) for the peer's close frame when no receive loop is running, e.g. after setup failed.</summary>
    private static async Task DrainUntilCloseAsync(WebSocket socket, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[4096];
        try
        {
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                var result = await socket.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Timed out or the peer vanished; closing is best effort.
        }
    }

    private async Task HandleCancellationAsync()
    {
        if (_finished)
        {
            return;
        }

        if (_session.IsDiscarded)
        {
            // The session was discarded (new session, sign-out, or explicit discard): store nothing.
            _browserOut.TryEnqueue(AppMessages.Error("session_discarded", "This practice session was reset.", _correlationId, false), critical: true);
            _finished = true;
            return;
        }

        if (_active)
        {
            RequestEnd(SessionEndReason.Disconnected, null, null, false);
            CompleteFinalization();
        }
        else
        {
            FailSetup("service_stopping", "The service is restarting. Please start a new session.", retryable: true);
        }

        await Task.CompletedTask;
    }

    // ---- Provider connection and handshake --------------------------------------------------

    private async Task<bool> ConnectProviderAsync(CancellationToken ct)
    {
        var transitioned = _mode == RelayMode.Roleplay
            ? _session.TryTransition(ConversationState.Connecting, Now.UtcDateTime)
            : _demonstration!.TryStart(Now.UtcDateTime);
        if (!transitioned)
        {
            _browserOut.TryEnqueue(AppMessages.Error("invalid_state", "This session cannot be started again. Start a new practice session.", _correlationId, false));
            _finished = true;
            return false;
        }

        var deadline = Now + HandshakeTimeout;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await ConnectOnceAsync(deadline, ct);
                break;
            }
            catch (ProviderConnectException ex) when (ex.IsRetryable && attempt < ConnectBackoff.Length && Now + ConnectBackoff[attempt] < deadline)
            {
                _logger.LogInformation("Realtime connect attempt {Attempt} failed ({Failure}); retrying.", attempt + 1, ex.Failure);
                await DisposeProviderAsync();
                await Task.Delay(ConnectBackoff[attempt], ct);
            }
            catch (ProviderConnectException ex)
            {
                await DisposeProviderAsync();
                _deps.Health.RealtimeFailed(ex.Failure == ProviderConnectFailure.Authentication);
                RelayMetrics.ConnectionFailures.Add(1, new KeyValuePair<string, object?>("failure", ex.Failure.ToString()));
                var (code, message) = ex.Failure switch
                {
                    ProviderConnectFailure.NotConfigured => ("provider_not_configured", "Azure setup required. Live voice practice is not configured yet."),
                    ProviderConnectFailure.Authentication => ("provider_auth_failed", "The voice service rejected the application's credentials. Please contact the administrator."),
                    ProviderConnectFailure.Throttled => ("provider_throttled", "The voice service is busy right now. Please try again in a minute."),
                    ProviderConnectFailure.NotFound => ("provider_not_found", "The configured voice deployment was not found. Please contact the administrator."),
                    ProviderConnectFailure.Timeout => ("provider_timeout", "Connecting to the voice service timed out. Please try again."),
                    _ => ("provider_unavailable", "The voice service is temporarily unavailable. Please try again."),
                };
                FailSetup(code, message, ex.IsRetryable || ex.Failure == ProviderConnectFailure.Throttled);
                return false;
            }
            catch (Exception ex) when (ex is ProviderProtocolException or WebSocketException or JsonException or TimeoutException or WebSocketMessageTooLargeException or InvalidOperationException)
            {
                await DisposeProviderAsync();
                _deps.Health.RealtimeFailed(false);
                RelayMetrics.ConnectionFailures.Add(1, new KeyValuePair<string, object?>("failure", ex.GetType().Name));
                _logger.LogWarning("Realtime handshake failed for session {SessionId}: {Error}.", _session.Id, ex.GetType().Name);
                FailSetup("provider_unavailable", "The voice service did not complete setup. Please try again.", retryable: true);
                return false;
            }
        }

        _deps.Health.RealtimeSucceeded();
        RelayMetrics.Connections.Add(1, new KeyValuePair<string, object?>("mode", _mode.ToString()));
        _startedAt = Now;
        _active = true;
        if (_mode == RelayMode.Roleplay)
        {
            _session.TryTransition(ConversationState.Active, _startedAt.UtcDateTime);
            var cap = _startedAt.AddMinutes(Math.Max(1, _deps.Options.MaxSessionMinutes));
            _endsAt = cap < _owner.ExpiresAtUtc ? cap : _owner.ExpiresAtUtc;
        }
        else
        {
            _demonstration!.MarkPlaying();
            _endsAt = _startedAt.AddSeconds(Math.Max(10, _deps.Options.DemonstrationMaxSeconds));
        }

        _browserOut.TryEnqueue(AppMessages.Ready(
            _mode == RelayMode.Roleplay ? _session.Id : _demonstration!.Id,
            (int)Math.Round((_endsAt - _startedAt).TotalSeconds),
            _endsAt,
            _session.Scenario.ClientDisplayName,
            _session.Scenario.Title,
            _session.IsSimulated), critical: true);
        SendState("ready");

        // Request the opening (or demonstration) response exactly once.
        if (!_openingRequested)
        {
            _openingRequested = true;
            EnqueueProvider(ProviderClientEvents.ResponseCreate());
        }

        return true;
    }

    private async Task ConnectOnceAsync(DateTimeOffset deadline, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Max(deadline - Now, TimeSpan.FromSeconds(1)));
        try
        {
            _providerSocket = await _deps.Connector.ConnectAsync(timeout.Token);
            _providerOut = new SerializedWebSocketSender(_providerSocket, maxQueuedMessages: 1024, maxQueuedAudioMs: _deps.Options.MaxQueuedInputMs);
            await ExpectProviderEventAsync("session.created", timeout.Token);
            var settings = _deps.Connector.SessionSettings;
            var (instructions, promptVersion) = _mode == RelayMode.Roleplay
                ? (RoleplayPromptBuilder.Build(_session.Scenario, _session.Difficulty), RoleplayPromptBuilder.Version)
                : (DemonstrationInstructions ?? "", DemonstrationPromptBuilder.Version);
            var update = _mode == RelayMode.Roleplay
                ? ProviderClientEvents.SessionUpdateRoleplay(instructions, settings, _deps.Options)
                : ProviderClientEvents.SessionUpdateDemonstration(instructions, settings);
            await _providerOut.SendAsync(update, timeout.Token);
            var updated = await ExpectProviderEventAsync("session.updated", timeout.Token);
            VerifyAppliedInstructions(instructions, promptVersion, updated.SessionInstructions);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderConnectException(ProviderConnectFailure.Timeout, "Realtime handshake timed out.");
        }
    }

    /// <summary>
    /// Azure echoes the effective session in session.updated. When it reports instructions other than the ones sent,
    /// setup fails instead of running the conversation with the wrong client persona or scenario.
    /// </summary>
    private void VerifyAppliedInstructions(string sent, string promptVersion, string? applied)
    {
        var fingerprint = PromptFingerprint.Of(sent);
        if (applied is null)
        {
            _logger.LogInformation(
                "Provider did not echo the session instructions for session {SessionId}; sent {PromptVersion} ({Length} chars, sha256 {Fingerprint}).",
                _session.Id, promptVersion, sent.Length, fingerprint);
            return;
        }

        if (string.Equals(applied, sent, StringComparison.Ordinal) || PromptFingerprint.SameIgnoringWhitespace(applied, sent))
        {
            _logger.LogInformation(
                "Provider confirmed the session instructions for session {SessionId}: {PromptVersion}, {Length} chars, sha256 {Fingerprint}.",
                _session.Id, promptVersion, sent.Length, fingerprint);
            return;
        }

        var appliedFingerprint = PromptFingerprint.Of(applied);
        _logger.LogWarning(
            "Provider applied different session instructions for session {SessionId}: sent {PromptVersion} ({Length} chars, sha256 {Fingerprint}), provider reports {AppliedLength} chars (sha256 {AppliedFingerprint}).",
            _session.Id, promptVersion, sent.Length, fingerprint, applied.Length, appliedFingerprint);
        throw new ProviderProtocolException("session.updated", "session.instructions");
    }

    private async Task<ProviderEvent> ExpectProviderEventAsync(string expectedType, CancellationToken ct)
    {
        while (true)
        {
            var message = await WebSocketMessageReader.ReadAsync(_providerSocket!, MaxProviderMessageBytes, ct)
                ?? throw new ProviderConnectException(ProviderConnectFailure.Network, "Provider closed during setup.");
            if (message.Type != WebSocketMessageType.Text)
            {
                continue;
            }

            var evt = ProviderEventParser.Parse(message.Payload);
            if (evt.Type == expectedType)
            {
                return evt;
            }

            if (evt.Type == "error")
            {
                RelayMetrics.ProviderErrors.Add(1, new KeyValuePair<string, object?>("code", evt.ErrorCode ?? evt.ErrorType));
                _logger.LogWarning("Provider error during setup: {ErrorType}/{ErrorCode}.", evt.ErrorType, evt.ErrorCode);
                var failure = evt.ErrorType is "authentication_error" or "permission_error" ? ProviderConnectFailure.Authentication
                    : evt.ErrorCode is "rate_limit_exceeded" ? ProviderConnectFailure.Throttled
                    : ProviderConnectFailure.Network;
                throw new ProviderConnectException(failure, "Provider rejected the session configuration.");
            }
        }
    }

    private void FailSetup(string code, string message, bool retryable)
    {
        if (_mode == RelayMode.Roleplay)
        {
            _session.TryTransition(ConversationState.Failed, Now.UtcDateTime, failureCode: code);
        }
        else
        {
            _demonstration!.Finish(DemonstrationStatus.Failed, "", false, Now.UtcDateTime, code);
        }

        _browserOut.TryEnqueue(AppMessages.Error(code, message, _correlationId, retryable), critical: true);
        SendState("ended", critical: true);
        _finished = true;
    }

    // ---- Receive loops ----------------------------------------------------------------------

    private async Task BrowserReceiveLoopAsync(CancellationToken ct)
    {
        long lastSequence = 0;
        var budget = new AudioRateBudget(burstMs: 2000, _deps.Time);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await WebSocketMessageReader.ReadAsync(_browser, AppProtocol.MaxMessageBytes, ct);
                if (message is null)
                {
                    await PostAsync(new BrowserClosedEvent(), ct);
                    return;
                }

                Interlocked.Exchange(ref _lastBrowserActivityTicks, _deps.Time.GetUtcNow().UtcTicks);
                if (message.Value.Type != WebSocketMessageType.Text)
                {
                    await PostAsync(new FaultEvent(SessionEndReason.Disconnected, "protocol_error", "Unsupported message format.", false), ct);
                    return;
                }

                BrowserMessage parsed;
                try
                {
                    parsed = BrowserMessageParser.Parse(message.Value.Payload.Span, allowAudio: _mode == RelayMode.Roleplay);
                }
                catch (AppProtocolException ex)
                {
                    _logger.LogWarning("Browser protocol violation in session {SessionId}: {Code}.", _session.Id, ex.Code);
                    await PostAsync(new FaultEvent(SessionEndReason.Disconnected, "protocol_error", "The browser sent an invalid message.", false), ct);
                    return;
                }

                switch (parsed)
                {
                    case AudioAppendMessage audio:
                        if (audio.ClientSequence <= lastSequence)
                        {
                            await PostAsync(new FaultEvent(SessionEndReason.Disconnected, "protocol_error", "Audio arrived out of order.", false), ct);
                            return;
                        }

                        lastSequence = audio.ClientSequence;
                        if (!budget.TryConsume(audio.ByteLength))
                        {
                            await PostAsync(new FaultEvent(SessionEndReason.ConnectionQuality, "audio_rate_exceeded", "Audio arrived faster than real time.", false), ct);
                            return;
                        }

                        if (!await ForwardAudioAsync(audio, ct))
                        {
                            return;
                        }

                        break;

                    case MicrophoneMutedMessage muted:
                        // Single writer: only this loop changes the mute flag (no event-loop round trip, no reordering).
                        _muted = muted.Muted;
                        break;

                    case HeartbeatMessage:
                        break;

                    default:
                        await PostAsync(new BrowserEvent(parsed), ct);
                        break;
                }
            }
        }
        catch (WebSocketMessageTooLargeException)
        {
            await TryPostAsync(new FaultEvent(SessionEndReason.Disconnected, "protocol_error", "A message was too large.", false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // WebSocketException or any unexpected failure: treat as a lost browser connection.
            _logger.LogDebug("Browser receive loop ended: {Error}.", ex.GetType().Name);
            await TryPostAsync(new BrowserClosedEvent());
        }
    }

    private async Task ProviderReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await WebSocketMessageReader.ReadAsync(_providerSocket!, MaxProviderMessageBytes, ct);
                if (message is null)
                {
                    await PostAsync(new ProviderClosedEvent(), ct);
                    return;
                }

                if (message.Value.Type != WebSocketMessageType.Text)
                {
                    continue;
                }

                ProviderEvent evt;
                try
                {
                    evt = ProviderEventParser.Parse(message.Value.Payload);
                }
                catch (Exception ex) when (ex is ProviderProtocolException or JsonException)
                {
                    var detail = ex is ProviderProtocolException p ? $"{p.EventType}:{p.Field}" : "invalid_json";
                    _logger.LogError("Provider event shape mismatch in session {SessionId}: {Detail}.", _session.Id, detail);
                    await PostAsync(new FaultEvent(SessionEndReason.ProviderError, "provider_protocol_error", "The voice service sent an unexpected response. Your completed turns were kept.", true), ct);
                    return;
                }

                await PostAsync(new ProviderMessageEvent(evt), ct);
            }
        }
        catch (WebSocketMessageTooLargeException)
        {
            _logger.LogError("Provider sent an oversized message in session {SessionId}.", _session.Id);
            await TryPostAsync(new FaultEvent(SessionEndReason.ProviderError, "provider_protocol_error", "The voice service sent an unexpected response. Your completed turns were kept.", true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // WebSocketException or any unexpected failure: the provider connection is gone.
            _logger.LogDebug("Provider receive loop ended: {Error}.", ex.GetType().Name);
            await TryPostAsync(new ProviderClosedEvent());
        }
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), _deps.Time);
            while (await timer.WaitForNextTickAsync(ct))
            {
                _events.Writer.TryWrite(new TickEvent());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private ValueTask PostAsync(RelayEvent relayEvent, CancellationToken ct) => _events.Writer.WriteAsync(relayEvent, ct);

    /// <summary>
    /// Forwards validated microphone audio with backpressure (at most one frame in flight), so audio is never
    /// silently dropped. A send that stalls beyond the limit is sustained overload and ends the attempt with
    /// connection_quality; shorter stalls back up into the browser, whose bufferedAmount monitor reports congestion.
    /// </summary>
    private async Task<bool> ForwardAudioAsync(AudioAppendMessage audio, CancellationToken ct)
    {
        if (!_active || _finalizing || _providerClosed || _providerOut is null)
        {
            return true; // Not streaming (still connecting or already ending).
        }

        if (_muted)
        {
            Interlocked.Increment(ref _droppedWhileMuted);
            return true;
        }

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(AudioSendStallLimit);
        try
        {
            await _providerOut.SendAsync(ProviderClientEvents.AppendAudio(audio.AudioBase64), stall.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await PostAsync(new FaultEvent(SessionEndReason.ConnectionQuality, "connection_quality", "The connection to the voice service could not keep up. Your completed turns were kept.", true), ct);
            return false;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // The provider socket failed; the provider receive loop reports the disconnect.
            return true;
        }
    }

    private async Task TryPostAsync(RelayEvent relayEvent)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _events.Writer.WriteAsync(relayEvent, timeout.Token);
        }
        catch (Exception)
        {
        }
    }

    // ---- Event loop -------------------------------------------------------------------------

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        await foreach (var relayEvent in _events.Reader.ReadAllAsync(ct))
        {
            switch (relayEvent)
            {
                case BrowserEvent browser:
                    HandleBrowser(browser.Message);
                    break;
                case ProviderMessageEvent provider:
                    HandleProvider(provider.Event);
                    break;
                case TickEvent:
                    ReleaseOutputAudio();
                    break;
                case BrowserClosedEvent:
                    _browserClosed = true;
                    RequestEnd(SessionEndReason.Disconnected, null, null, false);
                    break;
                case ProviderClosedEvent:
                    _providerClosed = true;
                    if (!_finalizing)
                    {
                        RequestEnd(SessionEndReason.ProviderError, "provider_disconnected", "The voice service connection was lost. Your completed turns were kept; please start a new attempt.", true);
                    }

                    break;
                case FaultEvent fault:
                    RequestEnd(fault.Reason, fault.Code, fault.Message, fault.Retryable);
                    break;
            }

            // Deadlines are checked after EVERY event (not only on ticks, which may be skipped when the queue is busy).
            CheckDeadlines();

            if (_finalizing && ReadyToFreeze())
            {
                CompleteFinalization();
            }

            if (_finished)
            {
                return;
            }
        }
    }

    private void HandleBrowser(BrowserMessage message)
    {
        switch (message)
        {
            case PlaybackPositionMessage position:
                HandlePlaybackPosition(position);
                break;
            case SessionEndMessage end:
                if (end.Reason == "connection_quality")
                {
                    RequestEnd(SessionEndReason.ConnectionQuality, null, null, false);
                }
                else
                {
                    RequestEnd(SessionEndReason.UserEnded, null, null, false);
                }

                break;
        }
    }

    private void CheckDeadlines()
    {
        if (_finished)
        {
            return;
        }

        var now = Now;
        if (_active && !_finalizing)
        {
            if (_httpEndRequested)
            {
                RequestEnd(SessionEndReason.UserEnded, null, null, false);
                return;
            }

            if (now >= _endsAt)
            {
                var reason = _mode == RelayMode.Roleplay && _endsAt >= _owner.ExpiresAtUtc ? SessionEndReason.AccessExpired : SessionEndReason.DurationLimit;
                RequestEnd(reason, null, null, false);
                return;
            }

            if (now - _lastAccessCheck > TimeSpan.FromSeconds(5))
            {
                _lastAccessCheck = now;
                if (!_deps.Access.IsValid(_user))
                {
                    RequestEnd(SessionEndReason.AccessExpired, "access_expired", "Your access has expired. Enter the access code to continue.", false);
                    return;
                }
            }

            var lastActivity = new DateTimeOffset(Interlocked.Read(ref _lastBrowserActivityTicks), TimeSpan.Zero);
            if (now - lastActivity > TimeSpan.FromSeconds(45))
            {
                RequestEnd(SessionEndReason.Disconnected, null, null, false);
                return;
            }
        }

        ProcessPendingTruncations(now);
    }

    private bool EnqueueProvider(byte[] payload, int audioMs = 0) =>
        _providerOut is not null && !_providerClosed && _providerOut.TryEnqueue(payload, audioMs, critical: audioMs == 0);

    private void SendState(string state, bool critical = false)
    {
        if (_lastState == state)
        {
            return;
        }

        _lastState = state;
        _browserOut.TryEnqueue(AppMessages.State(state), critical: critical);
    }

    private void SendError(string code, string message, bool retryable) =>
        _browserOut.TryEnqueue(AppMessages.Error(code, message, _correlationId, retryable), critical: true);

    private async Task DisposeProviderAsync()
    {
        if (_providerOut is not null)
        {
            await _providerOut.DisposeAsync();
            _providerOut = null;
        }

        _providerSocket?.Dispose();
        _providerSocket = null;
    }

    private async Task CloseSocketsAsync()
    {
        if (_providerOut is not null)
        {
            await _providerOut.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", TimeSpan.FromSeconds(2));
        }

        await _browserOut.CloseAsync(WebSocketCloseStatus.NormalClosure, "session ended", TimeSpan.FromSeconds(3));
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
