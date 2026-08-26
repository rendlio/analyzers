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
    /// </remarks>
    [GeneratedRegex(@"\bRendlio ([A-Z][A-Za-z]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ProductName();

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
    /// Returns one message per violation in <paramref name="text"/>, or an empty list when the
    /// page complies. <paramref name="documentPath"/> is used only to name the page in a message.
    /// </summary>
    internal static IReadOnlyList<string> Inspect(string documentPath, string text)
    {
        string unwrapped = SoftWrap().Replace(text, " ");
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

        // Same definition the shipped diagnostic text is held to — see AnalyzerConventions.
        foreach (Match match in AnalyzerConventions.InternalReference().Matches(unwrapped))
        {
            violations.Add(
                $"{documentPath}: cites '{match.Value}', which means nothing to a reader outside Rendlio.");
        }

        return violations;
    }
}
