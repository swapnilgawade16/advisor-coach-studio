using AdvisorCoachStudio.Web.Prompts;

namespace AdvisorCoachStudio.UnitTests.Prompts;

public sealed class PromptFingerprintTests
{
    [Fact]
    public void FingerprintIsAStableTwelveCharacterLowercaseSha256Prefix()
    {
        // SHA-256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
        Assert.Equal("ba7816bf8f01", PromptFingerprint.Of("abc"));
        Assert.NotEqual(PromptFingerprint.Of("abc"), PromptFingerprint.Of("abd"));
    }

    [Theory]
    [InlineData("# Role\nYou are Alex.\n", "# Role\r\nYou are Alex.", true)]
    [InlineData("You  are\tAlex.", " You are Alex. ", true)]
    [InlineData("You are Alex.", "You are Sam.", false)]
    [InlineData("You are Alex. Stay in role.", "You are Alex.", false)]
    public void ComparisonIgnoresOnlyWhitespace(string first, string second, bool expected) =>
        Assert.Equal(expected, PromptFingerprint.SameIgnoringWhitespace(first, second));
}
