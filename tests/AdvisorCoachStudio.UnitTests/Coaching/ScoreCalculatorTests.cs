using AdvisorCoachStudio.Web.Features.Coaching;

namespace AdvisorCoachStudio.UnitTests.Coaching;

/// <summary>Server-side overall score (spec section 11.1): equal-weight mean, rounded once, never from the model.</summary>
public sealed class ScoreCalculatorTests
{
    public static TheoryData<int?[], decimal> RoundedMeans => new()
    {
        { [3, 4, 3, 3], 3.3m },
        { [4, 3, 3, 3], 3.3m },
        { [3, 3, 4, 4], 3.5m },
        { [1, 2, 2, 2], 1.8m },
        { [1, 2, 2], 1.7m },
        { [2, 3, 3, 3, 3, 3], 2.8m },
        { [5, 5, 5, 5, 5, 5], 5.0m },
        { [3, 4, 3, 3, null, null], 3.3m },
    };

    public static TheoryData<int?[], int, int> InsufficientEvidence => new()
    {
        { [3, 4, null, null, null, null], 5, 2 },
        { [3, 4, 5, null, null, null], 2, 3 },
        { [null, null, null, null, null, null], 6, 0 },
        { [], 10, 0 },
    };

    [Theory]
    [MemberData(nameof(RoundedMeans))]
    public void OverallScoreIsTheMeanOfScoredCategoriesRoundedOnceAwayFromZero(int?[] scores, decimal expected)
    {
        var (overall, scoredCount) = ScoreCalculator.Compute(scores, usableAdvisorTurns: 3);

        Assert.Equal(expected, overall);
        Assert.Equal(scores.Count(score => score is not null), scoredCount);
    }

    [Theory]
    [MemberData(nameof(InsufficientEvidence))]
    public void OverallScoreIsNullWithoutEnoughEvidence(int?[] scores, int usableAdvisorTurns, int expectedScoredCount)
    {
        var (overall, scoredCount) = ScoreCalculator.Compute(scores, usableAdvisorTurns);

        Assert.Null(overall);
        Assert.Equal(expectedScoredCount, scoredCount);
    }

    [Fact]
    public void MinimumThresholdsAreInclusive()
    {
        var (overall, scoredCount) = ScoreCalculator.Compute([3, null, 4, null, 5, null], usableAdvisorTurns: 3);

        Assert.Equal(4.0m, overall);
        Assert.Equal(3, scoredCount);
    }

    [Fact]
    public void MissingScoresNeverCountAsZero()
    {
        var (overall, _) = ScoreCalculator.Compute([5, 5, 5, null, null, null], usableAdvisorTurns: 4);

        Assert.Equal(5.0m, overall);
    }
}
