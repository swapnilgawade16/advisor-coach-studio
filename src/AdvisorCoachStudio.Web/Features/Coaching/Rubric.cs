namespace AdvisorCoachStudio.Web.Features.Coaching;

public sealed record RubricCategory(string Id, string DisplayName, string Assessment);

/// <summary>Stable rubric (spec section 11.1). Category IDs must never change within a rubric version.</summary>
public static class Rubric
{
    public const string Version = "rubric/1.0.0";
    public const string SchemaVersion = "coaching-report/1.0.0";

    public static readonly IReadOnlyList<RubricCategory> Categories =
    [
        new("rapport-empathy", "Rapport and empathy", "Acknowledges emotion and creates trust"),
        new("listening-responsiveness", "Listening and responsiveness", "Responds to what the client actually said"),
        new("discovery-questioning", "Discovery and questioning", "Asks relevant, open questions and explores underlying needs"),
        new("clarity-language", "Clarity and plain language", "Uses understandable language and manageable explanations"),
        new("handling-concerns", "Handling concerns", "Addresses concerns respectfully and directly"),
        new("structure-next-steps", "Structure and next steps", "Organizes the conversation and agrees on a useful next step"),
    ];

    public static readonly IReadOnlyDictionary<int, string> ScoreAnchors = new Dictionary<int, string>
    {
        [1] = "Clear observed difficulty with the skill.",
        [2] = "Some attempt, but substantial opportunities for improvement.",
        [3] = "Competent evidence with identifiable gaps.",
        [4] = "Strong, consistent use of the skill.",
        [5] = "Particularly effective use, supported by specific evidence.",
    };

    public static bool IsCategory(string? id) => Categories.Any(c => c.Id == id);

    public static string DisplayName(string id) => Categories.FirstOrDefault(c => c.Id == id)?.DisplayName ?? id;
}
