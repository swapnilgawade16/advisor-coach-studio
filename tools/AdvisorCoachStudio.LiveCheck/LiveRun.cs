using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Scenarios;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.Configuration;
using AdvisorCoachStudio.Web.Prompts;

namespace AdvisorCoachStudio.LiveCheck;

internal sealed record AdvisorClip(string Name, byte[] Pcm);

/// <summary>One transcript turn as the application would record it (used to generate a live coaching report).</summary>
internal sealed record LiveTurn(TurnSpeaker Speaker, string ItemId, int ContentIndex, string? ResponseId, string Text, TurnStatus Status, bool? FullyHeard);

/// <summary>
/// The live conversation flow: configure the session exactly as the relay does, verify that Azure applied the system
/// instructions, request the opening once, then speak each advisor clip and verify transcription and client replies.
/// </summary>
internal sealed class LiveRun(
    IRealtimeProviderConnector connector,
    CheckLog checks,
    ScenarioDefinition scenario,
    string instructions,
    string expectedTranscriptionModel,
    RealtimeOptions realtime,
    IReadOnlyList<AdvisorClip> clips,
    bool interrupt)
{
    private static readonly TimeSpan ShortWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LongWait = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ThinkingPause = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan InterruptAfter = TimeSpan.FromMilliseconds(900);
    private readonly List<string> _conversation = [];
    private readonly List<LiveTurn> _turns = [];

    /// <summary>The conversation in order, with the same quotability rules the application applies.</summary>
    public IReadOnlyList<LiveTurn> Turns => _turns;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var connectTimer = Stopwatch.StartNew();
        var socket = await connector.ConnectAsync(cancellationToken);
        checks.Pass("connect", $"authenticated WebSocket to the GA endpoint in {connectTimer.ElapsedMilliseconds} ms");

        var session = new LiveProviderSession(socket);
        session.Start();
        try
        {
            await ConfigureAsync(session, cancellationToken);
            var opening = await OpeningAsync(session, cancellationToken);
            await ConversationAsync(session, opening, cancellationToken);
        }
        finally
        {
            await session.CloseAsync();
            ReportProtocol(session);
            PrintConversation();
        }
    }

    private async Task ConfigureAsync(LiveProviderSession session, CancellationToken cancellationToken)
    {
        var state = session.State;
        await session.RunUntilAsync(() => state.SessionCreated is not null, ShortWait, "session.created", cancellationToken);
        var created = Json.Obj(state.SessionCreated!.Value, "session");
        checks.Pass("session.created", $"session {Json.Str(created, "id") ?? "-"}, model {Json.Str(created, "model") ?? "-"}");

        await session.SendAsync(ProviderClientEvents.SessionUpdateRoleplay(instructions, connector.SessionSettings, realtime), cancellationToken);
        await session.RunUntilAsync(() => state.SessionUpdated is not null, ShortWait, "session.updated", cancellationToken);
        checks.Pass("session.update", "accepted by Azure (session.updated received)");
        VerifyEffectiveSession(Json.Obj(state.SessionUpdated!.Value, "session"));
    }

    private async Task<ResponseInfo> OpeningAsync(LiveProviderSession session, CancellationToken cancellationToken)
    {
        // Like the relay: start streaming the microphone and request the opening exactly once.
        var state = session.State;
        session.StartMicrophone();
        await session.SendAsync(ProviderClientEvents.ResponseCreate(), cancellationToken);
        await session.RunUntilAsync(() => state.ResponseOrder.Count > 0 && state.Responses[state.ResponseOrder[0]].Done, LongWait, "the client's opening response", cancellationToken);
        var opening = state.Responses[state.ResponseOrder[0]];
        VerifyResponse("opening", opening);
        var overlap = TextTools.WordOverlap(scenario.OpeningUtterance, opening.Transcript);
        var detail = $"{overlap.ToString("P0", CultureInfo.InvariantCulture)} of the configured opening line's words were spoken";
        if (overlap >= 0.5)
        {
            checks.Pass("opening line in character", detail);
        }
        else
        {
            checks.Warn("opening line in character", detail + "; review the transcript below");
        }

        _conversation.Add($"CLIENT (opening): {opening.Transcript}");
        AddClientTurn(opening, interrupted: false);
        return opening;
    }

    private async Task ConversationAsync(LiveProviderSession session, ResponseInfo opening, CancellationToken cancellationToken)
    {
        var interruptingClip = interrupt && clips.Count >= 2 ? clips.Count - 1 : -1;
        var previous = opening;
        for (var index = 0; index < clips.Count; index++)
        {
            var number = index + 1;
            AdvisorTurn turn;
            if (index == interruptingClip)
            {
                turn = await InterruptAsync(session, previous, number, clips[index], cancellationToken);
            }
            else
            {
                await ListenAsync(session, previous, cancellationToken);
                turn = BeginTurn(session, number, clips[index]);
            }

            var reply = await CompleteTurnAsync(session, turn, cancellationToken);
            previous = reply;
            if (index + 1 == interruptingClip)
            {
                continue; // The next clip interrupts this reply while it is still "playing".
            }

            await session.RunUntilAsync(() => reply.Done, LongWait, $"the end of the client's reply to advisor turn {number}", cancellationToken);
            VerifyResponse($"reply {number}", reply);
            ReportLatency(session.State, reply, number);
            _conversation.Add($"CLIENT: {reply.Transcript}");
            AddClientTurn(reply, interrupted: false);
        }
    }

    /// <summary>
    /// Records a client turn. A completed reply counts as fully heard (the check waits for playback before the advisor
    /// speaks); an interrupted reply is not, so, as in the application, it can never be quoted as report evidence.
    /// </summary>
    private void AddClientTurn(ResponseInfo reply, bool interrupted)
    {
        if (reply.ItemId is not { } itemId || (reply.Transcript.Length == 0 && !interrupted))
        {
            return;
        }

        var status = interrupted ? TurnStatus.Interrupted : reply.Status == "completed" ? TurnStatus.Final : TurnStatus.Failed;
        _turns.Add(new LiveTurn(TurnSpeaker.Client, itemId, reply.ContentIndex, reply.Id, reply.Transcript, status, status == TurnStatus.Final));
    }

    /// <summary>Waits until a reply would have finished playing in the browser, plus a short human pause.</summary>
    private static async Task ListenAsync(LiveProviderSession session, ResponseInfo reply, CancellationToken cancellationToken)
    {
        var end = (reply.FirstAudioAt ?? session.Elapsed) + TimeSpan.FromMilliseconds(reply.AudioMilliseconds) + ThinkingPause;
        if (end > session.Elapsed)
        {
            await session.PumpAsync(end - session.Elapsed, cancellationToken);
        }
    }

    private static AdvisorTurn BeginTurn(LiveProviderSession session, int number, AdvisorClip clip) =>
        new(number, clip, session.State.SpeechStarted.Count, session.State.ResponseOrder.Count, session.Speak(clip.Pcm));

    /// <summary>
    /// Waits for an advisor clip to be spoken and answered. Server VAD may split one clip into several turns at pauses;
    /// replies it starts during those pauses are cancelled when the advisor continues, and the reply that counts is the
    /// first one requested after the clip's last segment began.
    /// </summary>
    private async Task<ResponseInfo> CompleteTurnAsync(LiveProviderSession session, AdvisorTurn turn, CancellationToken cancellationToken)
    {
        var state = session.State;
        var label = $"advisor turn {turn.Number}";
        var duration = TimeSpan.FromMilliseconds(turn.Clip.Pcm.Length / 48.0);
        await session.RunUntilAsync(() => turn.Spoken.IsCompleted, duration + ShortWait, $"{turn.Clip.Name} to be spoken", cancellationToken);
        await session.RunUntilAsync(() => state.SpeechStarted.Count > turn.SegmentsBefore, ShortWait, $"server VAD to detect {label}", cancellationToken);
        await session.RunUntilAsync(
            () => state.SpeechStopped.Count >= state.SpeechStarted.Count && FinalReply(state, turn) is not null,
            LongWait, $"the client's reply to {label}", cancellationToken);

        var segments = state.SpeechStarted.Skip(turn.SegmentsBefore).Select(s => s.ItemId).ToList();
        await session.RunUntilAsync(
            () => segments.TrueForAll(id => state.AdvisorTranscripts.ContainsKey(id) || state.AdvisorFailures.ContainsKey(id)),
            LongWait, $"the transcription of {label}", cancellationToken);
        ReportAdvisorSpeech(state, turn, segments);

        var reply = FinalReply(state, turn)!;
        var provisional = state.ResponseOrder.Skip(turn.ResponsesBefore).Select(id => state.Responses[id]).TakeWhile(r => !ReferenceEquals(r, reply)).ToList();
        if (provisional.Count > 0)
        {
            await session.RunUntilAsync(() => provisional.TrueForAll(r => r.Done), ShortWait, $"the provisional replies during {label} to end", cancellationToken);
            ReportProvisionalReplies(label, provisional);
        }

        return reply;
    }

    private static ResponseInfo? FinalReply(ConversationState state, AdvisorTurn turn)
    {
        var lastSegmentStart = state.SpeechStarted[^1].At;
        return state.ResponseOrder.Skip(turn.ResponsesBefore).Select(id => state.Responses[id]).FirstOrDefault(r => r.FirstSeenAt > lastSegmentStart);
    }

    private void ReportAdvisorSpeech(ConversationState state, AdvisorTurn turn, List<string> segments)
    {
        var label = $"advisor turn {turn.Number}";
        var texts = new List<string>();
        var problems = new List<string>();
        foreach (var id in segments)
        {
            if (state.AdvisorTranscripts.TryGetValue(id, out var text) && text.Length > 0)
            {
                texts.Add(text);
                _turns.Add(new LiveTurn(TurnSpeaker.Advisor, id, 0, null, text, TurnStatus.Final, null));
            }
            else
            {
                problems.Add(state.AdvisorFailures.TryGetValue(id, out var failure) ? failure : "empty transcript");
                _turns.Add(new LiveTurn(TurnSpeaker.Advisor, id, 0, null, "", TurnStatus.Failed, null));
            }
        }

        var combined = string.Join(' ', texts);
        if (problems.Count == 0)
        {
            checks.Pass($"{label} transcription", $"\"{combined}\"");
        }
        else if (texts.Count > 0)
        {
            checks.Warn($"{label} transcription", $"\"{combined}\"; {problems.Count} segment(s) without text: {string.Join("; ", problems)}");
        }
        else
        {
            checks.Fail($"{label} transcription", string.Join("; ", problems));
        }

        if (segments.Count > 1)
        {
            checks.Info($"{label} segmentation", $"server VAD split {turn.Clip.Name} into {segments.Count} turns at pauses longer than {realtime.VadSilenceDurationMs} ms");
        }

        var segmentNote = segments.Count > 1 ? $", {segments.Count} VAD segments" : "";
        _conversation.Add($"ADVISOR ({turn.Clip.Name}{segmentNote}): {(combined.Length > 0 ? combined : "[transcription unavailable]")}");
    }

    private void ReportProvisionalReplies(string label, List<ResponseInfo> provisional)
    {
        var outcomes = string.Join(", ", provisional.Select(r => $"{r.Status ?? "unknown"} {r.StatusReason}".Trim()));
        var audible = provisional.Where(r => r.AudioBytes > 0).ToList();
        if (audible.Count == 0)
        {
            checks.Info($"{label} pauses", $"{provisional.Count} provisional reply(ies) started during pauses ended before any audio ({outcomes}), so nothing would be heard");
        }
        else
        {
            checks.Warn($"{label} pauses", $"{audible.Count} provisional reply(ies) produced {audible.Sum(r => r.AudioMilliseconds)} ms of audio before the advisor continued ({outcomes}); the advisor would briefly hear the client start to talk");
        }
    }

    /// <summary>Speaks the next clip while the client's reply is still "playing", then cancels and truncates like the relay.</summary>
    private async Task<AdvisorTurn> InterruptAsync(LiveProviderSession session, ResponseInfo reply, int number, AdvisorClip clip, CancellationToken cancellationToken)
    {
        var state = session.State;
        await session.RunUntilAsync(() => reply.FirstAudioAt is not null || reply.Done, LongWait, "the first audio of the reply to interrupt", cancellationToken);
        if (reply.FirstAudioAt is not { } firstAudio)
        {
            checks.Fail("interruption", $"the reply to interrupt ended ({reply.Status} {reply.StatusReason}) without audio");
            return BeginTurn(session, number, clip);
        }

        await session.PumpAsync(InterruptAfter, cancellationToken);
        var turn = BeginTurn(session, number, clip);
        await session.RunUntilAsync(() => state.SpeechStarted.Count > turn.SegmentsBefore, ShortWait, $"speech_started for the interruption (advisor turn {number})", cancellationToken);

        // Played position = wall-clock time since the reply's audio began, bounded by the audio actually received.
        var played = (int)Math.Clamp((session.Elapsed - firstAudio).TotalMilliseconds, 0, reply.AudioMilliseconds);
        if (!reply.Done)
        {
            await session.SendAsync(ProviderClientEvents.ResponseCancel(reply.Id), cancellationToken);
        }

        if (reply.ItemId is { } itemId)
        {
            await session.SendAsync(ProviderClientEvents.Truncate(itemId, reply.ContentIndex, played), cancellationToken);
            await session.RunUntilAsync(() => state.Truncations.Exists(t => t.ItemId == itemId), ShortWait, "conversation.item.truncated", cancellationToken);
            var acknowledged = state.Truncations.Find(t => t.ItemId == itemId).AudioEndMs;
            checks.Pass("interruption truncation", $"Azure truncated the client's reply at {acknowledged} ms ({played} ms heard of {reply.AudioMilliseconds} ms generated so far)");
        }
        else
        {
            checks.Fail("interruption truncation", "the interrupted reply had no item id");
        }

        await session.RunUntilAsync(() => reply.Done, ShortWait, "response.done for the interrupted reply", cancellationToken);
        checks.Info("interrupted reply", reply.Status == "cancelled"
            ? $"cancelled ({reply.StatusReason ?? "no reason"}) while still generating, after {reply.AudioMilliseconds} ms of audio"
            : $"{reply.Status}: generation had already finished ({reply.AudioMilliseconds} ms), so the truncation is what removes the unheard part");
        _conversation.Add($"CLIENT (interrupted after ~{played} ms of {reply.AudioMilliseconds} ms): {reply.Transcript}");
        AddClientTurn(reply, interrupted: true);
        return turn;
    }

    private sealed record AdvisorTurn(int Number, AdvisorClip Clip, int SegmentsBefore, int ResponsesBefore, Task Spoken);

    private void VerifyEffectiveSession(JsonElement session)
    {
        var echoed = Json.Str(session, "instructions");
        if (echoed is null)
        {
            checks.Warn("system instructions", "session.updated did not echo the instructions, so they cannot be compared");
        }
        else if (string.Equals(echoed, instructions, StringComparison.Ordinal))
        {
            checks.Pass("system instructions", $"Azure applied exactly the instructions sent ({instructions.Length} chars, sha256 {TextTools.Hash(instructions)}, {RoleplayPromptBuilder.Version})");
        }
        else if (PromptFingerprint.SameIgnoringWhitespace(echoed, instructions))
        {
            checks.Pass("system instructions", "Azure applied the instructions sent (identical after whitespace normalization)");
        }
        else
        {
            checks.Fail("system instructions", $"echo differs: sent {instructions.Length} chars ({TextTools.Hash(instructions)}), Azure reports {echoed.Length} chars ({TextTools.Hash(echoed)})");
        }

        checks.Info("effective session", $"type={Json.Str(session, "type") ?? "-"}, model={Json.Str(session, "model") ?? "-"}, output_modalities=[{Json.Strings(session, "output_modalities")}]");
        var audio = Json.Obj(session, "audio");
        var input = Json.Obj(audio, "input");
        var output = Json.Obj(audio, "output");
        CheckFormat("input audio format", Json.Obj(input, "format"));
        CheckFormat("output audio format", Json.Obj(output, "format"));
        var vad = Json.Obj(input, "turn_detection");
        var vadText = $"type={Json.Str(vad, "type") ?? "-"}, threshold={Json.Num(vad, "threshold")}, prefix_padding_ms={Json.Int(vad, "prefix_padding_ms")}, " +
                      $"silence_duration_ms={Json.Int(vad, "silence_duration_ms")}, create_response={Json.Bool(vad, "create_response")}, interrupt_response={Json.Bool(vad, "interrupt_response")}";
        if (Json.Str(vad, "type") == "server_vad" && Json.Bool(vad, "create_response") == true && Json.Bool(vad, "interrupt_response") == true)
        {
            checks.Pass("turn detection", vadText);
        }
        else
        {
            checks.Fail("turn detection", vadText);
        }

        var transcription = Json.Str(Json.Obj(input, "transcription"), "model");
        if (transcription == expectedTranscriptionModel)
        {
            checks.Pass("input transcription model", transcription);
        }
        else
        {
            checks.Warn("input transcription model", $"requested {expectedTranscriptionModel}, Azure reports {transcription ?? "(none)"}");
        }

        checks.Info("output voice", Json.Str(output, "voice") ?? "(not reported)");
    }

    private void CheckFormat(string name, JsonElement format)
    {
        var rate = Json.Int(format, "rate");
        var text = $"{Json.Str(format, "type") ?? "-"} @ {rate?.ToString(CultureInfo.InvariantCulture) ?? "-"} Hz";
        if (Json.Str(format, "type") == "audio/pcm" && rate == 24000)
        {
            checks.Pass(name, text);
        }
        else
        {
            checks.Fail(name, text);
        }
    }

    private void VerifyResponse(string name, ResponseInfo response)
    {
        if (response.Status == "completed")
        {
            checks.Pass($"{name} status", "completed");
        }
        else
        {
            checks.Fail($"{name} status", $"{response.Status ?? "unknown"} {response.StatusReason}".Trim());
        }

        if (response.AudioBytes > 0 && !response.OddAudioChunk)
        {
            checks.Pass($"{name} audio", $"{response.AudioMilliseconds} ms of 24 kHz PCM16 audio");
        }
        else
        {
            checks.Fail($"{name} audio", response.AudioBytes == 0 ? "no audio received" : "an audio chunk had an odd byte count");
        }

        if (response.Transcript.Length > 0)
        {
            checks.Pass($"{name} transcript", $"\"{response.Transcript}\"");
        }
        else
        {
            checks.Fail($"{name} transcript", "no output audio transcript");
        }
    }

    private void ReportLatency(ConversationState state, ResponseInfo reply, int turn)
    {
        var stopped = state.SpeechStopped.Where(s => s.At <= reply.FirstSeenAt).Select(s => s.At).DefaultIfEmpty(TimeSpan.Zero).Max();
        if (reply.FirstAudioAt is { } firstAudio && stopped > TimeSpan.Zero)
        {
            checks.Info($"reply {turn} latency", $"{(firstAudio - stopped).TotalMilliseconds:F0} ms from VAD end-of-speech to the first client audio from Azure (server-side only, after the 500 ms silence window; excludes browser playback)");
        }
    }

    private void ReportProtocol(LiveProviderSession session)
    {
        var state = session.State;
        if (session.ParserFailures.Count == 0)
        {
            checks.Pass("application event parser", $"every real Azure event parsed with the app's GA parser ({state.TotalEvents} events processed)");
        }
        else
        {
            checks.Fail("application event parser", string.Join("; ", session.ParserFailures.Distinct()));
        }

        checks.Info("event types seen", string.Join(", ", state.EventCounts.Select(pair => $"{pair.Key} x{pair.Value}")));
        if (state.BenignErrors.Count > 0)
        {
            checks.Info("benign provider errors", string.Join(", ", state.BenignErrors));
        }

        checks.Info("microphone", $"{session.MicrophoneFrames} real-time 20 ms frames streamed");
    }

    private void PrintConversation()
    {
        Console.WriteLine();
        Console.WriteLine("Conversation (transcripts returned by Azure):");
        foreach (var line in _conversation)
        {
            Console.WriteLine("  " + line);
        }
    }
}
