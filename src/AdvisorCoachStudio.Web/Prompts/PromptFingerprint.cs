using System.Security.Cryptography;
using System.Text;

namespace AdvisorCoachStudio.Web.Prompts;

/// <summary>Short, log-friendly identifiers for prompt text, so prompts can be correlated without logging them.</summary>
public static class PromptFingerprint
{
    private static readonly char[] Whitespace = [' ', '\t', '\r', '\n'];

    /// <summary>The first 12 lowercase hex characters of the SHA-256 of the UTF-8 text.</summary>
    public static string Of(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];

    /// <summary>True when the texts are identical apart from whitespace (line endings, indentation, runs of spaces).</summary>
    public static bool SameIgnoringWhitespace(string first, string second) =>
        string.Equals(Collapse(first), Collapse(second), StringComparison.Ordinal);

    private static string Collapse(string text) => string.Join(' ', text.Split(Whitespace, StringSplitOptions.RemoveEmptyEntries));
}
