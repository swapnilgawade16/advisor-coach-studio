using Microsoft.Playwright;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>Which microphone behaviour a browser process provides (see <see cref="FakeAudio"/> for what each sounds like).</summary>
public enum MicrophoneMode
{
    /// <summary>Chromium's fake capture device (a 20 ms 400 Hz beep every 500 ms) with the permission prompt auto-accepted.</summary>
    FakeBeeps,

    /// <summary>A fake device exists but permission prompts are denied, so getUserMedia rejects with NotAllowedError.</summary>
    PermissionDenied,

    /// <summary>Voice-like bursts, so the simulator detects complete advisor turns.</summary>
    SpeechBursts,

    /// <summary>Digital silence, so the client's opening plays to the end and no advisor speech is ever detected.</summary>
    Silence,
}

/// <summary>
/// Owns Playwright and one headless browser process per <see cref="MicrophoneMode"/> (launched on first use).
/// ACS_BROWSER_CHANNEL selects the browser: unset or "msedge" uses installed Microsoft Edge, "chrome" uses installed
/// Google Chrome, and "chromium" uses Playwright's bundled Chromium (CI). Set ACS_BROWSER_HEADED=1 to watch locally.
/// </summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private readonly Dictionary<MicrophoneMode, Task<IBrowser>> _browsers = [];
    private readonly Lock _gate = new();
    private IPlaywright? _playwright;

    public static string Channel => (Environment.GetEnvironmentVariable("ACS_BROWSER_CHANNEL") ?? "").Trim().ToLowerInvariant() switch
    {
        "" or "msedge" => "msedge",
        "chrome" => "chrome",

        // Bundled Chromium in "new headless" mode, matching how installed Edge/Chrome run headless.
        "chromium" => "chromium",
        var other => throw new InvalidOperationException($"Unsupported ACS_BROWSER_CHANNEL '{other}'. Use msedge, chrome, or chromium."),
    };

    public async ValueTask InitializeAsync()
    {
        Assertions.SetDefaultExpectTimeout(15_000);
        _playwright = await Playwright.CreateAsync();
    }

    public Task<IBrowser> GetBrowserAsync(MicrophoneMode mode)
    {
        lock (_gate)
        {
            if (!_browsers.TryGetValue(mode, out var browser) || browser.IsFaulted)
            {
                browser = LaunchAsync(mode);
                _browsers[mode] = browser;
            }

            return browser;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var browser in _browsers.Values)
        {
            try
            {
                await (await browser).CloseAsync();
            }
            catch (Exception)
            {
                // A browser that failed to launch has nothing to close.
            }
        }

        _playwright?.Dispose();
    }

    private Task<IBrowser> LaunchAsync(MicrophoneMode mode)
    {
        var playwright = _playwright ?? throw new InvalidOperationException("Playwright is not initialized.");
        var args = new List<string>
        {
            "--use-fake-device-for-media-stream",
            "--autoplay-policy=no-user-gesture-required",

            // The synthetic client voice still plays through the audio graph; just keep it off the speakers.
            "--mute-audio",
        };

        switch (mode)
        {
            case MicrophoneMode.FakeBeeps:
                args.Add("--use-fake-ui-for-media-stream");
                break;
            case MicrophoneMode.PermissionDenied:
                args.Add("--deny-permission-prompts");
                break;
            case MicrophoneMode.SpeechBursts:
                args.Add("--use-fake-ui-for-media-stream");
                args.Add($"--use-file-for-fake-audio-capture={FakeAudio.SpeechBurstsFile()}");
                break;
            case MicrophoneMode.Silence:
                args.Add("--use-fake-ui-for-media-stream");
                args.Add($"--use-file-for-fake-audio-capture={FakeAudio.SilenceFile()}");
                break;
        }

        return playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = Channel,
            Headless = Environment.GetEnvironmentVariable("ACS_BROWSER_HEADED") != "1",
            Args = args,
        });
    }
}
