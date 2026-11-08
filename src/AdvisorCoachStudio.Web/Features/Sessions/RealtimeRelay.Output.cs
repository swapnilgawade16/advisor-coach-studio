using AdvisorCoachStudio.Web.Contracts;
using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;

namespace AdvisorCoachStudio.Web.Features.Sessions;

/// <summary>Output audio tracking, pacing, playback progress, interruption, and truncation.</summary>
public sealed partial class RealtimeRelay
{
    private const int PlaybackToleranceMs = 40;

    private sealed class OutputResponse
    {
        public required string Id { get; init; }
        public bool Done { get; set; }
        public bool Cleared { get; set; }
        public bool AudioAnnounced { get; set; }
        public string? Status { get; set; }
    }

    private sealed class OutputItem
    {
        public required string ResponseId { get; init; }
        public required string ItemId { get; init; }
        public required int ContentIndex { get; init; }
        public Queue<byte[]> Pending { get; } = new();
        public long PendingBytes { get; set; }
        public long SentBytes { get; set; }
        public int PlayedMs { get; set; }
        public bool GenerationDone { get; set; }
        public bool PlaybackDone { get; set; }
        public bool Cleared { get; set; }
        public int SentMs => AppProtocol.BytesToMilliseconds(SentBytes);
        public bool HasUnheardAudio => Pending.Count > 0 || SentMs > PlayedMs + PlaybackToleranceMs || !GenerationDone;
    }

    private sealed record PendingTruncation(OutputItem Item, DateTimeOffset Deadline);

    private readonly Dictionary<string, OutputResponse> _responses = new(StringComparer.Ordinal);
    private readonly List<OutputItem> _outputItems = [];
    private readonly Dictionary<(string ItemId, int ContentIndex), OutputItem> _outputByKey = [];
    private readonly List<PendingTruncation> _pendingTruncations = [];
    private long _serverSequence;
    private int _interruptions;
    private long _lateAudioDeltas;
    private int _ignoredProgress;

    private bool IsResponseCleared(string responseId) => _responses.TryGetValue(responseId, out var response) && response.Cleared;

    private OutputResponse GetOrCreateResponse(string responseId)
    {
        if (!_responses.TryGetValue(responseId, out var response))
        {
            response = new OutputResponse { Id = responseId };
            _responses[responseId] = response;
        }

        return response;
    }

    private void OnResponseCreated(string responseId)
    {
        var response = GetOrCreateResponse(responseId);
        if (_finalizing)
        {
            // Never generate another AI reply while ending.
            response.Cleared = true;
            EnqueueProvider(ProviderClientEvents.ResponseCancel(responseId));
        }
    }

    private void OnAudioDelta(ProviderEvent evt)
    {
        var response = GetOrCreateResponse(evt.ResponseId!);
        if (response.Cleared || _finalizing)
        {
            _lateAudioDeltas++;
            return;
        }

        byte[] pcm;
        try
        {
            pcm = Convert.FromBase64String(evt.Delta!);
        }
        catch (FormatException)
        {
            pcm = [1];
        }

        if (pcm.Length % 2 != 0)
        {
            RequestEnd(SessionEndReason.ProviderError, "provider_protocol_error", "The voice service sent invalid audio. Your completed turns were kept.", true);
            return;
        }

        if (pcm.Length == 0)
        {
            return;
        }

        if (!_outputByKey.TryGetValue((evt.ItemId!, evt.ContentIndex), out var item))
        {
            item = new OutputItem { ResponseId = response.Id, ItemId = evt.ItemId!, ContentIndex = evt.ContentIndex };
            _outputByKey[(item.ItemId, item.ContentIndex)] = item;
            _outputItems.Add(item);
        }

        if (item.Cleared)
        {
            _lateAudioDeltas++;
            return;
        }

        // Re-chunk on sample boundaries so each browser message stays small.
        for (var offset = 0; offset < pcm.Length; offset += AppProtocol.MaxOutputChunkBytes)
        {
            var length = Math.Min(AppProtocol.MaxOutputChunkBytes, pcm.Length - offset);
            item.Pending.Enqueue(pcm.AsSpan(offset, length).ToArray());
            item.PendingBytes += length;
        }

        if (item.PendingBytes > (long)_deps.Options.MaxBufferedResponseSeconds * 1000 * AppProtocol.BytesPerMillisecond)
        {
            RequestEnd(SessionEndReason.ProviderError, "provider_audio_overflow", "The voice service sent more audio than expected. Your completed turns were kept.", true);
            return;
        }

        _assembler.OnOutputAudio(response.Id, item.ItemId, item.ContentIndex);
        ReleaseOutputAudio();
    }

