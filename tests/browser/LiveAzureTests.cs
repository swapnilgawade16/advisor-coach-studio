using System.Buffers.Binary;
using AdvisorCoachStudio.BrowserTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace AdvisorCoachStudio.BrowserTests;

/// <summary>
/// Opt-in end-to-end check of a running instance configured for real Azure deployments, with no fakes or simulators
/// on the server side: the browser's microphone plays recorded advisor clips (with pauses for the AI client to reply),
/// the session is ended, and the real coaching report must render with scores and evidence links. Skipped unless
/// ACS_LIVE_BASE_URL, ACS_LIVE_ACCESS_CODE, and ACS_LIVE_ADVISOR_WAVS (semicolon-separated 16-bit mono PCM WAV files
/// with one sample rate) are set. It uses Azure quota like a normal practice session.
/// </summary>
public sealed class LiveAzureTests : IAsyncLifetime
{
    private static readonly TimeSpan LeadSilence = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ReplyPause = TimeSpan.FromSeconds(16);
    private static readonly TimeSpan TailSilence = TimeSpan.FromSeconds(20);
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async ValueTask InitializeAsync() => _playwright = await Playwright.CreateAsync();

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
    }

    [Fact]
    public async Task RealConversationThroughTheBrowserProducesAGroundedCoachingReport()
    {
        var baseUrl = Environment.GetEnvironmentVariable("ACS_LIVE_BASE_URL");
        var accessCode = Environment.GetEnvironmentVariable("ACS_LIVE_ACCESS_CODE");
        var clips = (Environment.GetEnvironmentVariable("ACS_LIVE_ADVISOR_WAVS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(accessCode) || clips.Length == 0)
        {
            Assert.Skip("Set ACS_LIVE_BASE_URL, ACS_LIVE_ACCESS_CODE, and ACS_LIVE_ADVISOR_WAVS to run the live Azure check.");
        }

        var output = Environment.GetEnvironmentVariable("ACS_LIVE_OUTPUT_DIR") is { Length: > 0 } directory ? directory : Path.Combine(AppContext.BaseDirectory, "live-azure");
        Directory.CreateDirectory(output);
        var microphone = Path.Combine(output, "advisor-microphone.wav");
        var speechEnds = await ComposeMicrophoneAsync(clips, microphone, TestContext.Current.CancellationToken);

        _browser = await _playwright!.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = BrowserFixture.Channel,
            Headless = Environment.GetEnvironmentVariable("ACS_BROWSER_HEADED") != "1",
            Args =
            [
                "--use-fake-device-for-media-stream",
                "--use-fake-ui-for-media-stream",
                $"--use-file-for-fake-audio-capture={microphone}%noloop",
                "--autoplay-policy=no-user-gesture-required",
                "--mute-audio",
            ],
        });
        var context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseUrl.TrimEnd('/'),
            ViewportSize = BrowserTest.Desktop,
            Permissions = ["microphone"],
        });
        context.SetDefaultTimeout(30_000);
        var page = await context.NewPageAsync();

        await page.GotoAsync("/access");
        await page.FillAsync("#code", accessCode);
        await page.ClickAsync("form button[type=submit]");
        await Expect(page.Locator("#scenarios-heading")).ToBeVisibleAsync();

        // The fake microphone file starts playing when the practice page opens the microphone, just before it connects.
        var sessionId = await PracticeFlow.StartSessionAsync(page);
        await Expect(page.Locator("#live")).Not.ToContainTextAsync("Simulated");
        await Task.Delay(speechEnds, TestContext.Current.CancellationToken);
        await PracticeFlow.WaitForAdvisorTurnsAsync(page, clips.Length);
        await PracticeFlow.WaitForListeningAsync(page);
        await PracticeFlow.EndAsync(page);
        await PracticeFlow.OpenReportAsync(page, sessionId);

        await PracticeFlow.WaitForReportStatusAsync(page, "ready", timeoutMs: 240_000);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(output, "live-report.png"), FullPage = true });
        await Expect(page.Locator("#report-root .alert-warning", new() { HasText = "Simulated" })).ToHaveCountAsync(0);
        await Expect(page.Locator(".skill .score-pill")).ToHaveCountAsync(6);
        Assert.True(await page.Locator("a.evidence-link").CountAsync() > 0, "The report cites transcript evidence.");
        Assert.True(await page.Locator("#report-transcript li.turn-advisor").CountAsync() >= clips.Length, "The saved transcript contains the advisor's turns.");
    }

    /// <summary>Writes lead silence, each clip followed by a pause for the client's reply, and tail silence; returns when the last clip ends.</summary>
    private static async Task<TimeSpan> ComposeMicrophoneAsync(IReadOnlyList<string> clips, string path, CancellationToken cancellationToken)
    {
        var sampleRate = 0;
        var pcm = new List<byte>();
        var speechEnds = TimeSpan.Zero;
        foreach (var clip in clips)
        {
            var (rate, samples) = ReadPcm16Mono(await File.ReadAllBytesAsync(clip, cancellationToken), clip);
            if (sampleRate == 0)
            {
                sampleRate = rate;
                pcm.AddRange(new byte[Bytes(LeadSilence, sampleRate)]);
            }
            else if (rate != sampleRate)
            {
                throw new InvalidOperationException($"{clip} is {rate} Hz but the first clip is {sampleRate} Hz.");
            }

            pcm.AddRange(samples);
            speechEnds = TimeSpan.FromSeconds(pcm.Count / 2.0 / sampleRate);
            pcm.AddRange(new byte[Bytes(ReplyPause, sampleRate)]);
        }

        pcm.AddRange(new byte[Bytes(TailSilence, sampleRate)]);
        var wav = new byte[44 + pcm.Count];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + pcm.Count);
        "WAVEfmt "u8.CopyTo(span[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], pcm.Count);
        pcm.CopyTo(wav, 44);
        await File.WriteAllBytesAsync(path, wav, cancellationToken);
        return speechEnds;
    }

    private static int Bytes(TimeSpan duration, int sampleRate) => (int)(duration.TotalSeconds * sampleRate) * 2;

    private static (int SampleRate, byte[] Samples) ReadPcm16Mono(byte[] wav, string name)
    {
        var span = wav.AsSpan();
        if (wav.Length < 12 || !span[..4].SequenceEqual("RIFF"u8) || !span[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidOperationException($"{name} is not a WAV file.");
        }

        int? sampleRate = null;
        for (var offset = 12; offset + 8 <= wav.Length;)
        {
            var id = span.Slice(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(span[(offset + 4)..]);
            var body = span.Slice(offset + 8, Math.Min(size, wav.Length - offset - 8));
            if (id.SequenceEqual("fmt "u8))
            {
                if (BinaryPrimitives.ReadInt16LittleEndian(body) != 1 || BinaryPrimitives.ReadInt16LittleEndian(body[2..]) != 1 || BinaryPrimitives.ReadInt16LittleEndian(body[14..]) != 16)
                {
                    throw new InvalidOperationException($"{name} must be 16-bit mono PCM.");
                }

                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
            }
            else if (id.SequenceEqual("data"u8) && sampleRate is { } rate)
            {
                return (rate, body.ToArray());
            }

            offset += 8 + size + (size % 2);
        }

        throw new InvalidOperationException($"{name} has no PCM data.");
    }
}
