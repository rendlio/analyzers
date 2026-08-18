using System.Xml.Linq;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Reads the two files the local consume check copies — a restore configuration and the properties
/// beside it — and reports the ways either would let something other than the package just packed
/// answer the check.
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
/// this over the committed files and over fixtures that break each rule on purpose.
/// </para>
/// <para>
/// It takes two files because the settings are not the last word. NuGet reads
/// <c>NUGET_PACKAGES</c> ahead of the configuration and takes it without warning, so a correct
/// configuration isolates nothing on a machine that exports one — and exporting one is the ordinary
/// way a build cache is pointed somewhere. The MSBuild properties beside it are read ahead of both
/// the settings and the environment, so that is where the isolation is stated; the configuration
/// still carries it too, for the tools that read settings and for a reader of one file.
/// </para>
/// </remarks>
internal static class LocalConsumeIsolation
{
    /// <summary>The committed configuration, relative to the repository root.</summary>
    internal const string ConfigPath = "eng/local-consume/nuget.config";

    /// <summary>Its other half, which states the same thing where the environment cannot outrank it.</summary>
    internal const string PropsPath = "eng/local-consume/Directory.Build.props";

    /// <summary>The name NuGet discovers a configuration file by, walking up from a project.</summary>
    internal const string ConfigFileName = "nuget.config";

    /// <summary>The name MSBuild discovers a properties file by, walking up from a project.</summary>
    internal const string PropsFileName = "Directory.Build.props";

    /// <summary>The property naming the folder restore extracts into, ahead of every setting.</summary>
    internal const string PackagesPathProperty = "RestorePackagesPath";

    /// <summary>The property clearing the folders restore may answer out of without downloading.</summary>
    internal const string FallbackFoldersProperty = "RestoreFallbackFolders";

    /// <summary>The value that empties a NuGet list property rather than adding to it.</summary>
    private const string ClearValue = "clear";

    /// <summary>
    /// The only prefix that resolves against the copied file rather than against the consumer that
    /// imports it — an MSBuild property is evaluated in the importing project's directory.
    /// </summary>
    private const string ThisFileDirectory = "$(MSBuildThisFileDirectory)";

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