    private void OnAudioDone(string responseId, string itemId, int contentIndex)
    {
        if (_outputByKey.TryGetValue((itemId, contentIndex), out var item) && item.ResponseId == responseId)
        {
            item.GenerationDone = true;
            CheckItemPlayback(item);
        }
    }

    private void OnResponseDone(ProviderEvent evt)
    {
        var response = GetOrCreateResponse(evt.ResponseId!);
        response.Done = true;
        response.Status = evt.ResponseStatus;
        foreach (var item in _outputItems.Where(i => i.ResponseId == response.Id))
        {
            item.GenerationDone = true;
        }

        var outputs = evt.ResponseOutput
            .Where(part => part.Role is null or "assistant")
            .Select(part => (part.ItemId, part.ContentIndex, part.Transcript))
            .ToList();
        Forward(_assembler.OnResponseDone(response.Id, evt.ResponseStatus ?? "completed", evt.ResponseStatusReason, outputs));

        if (evt.InputTokens is not null || evt.OutputTokens is not null)
        {
            _logger.LogDebug("Response usage: {InputTokens} input / {OutputTokens} output tokens.", evt.InputTokens, evt.OutputTokens);
        }

        if (_mode == RelayMode.Demonstration && !_finalizing && evt.ResponseStatus is "failed" or "incomplete" && !response.Cleared)
        {
            RequestEnd(SessionEndReason.ProviderError, "demonstration_failed", "The demonstration could not be completed. Please try again.", true);
            return;
        }

        foreach (var item in _outputItems.Where(i => i.ResponseId == response.Id))
        {
            CheckItemPlayback(item);
        }

        CheckSpeakingState();
    }

    /// <summary>Releases buffered provider audio so that at most MaxQueuedOutputMs is unplayed in the browser.</summary>
    private void ReleaseOutputAudio()
    {
        if (_finalizing || !_active)
        {
            return;
        }

        var unplayed = 0;
        foreach (var item in _outputItems)
        {
            if (!item.Cleared && !item.PlaybackDone)
            {
                unplayed += Math.Max(0, item.SentMs - item.PlayedMs);
            }
        }

        foreach (var item in _outputItems)
        {
            if (item.Cleared || item.Pending.Count == 0)
            {
                continue;
            }

            while (item.Pending.Count > 0 && unplayed + AppProtocol.BytesToMilliseconds(item.Pending.Peek().Length) <= _deps.Options.MaxQueuedOutputMs)
            {
                var chunk = item.Pending.Peek();
                var chunkMs = AppProtocol.BytesToMilliseconds(chunk.Length);
                var payload = AppMessages.AudioDelta(_serverSequence + 1, item.ResponseId, item.ItemId, item.ContentIndex, chunk);
                if (!_browserOut.TryEnqueue(payload, chunkMs))
                {
                    RequestEnd(SessionEndReason.ConnectionQuality, "connection_quality", "Audio could not be delivered to your browser quickly enough. Your completed turns were kept.", true);
                    return;
                }

                _serverSequence++;
                item.Pending.Dequeue();
                item.PendingBytes -= chunk.Length;
                item.SentBytes += chunk.Length;
                unplayed += chunkMs;
                var response = GetOrCreateResponse(item.ResponseId);
                if (!response.AudioAnnounced)
                {
                    response.AudioAnnounced = true;
                    SendState("client_speaking");
                }
            }

            if (item.Pending.Count > 0)
            {
                break; // Preserve item order.
            }
        }
    }

