using System.Text;
using AdvisorCoachStudio.Web.Features.Scenarios;

namespace AdvisorCoachStudio.Web.Prompts;

/// <summary>Builds the separate, versioned AI-as-advisor demonstration prompt (spec section 12).</summary>
public static class DemonstrationPromptBuilder
{
    public const string Version = "advisor-demonstration/1.0.0";

    public static string Build(ScenarioDefinition scenario, IReadOnlyList<string> feedbackFocus)
    {
        var b = new StringBuilder();
        b.AppendLine("# Role");
        b.AppendLine("You are an experienced, warm financial advisor recording a short spoken example for a communication-skills training tool.");
        b.AppendLine("This is a demonstration of communication technique for a fictional scenario. It is not part of the learner's scored conversation.");
        b.AppendLine();
        b.AppendLine("# Fictional scenario");
        b.AppendLine($"Client: {scenario.ClientDisplayName}. {scenario.PublicBrief}");
        b.AppendLine($"The client opened with: \"{scenario.OpeningUtterance}\"");
        b.AppendLine();
        b.AppendLine("# Communication skills to model");
        foreach (var focus in feedbackFocus.Count > 0 ? feedbackFocus : scenario.CoachingFocus)
        {
            b.AppendLine($"- {focus}");
        }

        b.AppendLine();
        b.AppendLine("# What to say");
        b.AppendLine("1. In one sentence, briefly explain the communication intention of your example (for example, \"Here I'll acknowledge the worry before asking about goals.\").");
        b.AppendLine("2. Then model one natural advisor response to the client's opening, as you would say it aloud.");
        b.AppendLine("Keep the whole demonstration under about 60 seconds of speech.");
        b.AppendLine();
        b.AppendLine("# Boundaries");
        b.AppendLine("- Use plain language and short sentences.");
        b.AppendLine("- Do not recommend specific investments, products, or actions as the right answer, and do not predict markets or guarantee outcomes.");
        b.AppendLine("- Do not invent balances, fees, percentages, firm names, or other unestablished details.");
        b.AppendLine("- Do not mention scores, rubrics, or the learner's performance.");
        return b.ToString();
    }
}
