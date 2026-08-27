using System.Text.RegularExpressions;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// The rules every page this repository publishes has to satisfy, expressed as a function over
/// document text rather than as one assertion per page.
/// </summary>
/// <remarks>
/// The README is this repository's entire consumer-facing surface, and the constraints on it are
/// editorial rather than syntactic — the kind a compiler cannot see and a reader stops noticing
/// after the third pass. Encoding them means a later edit that quietly breaks one fails the build
/// instead of shipping. <see cref="ShippedTextTests"/> runs this over the real pages and over
/// fixtures that break each rule on purpose, so the guard is proven to bite.
/// </remarks>
internal static partial class ShippedText
{
    /// <summary>The one announced product. "Rendlio" alone is the umbrella, never a product.</summary>
    private const string AnnouncedProduct = "Sheets";

    /// <summary>
    /// The engine is source-available under its own terms. That is a licensing fact, not a
    /// preference about wording, so the phrase below must not appear on a page a reader could
    /// take as a statement about it.
    /// </summary>
    [GeneratedRegex(@"\bopen[ \-]?source\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenSource();

    /// <summary>
    /// A product name: "Rendlio" followed by a capitalised word.
    /// </summary>
    /// <remarks>
    /// Deliberately matches the SHAPE of a product name and checks what it captured against what is
    /// announced, rather than listing names that may not be mentioned. A ban list would have to
    /// spell those names out in a file that ships publicly, which is the exact exposure it would
    /// exist to prevent — so the rule is written as an allow-list of one.
    /// <para>
    /// Markdown emphasis is skipped on either side of the gap, because a name is a name whether or
    /// not the writer bolded half of it, and the rule has to read what a reader reads rather than
    /// what the source spells. The gap itself is horizontal whitespace only, never <c>\s</c>: a
    /// paragraph break survives unwrapping on purpose, and matching across one would bind a
    /// paragraph ending in "Rendlio" to the next paragraph's first capitalised word.
    /// </para>
    /// <para>
    /// The left edge is a lookbehind rather than <c>\b</c> for the same reason, and it is not a
    /// nicety: <c>_</c> is a word character, so <c>\b</c> does NOT hold between the underscore and
    /// the R in <c>_Rendlio_</c> — one of the two spellings of emphasis would have walked straight
    /// past a boundary the other is caught by. Excluding letters and digits keeps a word ending in
    /// "Rendlio" from being read as the name while letting every emphasis character through.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"(?<![\p{L}\p{Nd}])Rendlio[*_]*[ \t]+[*_]*([A-Z][A-Za-z]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ProductName();

    /// <summary>
    /// Language that would describe how rendering fidelity is assured.
    /// </summary>
    /// <remarks>
    /// How output is checked is not something these pages describe; the most that is ever said is
    /// that there is a fidelity QA pipeline. The word below is the term of art for the thing on the
    /// other side of such a check, and the verbs are the ones that only appear when prose is about
    /// to describe one.
    /// <para>
    /// Written as a shape and NOT as a list of what might sit on the other side, which is the
    /// difference between this pattern and the internal-reference one it sits beside. Naming those
    /// here would publish, in a file that ships, the very thing the rule exists to keep off the
    /// pages — so the rule is deliberately blind to them and matches the sentence instead. Verbs
    /// that carry the meaning in ordinary technical prose ("checked against", "validated against")
    /// are left out for the same reason a broad ban list is: the cost of a false positive here is
    /// paid by someone editing a rule page, every time.
    /// </para>
    /// <para>
    /// The word is banned outright rather than in context, so a page that wanted it for an
    /// unrelated reason — naming a database vendor in a list of banned clients, say — fails here
    /// too. That is this rule biting rather than a defect; the fix is to widen it knowingly, the
    /// way the suppression walk in <see cref="ShippedTextTests"/> is widened.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"\boracles?\b|\b(?:scored|graded|diffed|benchmarked)[ \t]+against\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FidelityComparison();

    /// <summary>
    /// The association named without the qualifier that says it does not exist yet.
    /// </summary>
    /// <remarks>
    /// Stating who is behind Rendlio is optional; stating it as though the association were already
    /// registered is not a wording preference but a claim that is not true today. The qualifier is
    /// the whole difference, and dropping it is a one-word edit that reads perfectly well — which
    /// is exactly why nothing but a guard would catch it. Matched as a negative lookahead rather
    /// than by requiring the full phrase somewhere on the page, so a page that names the
    /// association twice cannot satisfy the rule once and breach it in the other place.
    /// </remarks>
    [GeneratedRegex(@"\bSwiss association\b(?![ \t]+in formation\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnformedAssociation();

    /// <summary>A single newline inside a paragraph, as opposed to a paragraph break.</summary>
    /// <remarks>
    /// The pages here are hard-wrapped at column ~90, so "Rendlio" and the word naming the product
    /// routinely land on different lines. Soft wraps are folded to spaces before matching, or a
    /// wrapped name would slip past unchecked. Blank lines are left alone: folding those too would
    /// let a paragraph ending in "Rendlio" bind to the next paragraph's first capitalised word and
    /// report a product nobody wrote.
    /// </remarks>
    [GeneratedRegex(@"(?<![\r\n])\r?\n(?![\r\n])")]
    private static partial Regex SoftWrap();

    /// <summary>
    /// <paramref name="text"/> with its soft wraps folded to spaces, leaving paragraph breaks
    /// intact — the form every rule here is matched against.
    /// </summary>
    /// <remarks>
    /// Exposed rather than kept inside <see cref="Inspect"/> because the cases that pin a phrase
    /// POSITIVELY — the identity phrasing, say — have to fold the same way before looking for it,
    /// or the pin turns red the first time someone rewraps the paragraph carrying it.
    /// </remarks>
    internal static string Unwrap(string text) => SoftWrap().Replace(text, " ");

    /// <summary>
    /// Returns one message per violation in <paramref name="text"/>, or an empty list when the
    /// page complies. <paramref name="documentPath"/> is used only to name the page in a message.
    /// </summary>
    internal static IReadOnlyList<string> Inspect(string documentPath, string text)
    {
        string unwrapped = Unwrap(text);
        var violations = new List<string>();

        foreach (Match match in OpenSource().Matches(unwrapped))
        {
            violations.Add(
                $"{documentPath}: says '{match.Value}'; the engine is source-available under its own terms.");
        }

        foreach (Match match in ProductName().Matches(unwrapped))
        {
            string product = match.Groups[1].Value;
            if (!string.Equals(product, AnnouncedProduct, StringComparison.Ordinal))
            {
                violations.Add($"{documentPath}: names 'Rendlio {product}', which is not an announced product.");
            }
        }

        foreach (Match match in FidelityComparison().Matches(unwrapped))
        {
            violations.Add(
                $"{documentPath}: says '{match.Value}'; how rendering fidelity is assured is not something these pages describe.");
        }

        foreach (Match match in UnformedAssociation().Matches(unwrapped))
        {
            violations.Add(
                $"{documentPath}: says '{match.Value}', which presents as existing something that is in formation.");
        }

        // Same definition the shipped diagnostic text is held to — see AnalyzerConventions.
        foreach (Match match in AnalyzerConventions.InternalReference().Matches(unwrapped))
        {
            violations.Add(
                $"{documentPath}: cites '{match.Value}', which means nothing to a reader outside Rendlio.");
        }

        return violations;
    }
}