    private void HandlePlaybackPosition(PlaybackPositionMessage position)
    {
        if (!_outputByKey.TryGetValue((position.ItemId, position.ContentIndex), out var item) || item.ResponseId != position.ResponseId)
        {
            _ignoredProgress++;
            return;
        }

        // Untrusted client telemetry: monotonic per item and bounded by audio actually sent.
        item.PlayedMs = Math.Clamp(position.PlayedMilliseconds, item.PlayedMs, Math.Max(item.PlayedMs, item.SentMs));
        if (position.Stopped)
        {
            ResolveTruncation(item);
        }

        CheckItemPlayback(item);
        ReleaseOutputAudio();
    }

    private void CheckItemPlayback(OutputItem item)
    {
        if (item.PlaybackDone || item.Cleared || !item.GenerationDone || item.Pending.Count > 0 || item.PlayedMs < item.SentMs - PlaybackToleranceMs)
        {
            return;
        }

        item.PlaybackDone = true;
        _assembler.MarkPlaybackComplete(item.ItemId, item.ContentIndex);
        CheckSpeakingState();
    }

    private void CheckSpeakingState()
    {
        if (_finalizing || _responses.Count == 0)
        {
            return;
        }

        var speaking = _outputItems.Any(i => !i.Cleared && !i.PlaybackDone) || _responses.Values.Any(r => !r.Done && !r.Cleared && r.AudioAnnounced);
        if (speaking)
        {
            return;
        }

        if (_mode == RelayMode.Demonstration)
        {
            if (_responses.Values.All(r => r.Done))
            {
                RequestEnd(SessionEndReason.Completed, null, null, false);
            }
        }
        else if (_pendingTruncations.Count == 0)
        {
            SendState("listening");
        }
    }

    /// <summary>Advisor speech started: stop client playback, cancel the response, and truncate to what was heard.</summary>
    private void HandleInterruption()
    {
        if (_finalizing)
        {
            return;
        }

        var interrupted = false;
        foreach (var response in _responses.Values)
        {
            if (response.Cleared || (response.Done && _outputItems.All(i => i.ResponseId != response.Id || i.PlaybackDone || i.Cleared)))
            {
                continue;
            }

            var audible = _outputItems.Where(i => i.ResponseId == response.Id && !i.Cleared && !i.PlaybackDone && i.HasUnheardAudio).ToList();
            response.Cleared = true;
            if (!response.Done)
            {
                EnqueueProvider(ProviderClientEvents.ResponseCancel(response.Id));
            }

            foreach (var item in audible)
            {
                ClearItem(item);
                _pendingTruncations.Add(new PendingTruncation(item, Now + TimeSpan.FromSeconds(1)));
                interrupted = true;
            }

            // Transcript-only turns of this response (text that arrived before any audio) were never heard either.
            Forward(_assembler.MarkResponseNotFullyHeard(response.Id));
        }

        if (interrupted)
        {
            _interruptions++;
            RelayMetrics.Interruptions.Add(1);
            SendState("interrupted");
        }
    }

    private void ClearItem(OutputItem item)
    {
        item.Cleared = true;
        item.Pending.Clear();
        item.PendingBytes = 0;
        _browserOut.TryEnqueue(AppMessages.PlaybackClear(item.ResponseId, item.ItemId), critical: true);
        Forward(_assembler.MarkNotFullyHeard(item.ItemId, item.ContentIndex));
    }

    private void ResolveTruncation(OutputItem item)
    {
        var pending = _pendingTruncations.Find(p => ReferenceEquals(p.Item, item));
        if (pending is null)
        {
            return;
        }

        _pendingTruncations.Remove(pending);
        SendTruncate(item);
    }

    private void ProcessPendingTruncations(DateTimeOffset now)
    {
        foreach (var pending in _pendingTruncations.Where(p => p.Deadline <= now).ToList())
        {
            // No playback.stopped arrived in time: use the last reported played position.
            _pendingTruncations.Remove(pending);
            SendTruncate(pending.Item);
        }
    }

    private void SendTruncate(OutputItem item)
    {
        EnqueueProvider(ProviderClientEvents.Truncate(item.ItemId, item.ContentIndex, Math.Clamp(item.PlayedMs, 0, item.SentMs)));
        if (_pendingTruncations.Count == 0 && _lastState == "interrupted" && !_finalizing)
        {
            SendState("listening");
        }
    }
}
