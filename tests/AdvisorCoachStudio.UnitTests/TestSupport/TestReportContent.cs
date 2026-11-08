using System.Text.Json;
using AdvisorCoachStudio.Web.Features.Coaching;

namespace AdvisorCoachStudio.UnitTests.TestSupport;

/// <summary>
/// Report content grounded in <see cref="TestTranscripts.Conversation"/>. Every quote is an exact substring of a
/// quotable turn. Scores are 4, 3, 3, 3, null, 4: five scored categories with a mean of exactly 3.4.
/// </summary>
internal static class TestReportContent
{
    public const decimal ExpectedOverallScore = 3.4m;
    public const int ExpectedScoredCategories = 5;

    public static ReportEvidence Evidence(string turnId, string quote) => new()
    {
        TurnId = turnId,
        Quote = quote,
        Explanation = "Synthetic explanation of why this excerpt matters.",
    };

    public static SkillScoreContent Score(string categoryId, int? score, params ReportEvidence[] evidence) => new()
    {
        CategoryId = categoryId,
        Score = score,
        Explanation = "Synthetic explanation of the score.",
        Evidence = evidence,
    };

    public static CoachingReportContent Valid() => new()
    {
        Summary = "You acknowledged the worry early and asked about retirement timing before agreeing a review.",
        KeyStrength = "You named the client's stress before moving to questions.",
        PriorityImprovement = "Check the client's understanding before proposing a next step.",
        SkillScores =
        [
            Score("rapport-empathy", 4, Evidence("T2", "a stressful morning for you")),
            Score("listening-responsiveness", 3, Evidence("T2", "I can hear that")),
            Score("discovery-questioning", 3, Evidence("T4", "when you are hoping to retire")),
            Score("clarity-language", 3, Evidence("T4", "Before we decide anything")),
            Score("handling-concerns", null),
            Score("structure-next-steps", 4, Evidence("T6", "book a review next week")),
        ],
        Strengths =
        [
            new StrengthContent
            {
                Title = "Acknowledged the emotion",
                Observation = "You named the stress before asking questions.",
                Interpretation = "This may have helped the client feel heard.",
                CategoryId = "rapport-empathy",
                Evidence = [Evidence("T2", "stressful morning")],
            },
        ],
        Improvements =
        [
            new ImprovementContent
            {
                Title = "Explore the partner's view",
                Observation = "The client mentioned their partner's suggestion to sell.",
                Interpretation = null,
                CategoryId = "discovery-questioning",
                Evidence = [Evidence("T3", "My partner thinks we should sell everything.")],
                SuggestedAdjustment = "Ask what is driving the partner's concern.",
            },
        ],
        AlternativePhrases =
        [
            new AlternativePhraseContent
            {
                OriginalEvidence = Evidence("T4", "can you tell me when you are hoping to retire?"),
                SuggestedPhrase = "What would you like your retirement to look like, and when?",
                CommunicationIntention = "Invite the client to describe goals in their own words.",
                Explanation = "An open question can surface goals and timing together.",
            },
        ],
        MissedDiscoveryQuestions =
        [
            new MissedQuestionContent
            {
                Question = "Do you need any of this money in the near term?",
                Purpose = "Separate immediate needs from retirement worries.",
                Relevance = "The client is considering selling everything.",
                RelatedTurnIds = ["T3"],
            },
        ],
        PracticePlan = new PracticePlanContent
        {
            FocusAreas = ["Checking understanding"],
            Exercise = "Practise summarizing the client's concern before suggesting a next step.",
            RecommendedScenarioId = TestScenarios.MarketDecline,
        },
        Warnings = [],
    };

    /// <summary>Serializes content exactly as a model would return it (camelCase web JSON).</summary>
    public static string ToJson(CoachingReportContent content) => JsonSerializer.Serialize(content, CoachingReportGenerator.ContentJsonOptions);
}
