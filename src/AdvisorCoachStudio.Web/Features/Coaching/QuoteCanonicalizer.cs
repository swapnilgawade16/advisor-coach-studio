using AdvisorCoachStudio.Web.Domain;

namespace AdvisorCoachStudio.Web.Features.Coaching;

/// <summary>
/// Maps model quotes back to the exact transcript text when they differ only in punctuation style, for example a
/// straight apostrophe in the quote and a curly one in the transcript. Matching is otherwise exact and case-sensitive,
/// and a repaired quote is always copied from the turn itself, so the strict exact-substring validation in
/// <see cref="ReportValidator"/> still applies to every stored quote. Quotes with any other difference are left alone
/// and rejected by validation.
/// </summary>
public static class QuoteCanonicalizer
{
    public static CoachingReportContent Canonicalize(CoachingReportContent content, IReadOnlyDictionary<string, TranscriptTurn> turnsByLabel) => content with
    {
        SkillScores = [.. content.SkillScores.Select(score => score with { Evidence = Canonicalize(score.Evidence, turnsByLabel) })],
        Strengths = [.. content.Strengths.Select(strength => strength with { Evidence = Canonicalize(strength.Evidence, turnsByLabel) })],
        Improvements = [.. content.Improvements.Select(improvement => improvement with { Evidence = Canonicalize(improvement.Evidence, turnsByLabel) })],
        AlternativePhrases = [.. content.AlternativePhrases.Select(phrase => phrase with { OriginalEvidence = Canonicalize(phrase.OriginalEvidence, turnsByLabel) })],
    };

    public static ReportEvidence Canonicalize(ReportEvidence evidence, IReadOnlyDictionary<string, TranscriptTurn> turnsByLabel)
    {
        if (string.IsNullOrEmpty(evidence.Quote) || !turnsByLabel.TryGetValue(evidence.TurnId, out var turn) ||
            turn.Text.Contains(evidence.Quote, StringComparison.Ordinal))
        {
            return evidence;
        }

        return FindInTranscript(turn.Text, evidence.Quote) is { } exact ? evidence with { Quote = exact } : evidence;
    }

    /// <summary>The substring of <paramref name="text"/> that equals <paramref name="quote"/> after folding punctuation, or null.</summary>
    public static string? FindInTranscript(string text, string quote)
    {
        // Folding maps each character to exactly one character, so an index in the folded text is the same index in the original.
        var index = Fold(text).IndexOf(Fold(quote), StringComparison.Ordinal);
        return index < 0 ? null : text.Substring(index, quote.Length);
    }

    private static List<ReportEvidence> Canonicalize(IReadOnlyList<ReportEvidence> evidence, IReadOnlyDictionary<string, TranscriptTurn> turnsByLabel) =>
        [.. evidence.Select(item => Canonicalize(item, turnsByLabel))];

    private static string Fold(string value) => string.Create(value.Length, value, static (span, source) =>
    {
        for (var i = 0; i < source.Length; i++)
        {
            span[i] = Fold(source[i]);
        }
    });

    private static char Fold(char c) => c switch
    {
        // U+0019 is not punctuation, but models have been observed emitting it in place of an apostrophe when copying text.
        '\u2018' or '\u2019' or '\u201B' or '\u02BC' or '\u2032' or '\u0060' or '\u00B4' or '\u0019' => '\'',
        '\u201C' or '\u201D' or '\u201F' or '\u2033' => '"',
        '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212' => '-',
        '\u00A0' or '\u2007' or '\u2009' or '\u202F' => ' ',
        _ => c,
    };
}
