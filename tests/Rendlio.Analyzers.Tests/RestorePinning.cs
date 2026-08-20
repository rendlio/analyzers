using System.Xml.Linq;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Reads a NuGet.config and reports the ways it leaves package resolution open to whatever the
/// machine building this repository happens to be configured with.
/// </summary>
/// <remarks>
/// A package that ships to strangers should resolve from exactly one public source, and the same
/// one on every machine that builds it. Two separate sections decide that, and both are inherited
/// from the machine-level config unless this repository clears them.
/// <para>
/// <c>packageSources</c> is the one everybody clears: it lists the feeds restore may reach.
/// <c>packageSourceMapping</c> is the one that gets missed, because clearing the first has no
/// effect on it — it says which source each package id pattern is <em>allowed</em> to come from,
/// and an inherited rule routing a pattern to a feed this repository does not declare produces
/// NU1101 for a package sitting on nuget.org. The error names a missing package; the cause is a
/// mapping nobody in this repository wrote. Security still holds either way, since restore only
/// ever resolves among declared sources — what does not hold is anyone's ability to read the
/// failure.
/// </para>
/// <para>
/// Held as a test rather than as a comment in the file, for the same reason the workflow pins are:
/// this is a convention about a handful of lines that a later contributor adding a feed has every
/// incentive to loosen, and the symptom of getting it wrong appears on someone else's machine.
/// <see cref="RestorePinningTests"/> runs it over the real file and over fixtures that break each
/// rule on purpose, so the guard is proven to bite.
/// </para>
/// </remarks>
internal static class RestorePinning
{
    /// <summary>The one source this repository resolves packages from.</summary>
    internal const string PublicFeed = "https://api.nuget.org/v3/index.json";

    /// <summary>The pattern that has to be mapped for the mapping section to cover everything.</summary>
    private const string EveryPackage = "*";

    /// <summary>
    /// Returns one message per violation in <paramref name="configText"/>, or an empty list when
    /// both halves of source resolution are pinned. <paramref name="configPath"/> is used only to
    /// name the file in a message.
    /// </summary>
    internal static IReadOnlyList<string> Inspect(string configPath, string configText)
    {
        var violations = new List<string>();
        XElement root = XDocument.Parse(configText).Root
            ?? throw new ArgumentException($"{configPath} has no root element.", nameof(configText));

        XElement? sources = root.Element("packageSources");
        XElement? mapping = root.Element("packageSourceMapping");

        if (sources is null)
        {
            violations.Add(
                $"{configPath}: declares no <packageSources>, so restore reaches every feed the machine-level config defines and a mirrored copy of a package can substitute the real one. Add the section, open it with <clear />, and declare {PublicFeed}.");
        }
        else
        {
            // First element, not merely present: a <clear /> after an <add> discards the entry
            // above it, which reads like a pin and declares nothing.
            if (sources.Elements().FirstOrDefault()?.Name.LocalName != "clear")
            {
                violations.Add(
                    $"{configPath}: <packageSources> does not open with <clear />, so it adds this repository's feed to the machine's rather than replacing them.");
            }

            foreach (XElement added in sources.Elements("add"))
            {
                string value = added.Attribute("value")?.Value ?? string.Empty;

                if (!string.Equals(value, PublicFeed, StringComparison.Ordinal))
                {
                    violations.Add(
                        $"{configPath}: declares the source '{value}'. This package resolves from {PublicFeed} and nothing else — a second feed is a second place a package of this name could come from, which is a decision to make in review rather than in a config file.");
                }
            }
        }

        if (mapping is null)
        {
            violations.Add(
                $"{configPath}: declares no <packageSourceMapping>, so the machine's mapping rules apply unchanged and may route a package id at a source this file does not declare. Restore then fails with NU1101 for a package that is on {PublicFeed}, naming the package rather than the mapping. Add the section, open it with <clear />, and map '{EveryPackage}' to nuget.org.");

            return violations;
        }

        if (mapping.Elements().FirstOrDefault()?.Name.LocalName != "clear")
        {
            violations.Add(
                $"{configPath}: <packageSourceMapping> does not open with <clear />, so the machine's mapping rules survive alongside this file's.");
        }

        // Keys compared case-insensitively because NuGet resolves a source key that way, so a
        // mapping onto "NuGet.org" names the declared source rather than a missing one.
        var declaredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (XElement added in sources?.Elements("add") ?? [])
        {
            string? key = added.Attribute("key")?.Value;

            if (!string.IsNullOrEmpty(key))
            {
                declaredKeys.Add(key);
            }
        }

        var mappedPatterns = new List<string>();

        foreach (XElement mapped in mapping.Elements("packageSource"))
        {
            string key = mapped.Attribute("key")?.Value ?? string.Empty;

            // A mapping onto a source this file cleared is the NU1101 trap written down rather
            // than inherited, so it is worth catching even though the intent is obviously good.
            if (!declaredKeys.Contains(key))
            {
                violations.Add(
                    $"{configPath}: maps packages to the source '{key}', which <packageSources> does not declare. Every id matching it would fail to resolve with NU1101.");
            }

            mappedPatterns.AddRange(
                mapped.Elements("package")
                    .Select(package => package.Attribute("pattern")?.Value ?? string.Empty));
        }

        if (!mappedPatterns.Contains(EveryPackage, StringComparer.Ordinal))
        {
            violations.Add(
                $"{configPath}: <packageSourceMapping> maps no '{EveryPackage}' pattern, so any package id it does not name is left to whatever the machine's rules say — which is the state the section was cleared to get out of.");
        }

        return violations;
    }
}
