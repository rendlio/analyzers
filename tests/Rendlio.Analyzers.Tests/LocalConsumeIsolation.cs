using System.Xml.Linq;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Reads the restore configuration the local consume check copies, and reports the ways it would
/// let something other than the package just packed answer the check.
/// </summary>
/// <remarks>
/// Installing the package into a throwaway consumer is the only check that exercises
/// <c>analyzers/dotnet/cs</c>, the one path NuGet reads to install a C# analyzer. Everything in
/// this suite drives the analyzer classes directly, so a pack that stopped producing that path
/// would install as a no-op with every test here green.
/// <para>
/// What makes that check fragile is that it can pass without having installed anything new. NuGet
/// keys the packages folder by id and version alone, and this project's version does not move
/// between changes — so once the package has been extracted there it is never extracted again,
/// whatever a fresh pack wrote to the feed. The consumer builds, restore reports success, and the
/// compiler is handed an assembly from an earlier build. It fails in both directions: a broken
/// package reads green off a good cached copy, and a good package reads red off a stale one.
/// </para>
/// <para>
/// So the load-bearing lines are the ones with no visible effect on a run that passes — a packages
/// folder of the check's own, the machine's fallback folders cleared, the package's id pinned at
/// the scratch feed. Remove any of them and everything observable about the check is unchanged,
/// which is the shape of an edit nobody questions. <see cref="LocalConsumeIsolationTests"/> runs
/// this over the committed file and over fixtures that break each rule on purpose.
/// </para>
/// </remarks>
internal static class LocalConsumeIsolation
{
    /// <summary>The committed configuration, relative to the repository root.</summary>
    internal const string ConfigPath = "eng/local-consume/nuget.config";

    /// <summary>The name NuGet discovers a configuration file by, walking up from a project.</summary>
    internal const string ConfigFileName = "nuget.config";

    /// <summary>The source key the scratch feed is declared under.</summary>
    internal const string FeedKey = "local";

    /// <summary>The package whose resolution the check is about.</summary>
    internal const string PackageId = "Rendlio.Analyzers";

    /// <summary>The mapping pattern that covers it, and the one a missing mapping is told to add.</summary>
    internal const string PackagePattern = "Rendlio.*";

    /// <summary>The <c>config</c> key naming the folder restore extracts packages into.</summary>
    private const string PackagesFolderKey = "globalPackagesFolder";

    /// <summary>
    /// Returns one message per way <paramref name="configText"/> would let a copy other than the
    /// one just packed satisfy the check, or an empty list when it is sealed against all of them.
    /// <paramref name="configPath"/> only names the file in a message; nothing is read from disk.
    /// </summary>
    internal static IReadOnlyList<string> Inspect(string configPath, string configText)
    {
        var violations = new List<string>();
        XElement root = XDocument.Parse(configText).Root
            ?? throw new ArgumentException($"{configPath} has no root element.", nameof(configText));

        InspectPackagesFolder(configPath, root, violations);
        InspectFallbackFolders(configPath, root, violations);
        InspectSources(configPath, root, violations);
        InspectMapping(configPath, root, violations);

        return violations;
    }

    /// <summary>The folder restore extracts into, which is the whole reason this file exists.</summary>
    private static void InspectPackagesFolder(string configPath, XElement root, List<string> violations)
    {
        // Matched without regard to case, the way NuGet reads a config key, so a differently
        // spelled but working line is not reported as an absent one.
        XElement? folder = root.Element("config")
            ?.Elements("add")
            .FirstOrDefault(add => string.Equals(
                add.Attribute("key")?.Value, PackagesFolderKey, StringComparison.OrdinalIgnoreCase));

        string? value = folder?.Attribute("value")?.Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            violations.Add(
                $"{configPath}: sets no {PackagesFolderKey}, so the consumer restores into the machine-wide packages folder. NuGet keys that folder by id and version, and this package's version does not move between changes — so an extraction left there by any earlier build satisfies the restore and the pack under test is never opened. Add <config><add key=\"{PackagesFolderKey}\" value=\"packages\" /></config>.");

            return;
        }

