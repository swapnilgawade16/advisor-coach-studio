using System.Security.Cryptography;
using System.Text;
using Microsoft.Playwright;

namespace AdvisorCoachStudio.BrowserTests.Infrastructure;

/// <summary>
/// Base class for browser tests: opens isolated browser contexts (one anonymous owner each) with the instrumentation
/// script, an access cookie, and microphone permission, and on failure saves a Playwright trace and screenshot and
/// prints the server log and browser console for the test.
/// </summary>
public abstract class BrowserTest(BrowserFixture browsers, AppHost host) : IAsyncLifetime
{
    private readonly List<IBrowserContext> _contexts = [];
    private readonly List<string> _browserLog = [];
    private readonly List<string> _pageErrors = [];
    private long _serverLogPosition;

    public static ViewportSize Desktop { get; } = new() { Width = 1280, Height = 900 };

    public static ViewportSize Phone { get; } = new() { Width = 375, Height = 800 };

    /// <summary>WCAG 2.2 reflow width (320 CSS pixels).</summary>
    public static ViewportSize Reflow { get; } = new() { Width = 320, Height = 800 };

    /// <summary>Uncaught exceptions and unhandled rejections raised by page scripts so far.</summary>
    protected IReadOnlyList<string> PageErrors
    {
        get { lock (_pageErrors) { return _pageErrors.ToList(); } }
    }

    protected static string TraceDirectory =>
        Environment.GetEnvironmentVariable("ACS_BROWSER_TRACE_DIR") is { Length: > 0 } directory
            ? directory
            : Path.Combine(AppContext.BaseDirectory, "playwright-traces");

    public virtual ValueTask InitializeAsync()
    {
        _serverLogPosition = host.Logs.Position;
        return ValueTask.CompletedTask;
    }

    /// <summary>Opens a page in a new browser context. Signed-in contexts get their own anonymous owner.</summary>
    protected async Task<IPage> OpenPageAsync(MicrophoneMode microphone = MicrophoneMode.FakeBeeps, bool signedIn = true, ViewportSize? viewport = null)
    {
        var browser = await browsers.GetBrowserAsync(microphone);
        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = host.Origin,
            ViewportSize = viewport ?? Desktop,
            Permissions = microphone == MicrophoneMode.PermissionDenied ? [] : ["microphone"],
        });
        _contexts.Add(context);
        context.SetDefaultTimeout(20_000);
        await context.Tracing.StartAsync(new TracingStartOptions { Screenshots = true, Snapshots = true });
        await context.AddInitScriptAsync(Probe.Script);
        if (signedIn)
        {
            await context.AddCookiesAsync([host.CreateAccessGrant().Cookie]);
        }

        var page = await context.NewPageAsync();
        var label = $"page{_contexts.Count}";
        page.Console += (_, message) =>
        {
            if (message.Type is "error" or "warning")
            {
                Log($"{label} console.{message.Type}: {message.Text}");
            }
        };
        page.PageError += (_, error) =>
        {
            lock (_pageErrors)
            {
                _pageErrors.Add(error);
            }

            Log($"{label} pageerror: {error}");
        };
        page.FrameNavigated += (_, frame) =>
        {
            if (frame.ParentFrame is null)
            {
                Log($"{label} navigated: {frame.Url}");
            }
        };
        return page;
    }

    protected void Log(string message)
    {
        lock (_browserLog)
        {
            _browserLog.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        var failed = TestContext.Current.TestState?.Result == TestResult.Failed;
        var display = TestContext.Current.Test?.TestDisplayName ?? GetType().Name;
        var name = $"{Sanitize(display.Replace("AdvisorCoachStudio.BrowserTests.", "", StringComparison.Ordinal))}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(display)))[..8]}";
        for (var i = 0; i < _contexts.Count; i++)
        {
            var context = _contexts[i];
            try
            {
                if (failed)
                {
                    Directory.CreateDirectory(TraceDirectory);
                    for (var p = 0; p < context.Pages.Count; p++)
                    {
                        await context.Pages[p].ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(TraceDirectory, $"{name}-{i + 1}-{p + 1}.png"), FullPage = true });
                    }

                    await context.Tracing.StopAsync(new TracingStopOptions { Path = Path.Combine(TraceDirectory, $"{name}-{i + 1}.zip") });
                }
                else
                {
                    await context.Tracing.StopAsync();
                }

                await context.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // Diagnostics and cleanup are best effort (for example after a browser crash).
            }
        }

        if (failed && TestContext.Current.TestOutputHelper is { } output)
        {
            var text = new StringBuilder();
            text.AppendLine($"Playwright traces: {TraceDirectory} (open with 'pwsh playwright.ps1 show-trace <zip>')");
            text.AppendLine("--- browser ---");
            lock (_browserLog)
            {
                _browserLog.ForEach(line => text.AppendLine(line));
            }

            text.AppendLine("--- server ---");
            foreach (var line in host.Logs.Since(_serverLogPosition).TakeLast(400))
            {
                text.AppendLine(line);
            }

            output.WriteLine(text.ToString());
        }

        await AfterContextsClosedAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Runs after every browser context of the test has been closed.</summary>
    protected virtual ValueTask AfterContextsClosedAsync() => ValueTask.CompletedTask;

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : '_');
        }

        return builder.Length > 100 ? builder.ToString(0, 100) : builder.ToString();
    }
}
