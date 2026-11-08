using AdvisorCoachStudio.Web.Domain;
using AdvisorCoachStudio.Web.Features.Scenarios;

namespace AdvisorCoachStudio.LiveCheck;

internal sealed record LiveCheckArguments(
    string ScenarioId, Difficulty Difficulty, IReadOnlyList<string> AdvisorWavs, bool Interrupt, bool ShowPrompt, bool Report,
    string? SaveTranscriptPath, string? TranscriptPath, string? DumpResponsesDirectory)
{
    public const string Usage = """
        Usage: dotnet run --project tools/AdvisorCoachStudio.LiveCheck -- [options]
          --scenario <id>            market-decline (default), fees-and-trust, first-time-investor
          --difficulty <level>       beginner, intermediate (default), advanced
          --advisor-wav <path>       24 kHz 16-bit mono WAV of advisor speech; repeat for several turns
          --interrupt                speak the last advisor clip while the client is still replying
          --show-prompt              print the full system instructions sent to Azure
          --report                   generate a coaching report from the live transcript with the AzureReports deployment
          --save-transcript <path>   save the live transcript as JSON (contains the conversation text)
          --transcript <path>        skip the voice conversation and generate a report from a saved transcript
          --dump-responses <dir>     write the raw report responses from Azure to <dir> (contains report text)
        """;

    public static LiveCheckArguments Parse(string[] args)
    {
        var scenario = "market-decline";
        var difficulty = Difficulty.Intermediate;
        var wavs = new List<string>();
        var interrupt = false;
        var showPrompt = false;
        var report = false;
        string? saveTranscript = null;
        string? transcript = null;
        string? dumpResponses = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--scenario":
                    scenario = Next(args, ref i);
                    break;
                case "--difficulty":
                    if (!DifficultyInfo.TryParse(Next(args, ref i), out difficulty))
                    {
                        throw new ArgumentException("Difficulty must be beginner, intermediate, or advanced.");
                    }

                    break;
                case "--advisor-wav":
                    wavs.Add(Path.GetFullPath(Next(args, ref i)));
                    break;
                case "--interrupt":
                    interrupt = true;
                    break;
                case "--show-prompt":
                    showPrompt = true;
                    break;
                case "--report":
                    report = true;
                    break;
                case "--save-transcript":
                    saveTranscript = Path.GetFullPath(Next(args, ref i));
                    break;
                case "--transcript":
                    transcript = Path.GetFullPath(Next(args, ref i));
                    break;
                case "--dump-responses":
                    dumpResponses = Path.GetFullPath(Next(args, ref i));
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (transcript is not null)
        {
            if (wavs.Count > 0 || interrupt || saveTranscript is not null)
            {
                throw new ArgumentException("--transcript replays a saved conversation; it cannot be combined with --advisor-wav, --interrupt, or --save-transcript.");
            }

            return new LiveCheckArguments(scenario, difficulty, wavs, Interrupt: false, showPrompt, Report: true, SaveTranscriptPath: null, transcript, dumpResponses);
        }

        if (interrupt && wavs.Count < 2)
        {
            throw new ArgumentException("--interrupt needs at least two --advisor-wav clips.");
        }

        if (report && wavs.Count == 0)
        {
            throw new ArgumentException("--report needs at least one --advisor-wav clip, so the transcript has advisor speech to assess.");
        }

        if (dumpResponses is not null && !report)
        {
            throw new ArgumentException("--dump-responses applies to report generation; add --report.");
        }

        return new LiveCheckArguments(scenario, difficulty, wavs, interrupt, showPrompt, report, saveTranscript, TranscriptPath: null, dumpResponses);
    }

    private static string Next(string[] args, ref int index) =>
        index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{args[index]} needs a value.");
}

/// <summary>Collects check outcomes and prints them as they happen.</summary>
internal sealed class CheckLog
{
    private readonly List<(string Outcome, string Name, string Detail)> _entries = [];

    public bool Failed => _entries.Exists(entry => entry.Outcome == "FAIL");

    public void Pass(string name, string detail) => Add("PASS", name, detail);

    public void Warn(string name, string detail) => Add("WARN", name, detail);

    public void Fail(string name, string detail) => Add("FAIL", name, detail);

    public void Info(string name, string detail) => Add("INFO", name, detail);

    public void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine($"Summary: {Count("PASS")} passed, {Count("WARN")} warning(s), {Count("FAIL")} failed.");
        foreach (var (outcome, name, detail) in _entries.Where(entry => entry.Outcome is "FAIL" or "WARN"))
        {
            Console.WriteLine($"  [{outcome}] {name}: {detail}");
        }
    }

    private int Count(string outcome) => _entries.Count(entry => entry.Outcome == outcome);

    private void Add(string outcome, string name, string detail)
    {
        _entries.Add((outcome, name, detail));
        Console.WriteLine($"[{outcome}] {name}: {detail}");
    }
}
