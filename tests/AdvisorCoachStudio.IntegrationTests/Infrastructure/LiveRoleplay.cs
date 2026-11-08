namespace AdvisorCoachStudio.IntegrationTests.Infrastructure;

/// <summary>Client speech as the browser received it: its audio chunks and final caption.</summary>
public sealed record HeardResponse(IReadOnlyList<JsonMessage> Audio, JsonMessage Final);

/// <summary>An advisor turn: microphone audio the browser sent, what the provider received, and the caption that proves the relay processed it.</summary>
public sealed record AdvisorTurn(string ItemId, string Transcript, IReadOnlyList<string> SentAudio, IReadOnlyList<string> ForwardedAudio, JsonMessage Caption);

/// <summary>
/// Drives one live roleplay end to end: the browser side through <see cref="AppSocket"/> and the provider side through
/// <see cref="FakeRealtimeConnection"/>. Every step waits for a specific observable message, never for a fixed delay.
/// </summary>
public sealed class LiveRoleplay : IAsyncDisposable
{
    private int _advisorTurns;

    private LiveRoleplay(TestApp app, TestUser user, Guid sessionId, AppSocket socket, FakeRealtimeConnection provider, JsonMessage ready)
    {
        App = app;
        User = user;
        SessionId = sessionId;
        Socket = socket;
        Provider = provider;
        Ready = ready;
    }

    public TestApp App { get; }

    public TestUser User { get; }

    public Guid SessionId { get; }

    public AppSocket Socket { get; }

    public FakeRealtimeConnection Provider { get; }

    public JsonMessage Ready { get; }

    /// <summary>Creates a session, connects the browser socket, and waits for session.ready.</summary>
    public static async Task<LiveRoleplay> StartAsync(TestApp app, TestUser user, string scenarioId = "market-decline", string difficulty = "intermediate") =>
        await ConnectAsync(app, user, await user.CreateSessionAsync(scenarioId, difficulty));

    /// <summary>Connects the browser socket to an existing session and waits for session.ready.</summary>
    public static async Task<LiveRoleplay> ConnectAsync(TestApp app, TestUser user, Guid sessionId)
    {
        var connectionIndex = app.Fake.Connections.Count;
        var socket = await user.ConnectAsync($"/ws/sessions/{sessionId}");
        try
        {
            var ready = await socket.WaitForAsync("session.ready");
            var provider = await app.Fake.WaitForConnectionAsync(connectionIndex);
            return new LiveRoleplay(app, user, sessionId, socket, provider, ready);
        }
        catch
        {
            await socket.DisposeAsync();
            throw;
        }
    }

    /// <summary>Waits for a client response's audio and final caption, then (optionally) reports full playback and waits for "listening".</summary>
    public async Task<HeardResponse> HearAsync(ScriptedResponse script, bool reportPlayback = true)
    {
        var audio = await Socket.WaitForCountAsync("audio.delta", script.AudioChunks, m => m.String("responseId") == script.ResponseId);
        var final = await Socket.WaitForAsync("transcript.final", m => m.String("speaker") == "client" && m.String("text") == script.Transcript);
        if (reportPlayback)
        {
            await Socket.SendPlaybackProgressAsync(script.ResponseId, script.ItemId, script.TotalMilliseconds);
            await Socket.WaitForStateAsync("listening", afterIndex: audio[0].Index);
        }

        return new HeardResponse(audio, final);
    }

    /// <summary>The provider streams a VAD-triggered client response (no response.create from the relay), and the browser plays it.</summary>
    public async Task<HeardResponse> ReplyAsync(ScriptedResponse script, bool reportPlayback = true)
    {
        await Provider.StreamResponseAsync(script);
        return await HearAsync(script, reportPlayback);
    }

    /// <summary>
    /// Streams microphone frames from the browser, waits until the provider received them, has the fake provider commit
    /// them as one advisor turn (server VAD), and either completes the input transcription or streams a first partial.
    /// Either way the browser observes a caption, which proves the relay processed the commit (a sync point for tests).
    /// </summary>
    public async Task<AdvisorTurn> SpeakAsync(string transcript, int frames = 10, bool completeTranscription = true)
    {
        var number = ++_advisorTurns;
        var itemId = $"item_user_{number}";
        var appendsBefore = Provider.ReceivedOfType("input_audio_buffer.append").Count;
        var sent = new List<string>();
        for (var i = 0; i < frames; i++)
        {
            sent.Add(await Socket.SendAudioAsync(TestAudio.MicrophoneFrame((number * 100) + i)));
        }

        var appends = await Provider.WaitForCountAsync("input_audio_buffer.append", appendsBefore + frames);
        var forwarded = appends.Skip(appendsBefore).Select(a => a.String("audio")!).ToList();
        await Provider.CommitAdvisorTurnAsync(itemId, frames * 20);
        JsonMessage caption;
        if (completeTranscription)
        {
            caption = await CompleteTranscriptionAsync(itemId, transcript);
        }
        else
        {
            var firstWord = FirstWord(transcript);
            await Provider.SendInputTranscriptionDeltaAsync(itemId, firstWord);
            caption = await Socket.WaitForAsync("transcript.partial", m => m.String("speaker") == "advisor" && m.String("text") == firstWord);
        }

        return new AdvisorTurn(itemId, transcript, sent, forwarded, caption);
    }

    public static string FirstWord(string transcript) => transcript.Split(' ')[0];

    public async Task<JsonMessage> CompleteTranscriptionAsync(string itemId, string transcript)
    {
        await Provider.SendInputTranscriptionCompletedAsync(itemId, transcript);
        return await Socket.WaitForAsync("transcript.final", m => m.String("speaker") == "advisor" && m.String("text") == transcript);
    }

    /// <summary>Opening, then <paramref name="exchanges"/> advisor/client exchanges, each fully played.</summary>
    public async Task PlayConversationAsync(int exchanges)
    {
        string[] advisor = [Lines.Advisor1, Lines.Advisor2, Lines.Advisor3];
        string[] client = [Lines.Client1, Lines.Client2, Lines.Client3];
        await HearAsync(Scripts.Opening);
        for (var i = 0; i < exchanges; i++)
        {
            await SpeakAsync(advisor[i]);
            await ReplyAsync(Scripts.Reply(i + 1, client[i]));
        }
    }

    /// <summary>Sends session.end, waits for session.ended, and waits for the server to close the socket.</summary>
    public async Task<JsonMessage> EndAsync()
    {
        var mark = Socket.LastIndex;
        await Socket.SendEndAsync();
        var ended = await Socket.WaitForAsync("session.ended", afterIndex: mark);
        await Socket.WaitForClosedAsync();
        return ended;
    }

    public ValueTask DisposeAsync() => Socket.DisposeAsync();
}
