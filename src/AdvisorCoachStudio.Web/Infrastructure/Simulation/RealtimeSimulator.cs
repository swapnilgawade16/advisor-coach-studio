using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using AdvisorCoachStudio.Web.Infrastructure.AzureRealtime;
using AdvisorCoachStudio.Web.Infrastructure.WebSockets;

namespace AdvisorCoachStudio.Web.Infrastructure.Simulation;

/// <summary>
/// DevelopmentSimulation provider: speaks the Azure GA realtime event protocol over an in-memory socket so the UI can be
/// exercised without Azure. Client speech is a synthetic tone with scripted, clearly-labelled text; advisor speech is
/// detected by a simple energy gate and is NOT transcribed (a labelled placeholder is used instead).
/// </summary>
public sealed partial class RealtimeSimulator(WebSocket socket, ILogger logger)
{
    private static readonly string[] ScriptedReplies =
    [
        "[Simulated client] Thank you for listening. Can you help me understand what that would mean for me?",
        "[Simulated client] That helps a little. What would you suggest we look at first?",
        "[Simulated client] Okay. I'd like to understand that properly before deciding anything.",
        "[Simulated client] Thanks. Could we agree on a sensible next step?",
    ];

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _response;
    private string? _responseId;
    private string? _lastItemId;
    private int _responses;
    private int _advisorTurns;
    private bool _demonstration;
    private string _openingLine = "[Simulated client] Hello. I wanted to talk something through with you.";
    private long _inputMs;
    private int _loudChunks;
    private int _quietChunks;
    private string? _speechItemId;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(new { type = "session.created", event_id = Id("event"), session = new { id = Id("sess"), type = "realtime" } }, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await WebSocketMessageReader.ReadAsync(socket, 16 * 1024 * 1024, cancellationToken);
                if (message is null)
                {
                    break;
                }

                using var document = JsonDocument.Parse(message.Value.Payload);
                var root = document.RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "session.update":
                    {
                        var session = root.GetProperty("session");
                        ConfigureFrom(session);
                        // Like Azure, echo the effective instructions so the relay's applied-instructions check runs in simulation too.
                        var instructions = session.TryGetProperty("instructions", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : null;
                        await SendAsync(new { type = "session.updated", event_id = Id("event"), session = new { id = Id("sess"), type = "realtime", instructions } }, cancellationToken);
                        break;
                    }

                    case "response.create":
                        StartResponse(_responses == 0 ? _openingLine : NextReply(), cancellationToken);
                        break;
                    case "response.cancel":
                        await CancelResponseAsync("client_cancelled", cancellationToken);
                        break;
                    case "input_audio_buffer.append":
                        await OnAudioAsync(root.GetProperty("audio").GetString()!, cancellationToken);
                        break;
                    case "input_audio_buffer.commit":
                        if (_speechItemId is not null)
                        {
                            await FinishAdvisorTurnAsync(respond: false, cancellationToken);
                        }
                        else
                        {
                            await SendAsync(new { type = "error", event_id = Id("event"), error = new { type = "invalid_request_error", code = "input_audio_buffer_commit_empty", message = "Buffer is empty." } }, cancellationToken);
                        }

                        break;
                    case "conversation.item.truncate":
                        await SendAsync(new
                        {
                            type = "conversation.item.truncated",
                            event_id = Id("event"),
                            item_id = root.GetProperty("item_id").GetString(),
                            content_index = root.GetProperty("content_index").GetInt32(),
                            audio_end_ms = root.GetProperty("audio_end_ms").GetInt32(),
                        }, cancellationToken);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Realtime simulator stopped unexpectedly.");
        }
        finally
        {
            _response?.Cancel();
            socket.Dispose();
        }
    }

    private void ConfigureFrom(JsonElement session)
    {
        _demonstration = session.TryGetProperty("audio", out var audio) && audio.TryGetProperty("input", out var input) &&
                         input.TryGetProperty("turn_detection", out var detection) && detection.ValueKind == JsonValueKind.Null;
        var instructions = session.TryGetProperty("instructions", out var i) ? i.GetString() ?? "" : "";
        if (_demonstration)
        {
            _openingLine = "[Simulated demonstration] Here I'll acknowledge the concern before asking a question. I can hear this has been worrying you. Before we decide anything, could you tell me what matters most to you right now?";
        }
        else if (OpeningPattern().Match(instructions) is { Success: true } match)
        {
            _openingLine = "[Simulated client] " + match.Groups[1].Value;
        }
    }