        if (Path.IsPathRooted(value))
        {
            violations.Add(
                $"{configPath}: points {PackagesFolderKey} at the absolute path '{value}'. This file is copied into a scratch directory and read from there, and a relative value is resolved against wherever it was copied to — which is what lets one committed file be correct on every machine. An absolute one names a folder on whoever wrote it, and the check quietly shares whatever is already in it.");
        }
    }

    /// <summary>The other folder a restore may answer an id and version out of.</summary>
    private static void InspectFallbackFolders(string configPath, XElement root, List<string> violations)
    {
        XElement? fallback = root.Element("fallbackPackageFolders");

        if (fallback is null)
        {
            violations.Add(
                $"{configPath}: declares no <fallbackPackageFolders>, so the machine's stay in force. A fallback folder is one restore may satisfy a package out of without downloading it, and Visual Studio installs one machine-wide — the {PackagesFolderKey} trap again, through a door nobody here configured. Add the section and open it with <clear />.");

            return;
        }

        // First element rather than merely present, for the reason it is on the sections below: a
        // <clear /> written after an entry discards what precedes it and inherits everything else.
        if (fallback.Elements().FirstOrDefault()?.Name.LocalName != "clear")
        {
            violations.Add(
                $"{configPath}: <fallbackPackageFolders> does not open with <clear />, so the machine's fallback folders are searched alongside anything this file names.");
        }
    }

    /// <summary>The feeds the check may reach, one of which has to be the scratch feed.</summary>
    private static void InspectSources(string configPath, XElement root, List<string> violations)
    {
        XElement? sources = root.Element("packageSources");

        if (sources is null)
        {
            violations.Add(
                $"{configPath}: declares no <packageSources>, so the scratch feed is not reachable and the check has nothing to install the pack from.");

            return;
        }

        if (sources.Elements().FirstOrDefault()?.Name.LocalName != "clear")
        {
            violations.Add(
                $"{configPath}: <packageSources> does not open with <clear />, so the check runs against the machine's feeds as well as its own rather than against a set it states.");
        }

        if (!sources.Elements("add").Any(add => string.Equals(
                add.Attribute("key")?.Value, FeedKey, StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add(
                $"{configPath}: declares no source keyed '{FeedKey}'. That is the scratch feed the pack is written to and the name the mapping refers to; without it the mapping routes {PackageId} at a source nothing declares, and restore fails NU1101 for a package sitting right there.");
        }
    }

    /// <summary>The rules that decide which of those feeds the package under test comes from.</summary>
    private static void InspectMapping(string configPath, XElement root, List<string> violations)
    {
        XElement? mapping = root.Element("packageSourceMapping");

        if (mapping is null)
        {
            violations.Add(
                $"{configPath}: declares no <packageSourceMapping>, so the machine's mapping rules apply unchanged. One routing '*' at some other feed survives clearing the sources and answers NU1101 for a package sitting in the scratch feed — an error naming the package rather than the rule that hid it. Add the section, open it with <clear />, and map '{PackagePattern}' at '{FeedKey}'.");

            return;
        }

        if (mapping.Elements().FirstOrDefault()?.Name.LocalName != "clear")
        {
            violations.Add(
                $"{configPath}: <packageSourceMapping> does not open with <clear />, so the machine's mapping rules apply alongside this file's.");
        }

        // Where an id resolves from is decided by the longest pattern matching it, not by which
        // patterns exist — so the rule is about that outcome rather than about any spelling. A
        // mapping naming the id exactly is as correct as one naming the family, and reporting it
        // would be a rule about spelling wearing the words of a rule about resolution.
        int longest = -1;
        var winners = new List<string>();

        foreach (XElement source in mapping.Elements("packageSource"))
        {
            string key = source.Attribute("key")?.Value ?? string.Empty;

            foreach (XElement package in source.Elements("package"))
            {
                int length = MatchLength(package.Attribute("pattern")?.Value ?? string.Empty, PackageId);

                // A pattern that does not match is not a shorter match: '*' matches nothing of it
                // and scores zero, while a pattern for some other package scores -1 and takes no
                // part. Collapsing the two would let a mapping naming only other packages read as
                // one that resolves this id.
                if (length < 0 || length < longest)
                {
                    continue;
                }

                if (length > longest)
                {
                    longest = length;
                    winners.Clear();
                }

                winners.Add(key);
            }
        }

        if (winners.Count == 0)
        {
            violations.Add(
                $"{configPath}: maps no pattern that matches {PackageId}, so nothing in the cleared section can resolve it and restore fails NU1101. Map '{PackagePattern}' at '{FeedKey}'.");

            return;
        }

        foreach (string key in winners.Where(key =>
                     !string.Equals(key, FeedKey, StringComparison.OrdinalIgnoreCase)))
        {
            violations.Add(
                $"{configPath}: resolves {PackageId} at the source '{key}' rather than at '{FeedKey}', because that is where its longest matching pattern points. Left to a wider rule the check grades whatever that source answers with — which is the scratch feed today only because nothing of this name is published yet, and the released package the day one is.");
        }
    }

    /// <summary>
    /// How much of <paramref name="packageId"/> the mapping pattern <paramref name="pattern"/>
    /// matches, or <c>-1</c> when it does not match at all.
    /// </summary>
    /// <remarks>
    /// NuGet compares ids without regard to case, reads a trailing <c>*</c> as a prefix and
    /// anything else as a whole id, and resolves at the source whose longest match wins. Length is
    /// therefore the comparison, and an exact id beats every prefix of itself because it is longer.
    /// </remarks>
    private static int MatchLength(string pattern, string packageId)
    {
        if (pattern.EndsWith('*'))
        {
            string prefix = pattern[..^1];

            return packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? prefix.Length : -1;
        }

        return string.Equals(pattern, packageId, StringComparison.OrdinalIgnoreCase) ? pattern.Length : -1;
    }
}