        if (!IsRelativeLocalPath(value))
        {
            violations.Add(
                $"{configPath}: points {PackagesFolderKey} at '{value}', which is not resolved against this file. It is copied into a scratch directory and read from there, and a relative value follows it — which is what lets one committed file be correct on every machine. An absolute one names a folder on whoever wrote it, and the check quietly shares whatever is already in it.");
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

        XElement? feed = sources.Elements("add").FirstOrDefault(add => string.Equals(
            add.Attribute("key")?.Value, FeedKey, StringComparison.OrdinalIgnoreCase));

        if (feed is null)
        {
            violations.Add(
                $"{configPath}: declares no source keyed '{FeedKey}'. That is the scratch feed the pack is written to and the name the mapping refers to; without it the mapping routes {PackageId} at a source nothing declares, and restore fails NU1101 for a package sitting right there.");

            return;
        }

        // The same argument the packages folder is held to, and for the same reason: this file is
        // read from wherever it was copied, so a value that does not follow it points somewhere
        // else on every machine but the one it was written on. A feed elsewhere is the louder half
        // of that — the check would grade whatever it answers with rather than the pack in hand.
        string feedValue = feed.Attribute("value")?.Value ?? string.Empty;

        if (!IsRelativeLocalPath(feedValue))
        {
            violations.Add(
                $"{configPath}: points the '{FeedKey}' source at '{feedValue}', which is not resolved against this file. The whole check rests on that source being the directory the pack was just written to; anywhere else and it grades a package this run did not build.");
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
    /// Returns one message per way <paramref name="propsText"/> fails to state the packages folder
    /// where the environment cannot outrank it, or an empty list when it does.
    /// <paramref name="propsPath"/> only names the file in a message; nothing is read from disk.
    /// </summary>
    /// <remarks>
    /// The settings in the configuration beside this file are not the last word on where restore
    /// extracts to. NuGet reads <c>NUGET_PACKAGES</c> ahead of <c>globalPackagesFolder</c> and takes
    /// it without a word, and <c>NUGET_FALLBACK_PACKAGES</c> survives that file clearing
    /// <c>fallbackPackageFolders</c> outright — so on a machine exporting either, a correct
    /// configuration produces an uninsulated run that reports success. MSBuild properties are read
    /// ahead of both, which makes this the only layer where the isolation can be stated rather than
    /// hoped for.
    /// </remarks>
    internal static IReadOnlyList<string> InspectProps(string propsPath, string propsText)
    {
        var violations = new List<string>();

        // Matched on local names so the reader works whether or not the file carries the old
        // MSBuild XML namespace. This one does not, because SDK-style projects dropped it; a
        // namespace is still the kind of thing a tool puts back, and a reader that silently found
        // nothing afterwards would report the file compliant for having become unreadable.
        List<XElement> properties =
            [.. XDocument.Parse(propsText)
                .Descendants()
                .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")];

        InspectProperty(
            propsPath,
            properties,
            PackagesPathProperty,
            $"sets no <{PackagesPathProperty}>, so where restore extracts to is left to the settings — which NUGET_PACKAGES overrides silently, putting the run back in the machine-wide folder this whole check exists to stay out of. Set it to {ThisFileDirectory}packages.",
            value => value.StartsWith(ThisFileDirectory, StringComparison.Ordinal)
                ? null
                : $"points <{PackagesPathProperty}> at '{value}'. A property here is evaluated in the consumer that imports it, not in this directory, so anything not opening with {ThisFileDirectory} resolves somewhere other than beside the feed the pack was written to.",
            violations);

        InspectProperty(
            propsPath,
            properties,
            FallbackFoldersProperty,
            $"sets no <{FallbackFoldersProperty}>, so a folder named by NUGET_FALLBACK_PACKAGES is still searched — and that variable survives the configuration clearing fallbackPackageFolders, which makes this the only place it is answered. Set it to '{ClearValue}'.",
            value => string.Equals(value, ClearValue, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"sets <{FallbackFoldersProperty}> to '{value}' rather than '{ClearValue}'. Anything else adds a folder to search instead of emptying the list, which is one more place an id and version can be answered from without downloading.",
            violations);

        return violations;
    }

    /// <summary>
    /// Reports <paramref name="name"/> missing, conditioned, or failing
    /// <paramref name="validate"/> — which returns a message for a bad value and <c>null</c> for a
    /// good one.
    /// </summary>
    private static void InspectProperty(
        string propsPath,
        List<XElement> properties,
        string name,
        string absent,
        Func<string, string?> validate,
        List<string> violations)
    {
        XElement? property = properties.FirstOrDefault(element => element.Name.LocalName == name);

        if (property is null)
        {
            violations.Add($"{propsPath}: {absent}");

            return;
        }

        // The nearest condition on the way out, rather than the one written on the property itself.
        // A <PropertyGroup> is where MSBuild convention puts a condition, and a <Choose>/<When>
        // around it is a third spelling of the same thing; each stops the property being set
        // exactly as an inline condition would. Nothing here has a reason to be conditional, and a
        // condition is precisely how a line whose effect is invisible on a passing run stops firing.
        XAttribute? condition = property.AncestorsAndSelf()
            .Select(element => element.Attribute("Condition"))
            .FirstOrDefault(attribute => attribute is not null);

        if (condition is not null)
        {
            violations.Add(
                $"{propsPath}: conditions <{name}> on \"{condition.Value}\", written on <{condition.Parent?.Name.LocalName}>. State it unconditionally — this file is copied into a directory made for one run, so there is no case it should not apply to, and a condition is how it comes to apply to none.");
        }

        string? failure = validate(property.Value.Trim());

        if (failure is not null)
        {
            violations.Add($"{propsPath}: {failure}");
        }
    }

    /// <summary>
    /// True when <paramref name="value"/> is a local path resolved against the file that declares
    /// it, rather than one naming a fixed place on the machine or a feed off it.
    /// </summary>
    /// <remarks>
    /// Both halves are needed. <see cref="Path.IsPathRooted(string)"/> catches an absolute path on
    /// either platform but not a URL, whose scheme makes it neither rooted nor relative; parsing it
    /// as an absolute <see cref="Uri"/> catches the URL, and on Windows an absolute path as well.
    /// </remarks>
    private static bool IsRelativeLocalPath(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !Path.IsPathRooted(value)
        && !Uri.TryCreate(value, UriKind.Absolute, out _);

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