    private string NextReply() => ScriptedReplies[Math.Max(0, _responses - 1) % ScriptedReplies.Length];

    private async Task OnAudioAsync(string base64, CancellationToken ct)
    {
        if (_demonstration)
        {
            return;
        }

        var bytes = Convert.FromBase64String(base64);
        _inputMs += bytes.Length / 48;
        double sum = 0;
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            var sample = (short)(bytes[i] | (bytes[i + 1] << 8)) / 32768.0;
            sum += sample * sample;
        }

        var loud = Math.Sqrt(sum / Math.Max(1, bytes.Length / 2)) > 0.02;
        _loudChunks = loud ? _loudChunks + 1 : 0;
        _quietChunks = loud ? 0 : _quietChunks + 1;
        if (_speechItemId is null && _loudChunks >= 3)
        {
            _speechItemId = Id("item");
            await SendAsync(new { type = "input_audio_buffer.speech_started", event_id = Id("event"), audio_start_ms = _inputMs, item_id = _speechItemId }, ct);
            if (_response is not null)
            {
                await CancelResponseAsync("turn_detected", ct);
            }
        }
        else if (_speechItemId is not null && _quietChunks >= 30)
        {
            await FinishAdvisorTurnAsync(respond: true, ct);
        }
    }

    private async Task FinishAdvisorTurnAsync(bool respond, CancellationToken ct)
    {
        var itemId = _speechItemId!;
        _speechItemId = null;
        _advisorTurns++;
        await SendAsync(new { type = "input_audio_buffer.speech_stopped", event_id = Id("event"), audio_end_ms = _inputMs, item_id = itemId }, ct);
        await SendAsync(new { type = "input_audio_buffer.committed", event_id = Id("event"), previous_item_id = _lastItemId, item_id = itemId }, ct);
        await SendAsync(new { type = "conversation.item.added", event_id = Id("event"), previous_item_id = _lastItemId, item = new { id = itemId, type = "message", role = "user", status = "completed", content = new[] { new { type = "input_audio", transcript = (string?)null } } } }, ct);
        _lastItemId = itemId;
        await SendAsync(new
        {
            type = "conversation.item.input_audio_transcription.completed",
            event_id = Id("event"),
            item_id = itemId,
            content_index = 0,
            transcript = $"[Simulated advisor turn {_advisorTurns}: speech is not transcribed in simulation mode]",
        }, ct);
        if (respond)
        {
            StartResponse(NextReply(), ct);
        }
    }

    private void StartResponse(string text, CancellationToken ct)
    {
        _responses++;
        _response?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _response = cts;
        var responseId = Id("resp");
        var itemId = Id("item");
        _responseId = responseId;
        _ = Task.Run(() => StreamResponseAsync(responseId, itemId, text, cts.Token), CancellationToken.None);
    }

    private async Task StreamResponseAsync(string responseId, string itemId, string text, CancellationToken ct)
    {
        try
        {
            var item = new { id = itemId, type = "message", role = "assistant", status = "in_progress" };
            await SendAsync(new { type = "response.created", event_id = Id("event"), response = new { id = responseId, status = "in_progress" } }, ct);
            await SendAsync(new { type = "response.output_item.added", event_id = Id("event"), response_id = responseId, output_index = 0, item }, ct);
            await SendAsync(new { type = "conversation.item.added", event_id = Id("event"), previous_item_id = _lastItemId, item }, ct);
            _lastItemId = itemId;
            var audio = Synthesize(Math.Clamp(text.Length * 45, 1500, 7000));
            var words = text.Split(' ');
            var chunkBytes = 4800; // 100 ms
            var chunks = (audio.Length + chunkBytes - 1) / chunkBytes;
            var wordIndex = 0;
            for (var c = 0; c < chunks; c++)
            {
                var length = Math.Min(chunkBytes, audio.Length - (c * chunkBytes));
                await SendAsync(new { type = "response.output_audio.delta", event_id = Id("event"), response_id = responseId, item_id = itemId, output_index = 0, content_index = 0, delta = Convert.ToBase64String(audio, c * chunkBytes, length) }, ct);
                var targetWords = (int)Math.Ceiling((c + 1) * words.Length / (double)chunks);
                while (wordIndex < targetWords)
                {
                    await SendAsync(new { type = "response.output_audio_transcript.delta", event_id = Id("event"), response_id = responseId, item_id = itemId, output_index = 0, content_index = 0, delta = (wordIndex == 0 ? "" : " ") + words[wordIndex] }, ct);
                    wordIndex++;
                }

                await Task.Delay(50, ct);
            }

            await SendAsync(new { type = "response.output_audio.done", event_id = Id("event"), response_id = responseId, item_id = itemId, output_index = 0, content_index = 0 }, ct);
            await SendAsync(new { type = "response.output_audio_transcript.done", event_id = Id("event"), response_id = responseId, item_id = itemId, output_index = 0, content_index = 0, transcript = text }, ct);
            await SendAsync(new
            {
                type = "response.done",
                event_id = Id("event"),
                response = new
                {
                    id = responseId,
                    status = "completed",
                    output = new[] { new { id = itemId, type = "message", role = "assistant", status = "completed", content = new[] { new { type = "output_audio", transcript = text } } } },
                    usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 },
                },
            }, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is WebSocketException or IOException)
        {
        }
    }

    private async Task CancelResponseAsync(string reason, CancellationToken ct)
    {
        if (_response is null || _responseId is null)
        {
            await SendAsync(new { type = "error", event_id = Id("event"), error = new { type = "invalid_request_error", code = "response_cancel_not_active", message = "No active response." } }, ct);
            return;
        }

        await _response.CancelAsync();
        _response = null;
        var responseId = _responseId;
        _responseId = null;
        await SendAsync(new { type = "response.done", event_id = Id("event"), response = new { id = responseId, status = "cancelled", status_details = new { type = "cancelled", reason }, output = Array.Empty<object>() } }, ct);
    }

    /// <summary>Soft, obviously synthetic two-tone hum with a syllable-like envelope (PCM16 LE, 24 kHz).</summary>
    private static byte[] Synthesize(int milliseconds)
    {
        var samples = milliseconds * 24;
        var bytes = new byte[samples * 2];
        for (var n = 0; n < samples; n++)
        {
            var t = n / 24000.0;
            var envelope = 0.5 - (0.5 * Math.Cos(2 * Math.PI * 3.5 * t));
            var value = 0.06 * envelope * (Math.Sin(2 * Math.PI * 196 * t) + (0.5 * Math.Sin(2 * Math.PI * 294 * t)));
            var sample = (short)Math.Clamp(value * 32767, short.MinValue, short.MaxValue);
            bytes[2 * n] = (byte)(sample & 0xFF);
            bytes[(2 * n) + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return bytes;
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await _sendLock.WaitAsync(ct);
        try
        {
            if (socket.State == WebSocketState.Open)
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            }
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static string Id(string prefix) => $"{prefix}_sim_{Guid.NewGuid():N}"[..(prefix.Length + 17)];

    [GeneratedRegex("Open the conversation naturally with this line, keeping its meaning and tone:\\s*\"([^\"]+)\"")]
    private static partial Regex OpeningPattern();
}

/// <summary>DevelopmentSimulation connector. Only registered when Ai:Mode=DevelopmentSimulation in Development.</summary>
public sealed class SimulatedRealtimeConnector(ILoggerFactory loggers) : IRealtimeProviderConnector
{
    public AzureRealtimeSessionSettings SessionSettings => new("simulation", null);

    public Task<WebSocket> ConnectAsync(CancellationToken cancellationToken)
    {
        var (client, server) = InMemoryWebSocketPair.Create();
        var simulator = new RealtimeSimulator(server, loggers.CreateLogger<RealtimeSimulator>());
        _ = Task.Run(() => simulator.RunAsync(CancellationToken.None), CancellationToken.None);
        return Task.FromResult(client);
    }
}
