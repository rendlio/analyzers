using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds every page this repository publishes to <see cref="ShippedText"/>, and holds
/// <see cref="ShippedText"/> itself to fixtures that break each rule on purpose. The links those
/// pages make into this repository are resolved with <see cref="PageLinks"/> and checked against
/// the files on disk, so a rename cannot quietly leave a page pointing at nothing.
/// </summary>
public sealed partial class ShippedTextTests
{
    /// <summary>
    /// Directory segments that are not published: the private notes tree, build output, and the
    /// repository's own plumbing. Anything else carrying a page is fair game for the rules.
    /// </summary>
    private static readonly string[] _unpublishedDirectories = ["docs/internal", ".git", ".conductor", "bin", "obj", "artifacts"];

    /// <summary>
    /// The repository root, resolved by <see cref="RepositoryLayout"/> — which the workflow rules
    /// share, so the walk and the reason for doing it that way live in one place.
    /// </summary>
    private static string RepositoryRoot => RepositoryLayout.Root;

    [Fact]
    public void Every_published_page_meets_the_publishing_rules()
    {
        List<string> violations = [];

        foreach (string page in PublishedPages())
        {
            violations.AddRange(
                ShippedText.Inspect(Path.GetRelativePath(RepositoryRoot, page), File.ReadAllText(page)));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void The_readme_is_one_of_the_pages_that_gets_checked()
    {
        // Guards the guard. If the walk above ever stops finding files, every rule passes over an
        // empty set and the whole check goes quietly decorative while still reporting green.
        Assert.Contains(PublishedPages(), p => Path.GetFileName(p) == "README.md");
    }

    [Fact]
    public void The_private_notes_tree_is_not_treated_as_published()
    {
        // The private tree exists precisely so internal vocabulary has somewhere to live. Scanning
        // it would either fail the build on notes that are never published, or — worse — push
        // someone to launder the notes instead of the pages.
        Assert.DoesNotContain(
            PublishedPages(),
            p => Normalise(p).Contains("docs/internal", StringComparison.Ordinal));
    }

    [Fact]
    public void The_repository_ships_under_apache_2()
    {
        // Load-bearing rather than ceremonial: this repository carries no engine-licensed code, and
        // the package tells everyone who installs it that Apache-2.0 is what they are getting.
        string licence = File.ReadAllText(Path.Combine(RepositoryRoot, "LICENSE"));

        Assert.Contains("Apache License", licence, StringComparison.Ordinal);
        Assert.Contains("Version 2.0, January 2004", licence, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_accepts_a_compliant_page()
    {
        IReadOnlyList<string> violations = ShippedText.Inspect(
            "fixture.md",
            "Rendlio Sheets renders spreadsheets. The engine is source-available under its own terms.");

        Assert.Empty(violations);
    }

    [Fact]
    public void Guard_accepts_a_product_name_split_across_a_soft_wrap()
    {
        // The real pages are hard-wrapped, so this is the common case rather than an edge one.
        Assert.Empty(ShippedText.Inspect("fixture.md", "…rendered by Rendlio\nSheets on every commit."));
    }

    [Fact]
    public void Guard_does_not_invent_a_product_across_a_paragraph_break()
    {
        // "Rendlio" ending a paragraph must not bind to the next paragraph's first word.
        Assert.Empty(ShippedText.Inspect("fixture.md", "…built by Rendlio\n\nSheets are rendered per commit."));
    }

    [Theory]
    // The product name here is deliberately fictitious: a fixture only needs a name that is not the
    // announced one, and naming a real unannounced product to test the rule would break it.
    [InlineData("Rendlio Widgets renders charts.", "not an announced product")]
    [InlineData("The engine is open source.", "source-available")]
    [InlineData("The engine is open-source.", "source-available")]
    [InlineData("Rationale lives in docs/internal/design.md.", "means nothing to a reader")]
    [InlineData("Required by FS-42 §3.", "means nothing to a reader")]
    public void Guard_rejects_a_page_that_breaks_a_rule(string page, string expected)
    {
        IReadOnlyList<string> violations = ShippedText.Inspect("fixture.md", page);

        Assert.Contains(violations, v => v.Contains(expected, StringComparison.Ordinal));
    }

    // ------------------------------------------- the triage policy, and the links that reach it

    /// <summary>The published triage policy, at the repository root.</summary>
    private const string TriagePolicy = "TRIAGE.md";

    /// <summary>The published security policy, at the repository root.</summary>
    private const string SecurityPolicy = "SECURITY.md";

    /// <summary>
    /// This repository's URL, read back from the one file that declares it.
    /// </summary>
    /// <remarks>
    /// The package metadata takes the same value from the same place, so reading it rather than
    /// repeating it is what keeps the absolute links on the packed README checked against the
    /// repository they are meant to point at — and keeps this test right if the URL ever changes.
    /// </remarks>
    private static string RepositoryUrl =>
        XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"))
            .Descendants("RepositoryUrl").Single().Value.Trim();

    [Fact]
    public void The_triage_policy_is_published_at_the_repository_root()
    {
        // Shipping a free package with no stated support posture is the one promise this
        // repository exists not to make, so the policy being published is a precondition of
        // shipping rather than a nicety. Pinned by path and not by prose: a rename is what would
        // drop the page out of the published set without any page looking wrong.
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, TriagePolicy)));
        Assert.Contains(PublishedPages(), p => Path.GetFileName(p) == TriagePolicy);
    }

    [Fact]
    public void The_readme_links_to_the_triage_policy()
    {
        // The README is the only page a consumer is guaranteed to see — it is what nuget.org
        // renders — so the policy is reachable only through a link from it. Asserted through the
        // resolver rather than by searching for a literal, so it holds for either spelling of the
        // link and fails if the README ever points at a different repository.
        IReadOnlyList<string> targets = PageLinks.RepositoryTargets(
            "README.md",
            File.ReadAllText(Path.Combine(RepositoryRoot, "README.md")),
            RepositoryUrl);

        Assert.Contains(TriagePolicy, targets);
    }

    [Fact]
    public void The_security_policy_is_published_at_the_repository_root()
    {
        // Pinned by path for a harder reason than the triage policy is: GitHub surfaces this page
        // in the repository's Security tab and links it from the new-issue flow, and it reads it
        // from the root, `.github/` or `docs/` and nowhere else. So a move that looks like tidying
        // silently removes the page from the one place a reporter goes looking, while the file
        // still exists and every link to it still resolves. Nothing else would notice.
        Assert.True(File.Exists(Path.Combine(RepositoryRoot, SecurityPolicy)));
        Assert.Contains(PublishedPages(), p => Path.GetFileName(p) == SecurityPolicy);
    }

    [Fact]
    public void The_readme_links_to_the_security_policy()
    {
        // Same reasoning as the triage-policy link, one degree more load-bearing: a consumer who
        // reads only what nuget.org renders has to be able to reach the private disclosure route
        // from there, or their alternative is the public issue tracker — which is the one place
        // this policy asks them not to put it.
        IReadOnlyList<string> targets = PageLinks.RepositoryTargets(
            "README.md",
            File.ReadAllText(Path.Combine(RepositoryRoot, "README.md")),
            RepositoryUrl);

        Assert.Contains(SecurityPolicy, targets);
    }

    [Fact]
    public void Both_policies_state_the_same_acknowledgment_window()
    {
        // The window is a promise to a reporter, and it is stated on both pages on purpose: the
        // security policy would be evasive without it, and the triage policy is where every other
        // number this repository commits to lives. Two statements of one number drift — so the
        // number is pinned to itself here rather than to a literal, which would need editing in a
        // third place to move it and would go stale the same way.
        Match onTriagePolicy = AcknowledgmentWindow().Match(
            File.ReadAllText(Path.Combine(RepositoryRoot, TriagePolicy)));
        Match onSecurityPolicy = AcknowledgmentWindow().Match(
            File.ReadAllText(Path.Combine(RepositoryRoot, SecurityPolicy)));

        Assert.True(onTriagePolicy.Success, $"{TriagePolicy} no longer states an acknowledgment window.");
        Assert.True(onSecurityPolicy.Success, $"{SecurityPolicy} no longer states an acknowledgment window.");
        Assert.Equal(onTriagePolicy.Groups[1].Value, onSecurityPolicy.Groups[1].Value, ignoreCase: true);
    }

    /// <summary>How long a report waits for a human, capturing the number of working days.</summary>
    /// <remarks>
    /// Spelled out rather than numeric because both pages write it that way, and deliberately not
    /// anchored to either page's surrounding sentence: it has to keep matching when one of them is
    /// reworded, or the pin becomes a tax on editing prose rather than a guard on the promise.
    /// </remarks>
    [GeneratedRegex(@"acknowledged within (\w+) working days",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AcknowledgmentWindow();

    [Fact]
    public void Every_link_from_a_published_page_into_this_repository_resolves()
    {
        List<string> broken = [];
        int linksChecked = 0;

        foreach (string page in PublishedPages())
        {
            string relativePage = Normalise(Path.GetRelativePath(RepositoryRoot, page));

            foreach (string target in
                     PageLinks.RepositoryTargets(relativePage, File.ReadAllText(page), RepositoryUrl))
            {
                linksChecked++;
                string resolved = Path.Combine(RepositoryRoot, target);

                if (!File.Exists(resolved) && !Directory.Exists(resolved))
                {
                    broken.Add($"{relativePage}: links to '{target}', which is not in this repository.");
                }
            }
        }

        // Guards the guard, for the reason the walk above is guarded: a resolver that quietly
        // stopped matching would leave nothing to check and report green forever.
        Assert.NotEqual(0, linksChecked);
        Assert.Empty(broken);
    }

    [Fact]
    public void Every_shipped_rule_links_to_a_page_that_is_in_this_repository()
    {
        // The conventions in AnalyzerConventionTests require an absolute http(s) help link, but
        // they never touch the disk, so they cannot check it points at anything. This is the other
        // half: a rule a stranger cannot look up is a rule they suppress rather than fix, and a
        // link to a page that has been renamed reads exactly like one that works.
        List<string> broken = [];
        int linksChecked = 0;

        foreach (Type type in AnalyzerConventions.AnalyzersIn(Assembly.Load("Rendlio.Analyzers")))
        {
            // A rule that cannot be constructed is already a failure of the conventions test; there
            // is nothing further for this one to say about it.
            if (Activator.CreateInstance(type) is not DiagnosticAnalyzer analyzer)
            {
                continue;
            }

            foreach (DiagnosticDescriptor rule in analyzer.SupportedDiagnostics)
            {
                linksChecked++;

                // Written the way a page would write it, so the same resolver decides what "names a
                // file in this repository" means for a help link and for a link on a page.
                string? target = PageLinks
                    .RepositoryTargets("README.md", $"[{rule.Id}]({rule.HelpLinkUri})", RepositoryUrl)
                    .SingleOrDefault();

                if (target is null || !File.Exists(Path.Combine(RepositoryRoot, target)))
                {
                    broken.Add($"{rule.Id}: help link '{rule.HelpLinkUri}' is not a page in this repository.");
                }
            }
        }

        // Guards the guard: an empty pack would otherwise report green forever.
        Assert.NotEqual(0, linksChecked);
        Assert.Empty(broken);
    }

    // ------------------------------------------------- the rules index, and the links that reach it

    /// <summary>The published index of the rules this package ships.</summary>
    private const string RulesIndex = "docs/rules/README.md";

    [Fact]
    public void The_readme_links_to_the_rules_index()
    {
        // Same reasoning as the triage policy above: the README is what nuget.org renders and the
        // only page a consumer is guaranteed to land on, so a page it does not link to is a page
        // nobody finds. Asserted through the resolver rather than by searching for a literal, so
        // it holds for either spelling of the link.
        IReadOnlyList<string> targets = PageLinks.RepositoryTargets(
            "README.md",
            File.ReadAllText(Path.Combine(RepositoryRoot, "README.md")),
            RepositoryUrl);

        Assert.Contains(RulesIndex, targets);
    }

    [Fact]
    public void Every_shipped_rule_is_listed_in_the_rules_index()
    {
        // The other half of the promise the help links make. A rule whose page exists but is
        // reachable only by already knowing its id is documented for someone who does not need the
        // documentation; the reader who needs it arrived from the README with no id in hand. This
        // is also what makes the index self-maintaining: syncing a rule in without listing it here
        // fails the build rather than shipping an index that silently understates the pack.
        IReadOnlyList<string> listed = PageLinks.RepositoryTargets(
            RulesIndex,
            File.ReadAllText(Path.Combine(RepositoryRoot, RulesIndex)),
            RepositoryUrl);

        List<string> missing = [];
        int rulesChecked = 0;

        foreach (Type type in AnalyzerConventions.AnalyzersIn(Assembly.Load("Rendlio.Analyzers")))
        {
            if (Activator.CreateInstance(type) is not DiagnosticAnalyzer analyzer)
            {
                continue;
            }

            foreach (DiagnosticDescriptor rule in analyzer.SupportedDiagnostics)
            {
                rulesChecked++;

                // Compared as the page the rule points at rather than as the id, so the two
                // spellings — the absolute help link and the index's relative one — are resolved by
                // the same resolver before being matched against each other.
                string? page = PageLinks
                    .RepositoryTargets("README.md", $"[{rule.Id}]({rule.HelpLinkUri})", RepositoryUrl)
                    .SingleOrDefault();

                if (page is null || !listed.Contains(page, StringComparer.Ordinal))
                {
                    missing.Add($"{rule.Id}: its page is not linked from {RulesIndex}.");
                }
            }
        }

        // Guards the guard, as above: an empty pack would report green forever.
        Assert.NotEqual(0, rulesChecked);
        Assert.Empty(missing);
    }

    [Fact]
    public void The_index_describes_every_rule_the_way_it_actually_ships()
    {
        // The index's table restates three things the descriptor already declares — a title, a
        // category and a default severity — and a restatement nobody compares is one that drifts.
        // Two of the three are held from the other side: the release-tracking analyzer fails the
        // build when a category or a severity moves without a release entry, and the suppression
        // snippets on these pages are checked against the shipped categories. The title has no
        // guard at all, and none of the three is checked as the *cell a reader reads*. Getting one
        // wrong is not cosmetic: a reader told a rule is a warning does not go looking for why the
        // build failed, and one told the wrong category writes a bulk switch that silences nothing.
        string[] rows = File.ReadAllLines(Path.Combine(RepositoryRoot, RulesIndex));

        List<string> wrong = [];
        int rulesChecked = 0;

        foreach (DiagnosticDescriptor rule in ShippedRules())
        {
            rulesChecked++;

            string? row = rows.SingleOrDefault(r => r.Contains($"[{rule.Id}](", StringComparison.Ordinal));

            if (row is null)
            {
                // Which page the row links to is the neighbouring case's business; this one only
                // needs the row to exist before it can read cells out of it.
                wrong.Add($"{rule.Id}: {RulesIndex} has no table row naming it.");
                continue;
            }

            // A Markdown row opens and closes with a pipe, so splitting leaves an empty cell at
            // each end and the columns land 1-based. Backticks are the index's own emphasis on the
            // category cell, not part of the value.
            string[] cells = [.. row.Split('|').Select(cell => cell.Trim().Trim('`'))];

            Compare(wrong, rule.Id, "title", cells, column: 2, rule.Title.ToString(CultureInfo.InvariantCulture));
            Compare(wrong, rule.Id, "category", cells, column: 3, rule.Category);
            Compare(wrong, rule.Id, "default severity", cells, column: 4, rule.DefaultSeverity.ToString());
        }

        // Guards the guard, as above: an empty pack would report green forever.
        Assert.NotEqual(0, rulesChecked);
        Assert.Empty(wrong);
    }

    /// <summary>
    /// Records a mismatch when the cell at <paramref name="column"/> is not <paramref name="shipped"/>.
    /// A row with too few cells reads as a mismatch rather than an exception, so a table someone
    /// reshapes reports which column stopped lining up instead of an index that is out of range.
    /// </summary>
    private static void Compare(
        List<string> wrong,
        string id,
        string what,
        string[] cells,
        int column,
        string shipped)
    {
        string published = column < cells.Length ? cells[column] : "<no such column>";

        if (!string.Equals(published, shipped, StringComparison.Ordinal))
        {
            wrong.Add($"{id}: {RulesIndex} gives its {what} as '{published}', but it ships as '{shipped}'.");
        }
    }

    /// <summary>
    /// Every rule the package ships, as the descriptor that declares it. Constructed from the
    /// shipping assembly rather than listed here, so a rule synced in is one these cases cover
    /// without anyone remembering to add it.
    /// </summary>
    private static IEnumerable<DiagnosticDescriptor> ShippedRules() =>
        AnalyzerConventions.AnalyzersIn(Assembly.Load("Rendlio.Analyzers"))
            // A rule that cannot be constructed is already a failure of the conventions test; there
            // is nothing further for these to say about it.
            .Select(static type => Activator.CreateInstance(type) as DiagnosticAnalyzer)
            .Where(static analyzer => analyzer is not null)
            .SelectMany(static analyzer => analyzer!.SupportedDiagnostics);

    /// <summary>An <c>.editorconfig</c> severity key, capturing the rule id it names.</summary>
    [GeneratedRegex(@"dotnet_diagnostic\.([^.\s]+)\.severity", RegexOptions.CultureInvariant)]
    private static partial Regex ConfiguredRule();

    /// <summary>A bulk-configuration key, capturing the category it names.</summary>
    [GeneratedRegex(@"dotnet_analyzer_diagnostic\.category-([^.\s]+(?:\.[^.\s]+)*)\.severity", RegexOptions.CultureInvariant)]
    private static partial Regex ConfiguredCategory();

    /// <summary>A pragma, capturing the rule id it names.</summary>
    [GeneratedRegex(@"#pragma warning (?:disable|restore) (\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex PragmaRule();

    /// <summary>A project-file <c>NoWarn</c>, capturing the whole list it sets.</summary>
    [GeneratedRegex(@"<NoWarn>([^<]*)</NoWarn>", RegexOptions.CultureInvariant)]
    private static partial Regex NoWarnList();

    /// <summary>
    /// The names one capture holds. <c>NoWarn</c> is a list, and the spelling these pages publish
    /// leads with the MSBuild token carrying the inherited value, which is not an id and is dropped.
    /// Splitting also means a multi-id pragma reads as its ids rather than as the first one with a
    /// separator stuck to it.
    /// </summary>
    private static IEnumerable<string> Names(string captured) =>
        captured
            .Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static name => !name.StartsWith("$(", StringComparison.Ordinal));

    [Fact]
    public void Every_suppression_a_published_page_shows_names_something_this_pack_ships()
    {
        // A suppression snippet is the one thing on these pages a reader copies verbatim rather
        // than reads, so a typo in one does not look like a typo — it looks like a rule that
        // ignores configuration, and the reader concludes the pack cannot be turned off. Nothing
        // else catches it: a misspelled id is a perfectly valid .editorconfig line that silences
        // nothing, and no build anywhere warns about it.
        //
        // The reach is deliberately total: EVERY id and category named in a suppression anywhere on
        // a published page has to be one this pack ships. So a page that legitimately documents
        // someone else's id — one of the CS or NU suppressions this repository's own build props
        // carry, say — fails here too. That is this test biting, not a defect; the fix is to widen
        // it knowingly rather than to let an unrecognised id through.
        var shippedRules = new HashSet<string>(StringComparer.Ordinal);
        var shippedCategories = new HashSet<string>(StringComparer.Ordinal);

        foreach (Type type in AnalyzerConventions.AnalyzersIn(Assembly.Load("Rendlio.Analyzers")))
        {
            if (Activator.CreateInstance(type) is not DiagnosticAnalyzer analyzer)
            {
                continue;
            }

            foreach (DiagnosticDescriptor rule in analyzer.SupportedDiagnostics)
            {
                shippedRules.Add(rule.Id);
                shippedCategories.Add(rule.Category);
            }
        }

        List<string> unknown = [];
        int suppressionsChecked = 0;

        foreach (string page in PublishedPages())
        {
            string relativePage = Normalise(Path.GetRelativePath(RepositoryRoot, page));
            string text = File.ReadAllText(page);

            foreach ((Regex pattern, ISet<string> shipped, string what) in new[]
                     {
                         (ConfiguredRule(), (ISet<string>)shippedRules, "rule"),
                         (PragmaRule(), shippedRules, "rule"),
                         (NoWarnList(), shippedRules, "rule"),
                         (ConfiguredCategory(), shippedCategories, "category"),
                     })
            {
                foreach (Match match in pattern.Matches(text))
                {
                    foreach (string named in Names(match.Groups[1].Value))
                    {
                        suppressionsChecked++;

                        if (!shipped.Contains(named))
                        {
                            unknown.Add($"{relativePage}: shows '{match.Value}', but no shipped {what} is named '{named}'.");
                        }
                    }
                }
            }
        }

        // Guards the guard: pages that stopped carrying suppression snippets, or a pattern that
        // stopped matching them, would leave nothing to check and report green.
        Assert.NotEqual(0, suppressionsChecked);
        Assert.Empty(unknown);
    }

    // ------------------------------------------- the install snippet, and the version it pins

    /// <summary>The version this repository is currently building, read back from the one file
    /// that declares it.</summary>
    /// <remarks>
    /// Read rather than repeated, for the same reason <see cref="RepositoryUrl"/> is: the package
    /// takes its version from here too, so reading it is what keeps the check honest across a bump.
    /// </remarks>
    private static string DeclaredVersionPrefix =>
        XDocument.Load(Path.Combine(RepositoryRoot, "src", "Rendlio.Analyzers", "Rendlio.Analyzers.csproj"))
            .Descendants("VersionPrefix").Single().Value.Trim();

    /// <summary>A <c>PackageReference</c> to this package, capturing the version it pins.</summary>
    [GeneratedRegex(
        @"<PackageReference\s+Include=""Rendlio\.Analyzers""[^>]*?\sVersion=""([^""]*)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex InstallSnippetVersion();

    [Fact]
    public void The_install_snippet_pins_the_version_this_repository_builds()
    {
        // The README is the PackageReadmeFile, so it is what nuget.org renders on EVERY version's
        // listing — including versions published long after the line was written. The snippet is
        // also the first thing a consumer copies rather than reads. Left uncoupled, cutting a
        // release would leave the new listing telling people to install the previous one, and
        // nothing would go red: a stale-but-valid version resolves and installs quietly.
        string readme = File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));
        string declared = DeclaredVersionPrefix;

        var pinned = InstallSnippetVersion().Matches(readme)
            .Select(static match => match.Groups[1].Value)
            .ToList();

        // Guards the guard: a README that stopped carrying the snippet, or a pattern that stopped
        // matching it, would have nothing to disagree with and would report green forever.
        Assert.NotEmpty(pinned);
        Assert.All(pinned, version => Assert.Equal(declared, version));
    }

    [Theory]
    // The shape the README actually ships.
    [InlineData(@"<PackageReference Include=""Rendlio.Analyzers"" Version=""9.9.9"" PrivateAssets=""all"" />", "9.9.9")]
    // Attribute order is not fixed by anything, so the pattern must not depend on it.
    [InlineData(@"<PackageReference Include=""Rendlio.Analyzers"" PrivateAssets=""all"" Version=""9.9.9"" />", "9.9.9")]
    public void The_install_snippet_pattern_reads_the_version_whatever_the_attribute_order(
        string snippet, string expected)
    {
        Match match = InstallSnippetVersion().Match(snippet);

        Assert.True(match.Success);
        Assert.Equal(expected, match.Groups[1].Value);
    }

    [Fact]
    public void The_install_snippet_pattern_ignores_a_reference_to_a_different_package()
    {
        // Otherwise a page showing how to install something else would be read as this package's
        // pin and fail the build — or, worse, satisfy the guard above on its behalf.
        Assert.DoesNotMatch(
            InstallSnippetVersion(),
            @"<PackageReference Include=""Rendlio.Analyzers.Extras"" Version=""9.9.9"" />");
    }

    // ------------------------------------- the compiler-host floor, and the pin it is read from

    /// <summary>The Roslyn version this package compiles against, read back from the one file that
    /// pins it.</summary>
    /// <remarks>
    /// Read rather than repeated, for the reason <see cref="RepositoryUrl"/> is: the host floor the
    /// README states is a consequence of this pin, so reading it is what stops the two parting
    /// company when the pin moves.
    /// </remarks>
    private static string PinnedRoslynVersion =>
        XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Single(element => (string?)element.Attribute("Include") == "Microsoft.CodeAnalysis.CSharp")
            .Attribute("Version")!.Value.Trim();

    /// <summary>The Roslyn version the README names as the floor, as major.minor.</summary>
    [GeneratedRegex(@"builds against Roslyn ([0-9]+\.[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex StatedRoslynVersion();

    [Fact]
    public void The_readme_states_the_roslyn_version_this_package_is_actually_built_against()
    {
        // The README is the PackageReadmeFile, so this sentence is what nuget.org shows a consumer
        // deciding whether their compiler host can load the package at all — and getting it wrong
        // is not a typo, it is telling someone on VS 17.8 that a package which will not load there
        // will. The number is a consequence of the Microsoft.CodeAnalysis.CSharp pin, which
        // Directory.Packages.props itself calls a consumer-visible breaking change to raise; left
        // uncoupled, raising it drops every host below the new floor while the listing page goes on
        // promising the old one, and nothing goes red.
        string readme = File.ReadAllText(Path.Combine(RepositoryRoot, "README.md"));
        string pinned = PinnedRoslynVersion;
        string floor = string.Join('.', pinned.Split('.').Take(2));

        Match stated = StatedRoslynVersion().Match(readme);

        // Guards the guard: a README that stopped carrying the sentence, or a pattern that stopped
        // matching it, would have nothing to disagree with and would report green forever.
        Assert.True(stated.Success, "README.md no longer states the Roslyn version it builds against.");
        Assert.Equal(floor, stated.Groups[1].Value);

        // The Visual Studio and SDK floors in the same sentence follow from the Roslyn version but
        // are not derivable from anything in this repository, so they cannot be checked here. This
        // assertion is what forces someone bumping the pin to reach that sentence and revisit them.
    }

    [Theory]
    // The shape the README actually ships.
    [InlineData("this package builds against Roslyn 4.8, so it needs", "4.8")]
    // A hard wrap can land between the words, and the pages here are wrapped at column ~90.
    [InlineData("builds against Roslyn 10.11 and upwards", "10.11")]
    public void The_roslyn_floor_pattern_reads_the_version_the_sentence_states(
        string sentence, string expected)
    {
        Match match = StatedRoslynVersion().Match(sentence);

        Assert.True(match.Success);
        Assert.Equal(expected, match.Groups[1].Value);
    }

    /// <summary>
    /// Stand-in repository. Deliberately not the live URL: a fixture only needs some repository to
    /// resolve against, and pinning the real one here would couple these cases to a value the
    /// resolver is supposed to read from the build props. `.invalid` is reserved by RFC 2606.
    /// </summary>
    private const string FixtureRepositoryUrl = "https://example.invalid/owner/repo";

    [Theory]
    // A page beside the one linking to it, written relatively — how a page that never ships inside
    // the package can write it.
    [InlineData("README.md", "see the [policy](TRIAGE.md).", "TRIAGE.md")]
    // The same file as the absolute URL a page inside the package has to use, because a relative
    // link does not resolve on nuget.org.
    [InlineData("README.md", "see the [policy](https://example.invalid/owner/repo/blob/main/TRIAGE.md).", "TRIAGE.md")]
    // An anchor names a place inside a file, not a different file.
    [InlineData("README.md", "see [what is in scope](TRIAGE.md#in-scope).", "TRIAGE.md")]
    // A link label may straddle the ~90-column wrap these pages are written to.
    [InlineData("README.md", "see the [triage\npolicy](TRIAGE.md).", "TRIAGE.md")]
    // A relative link resolves against the directory holding the page, not the repository root.
    [InlineData("docs/public/guide.md", "a [sibling](other.md).", "docs/public/other.md")]
    [InlineData("docs/public/guide.md", "the [policy](../../TRIAGE.md).", "TRIAGE.md")]
    public void A_link_into_this_repository_resolves_to_the_file_it_names(
        string page,
        string markdown,
        string expected)
    {
        Assert.Equal(
            expected,
            Assert.Single(PageLinks.RepositoryTargets(page, markdown, FixtureRepositoryUrl)));
    }

    [Theory]
    // A page of the repository rather than a file in it: no such path exists on disk, and
    // reporting one would fail the build over a link that works.
    [InlineData("[issues](https://example.invalid/owner/repo/issues)")]
    // Someone else's site.
    [InlineData("[the licence text](https://www.apache.org/licenses/LICENSE-2.0)")]
    // An anchor on this page.
    [InlineData("[what is in scope](#in-scope)")]
    // An address, not a path. Live rather than theoretical: a private reporting route is exactly
    // the kind of link a policy page carries.
    [InlineData("[write to us](mailto:someone@example.invalid)")]
    // Not a link at all — a bracketed phrase that happens to precede a parenthesis.
    [InlineData("suppress it [as documented] (severity, NoWarn, pragma).")]
    public void A_link_that_names_no_file_in_this_repository_is_left_alone(string markdown)
    {
        Assert.Empty(PageLinks.RepositoryTargets("README.md", markdown, FixtureRepositoryUrl));
    }

    [Fact]
    public void A_link_to_a_file_that_is_not_here_is_visible_to_the_check()
    {
        // The check above is an existence test over whatever the resolver returns, so proving it
        // bites means proving a bad target survives resolution and then fails to resolve on disk.
        // Without this, a resolver that silently dropped broken links would still report green.
        // The name is one no page here could plausibly acquire, so a file someone legitimately
        // adds later cannot turn this fixture red for the wrong reason.
        string target = Assert.Single(
            PageLinks.RepositoryTargets("README.md", "read the [policy](no-such-page.invalid.md).", RepositoryUrl));

        Assert.False(File.Exists(Path.Combine(RepositoryRoot, target)));
    }

    [Fact]
    public void Every_anchor_a_published_page_points_at_is_a_heading_that_exists()
    {
        // The other half of the link check above, and the half that fails silently. That one proves
        // the FILE is there; a fragment naming a heading that has since been reworded still resolves
        // to that file, so nothing 404s — GitHub simply drops the reader at the top of the page and
        // says nothing. The resolver strips fragments on purpose, so before this case nothing read
        // them at all.
        //
        // Load-bearing rather than tidy: the security policy sends a reporter to
        // TRIAGE.md#response-expectations for the acknowledgment window it promises them, and to
        // #in-scope for what to report in public instead. Editing a heading in the triage policy is
        // an ordinary thing to do, and it is the reader chasing a commitment who pays for it.
        List<string> broken = [];
        int anchorsChecked = 0;

        foreach (string page in PublishedPages())
        {
            string relativePage = Normalise(Path.GetRelativePath(RepositoryRoot, page));

            foreach ((string target, string fragment) in
                     PageLinks.RepositoryAnchors(relativePage, File.ReadAllText(page), RepositoryUrl))
            {
                string resolved = Path.Combine(RepositoryRoot, target);

                // A target that is not a Markdown page here has no headings to name, and one that is
                // missing entirely is the neighbouring case's business rather than this one's.
                if (!target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !File.Exists(resolved))
                {
                    continue;
                }

                anchorsChecked++;

                if (!Headings(File.ReadAllText(resolved)).Contains(fragment, StringComparer.Ordinal))
                {
                    broken.Add(
                        $"{relativePage}: links to '{target}#{fragment}', but no heading there has that anchor.");
                }
            }
        }

        // Guards the guard, for the reason the link walk is guarded: an extractor that stopped
        // matching would leave nothing to check and report green forever.
        Assert.NotEqual(0, anchorsChecked);
        Assert.Empty(broken);
    }

    /// <summary>A Markdown ATX heading, capturing its text.</summary>
    /// <remarks>
    /// Requires whitespace after the hashes, which is what separates a heading from the
    /// <c>#pragma</c> and <c>#nullable</c> lines the code samples on these pages carry.
    /// </remarks>
    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}[ \t]+(.+?)[ \t]*#*[ \t]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Heading();

    /// <summary>Anything GitHub drops from a heading when it builds the anchor for it.</summary>
    /// <remarks>
    /// Letters, digits, spaces, hyphens and underscores survive; everything else — backticks, the
    /// full stop in <c>Rendlio.Analyzers</c>, punctuation — is removed rather than replaced, and
    /// spaces then become hyphens. That is GitHub's slug, and it is worth reproducing rather than
    /// guessing at: a heading whose anchor is not the one the writer assumed is exactly the case
    /// this guard exists to catch.
    /// </remarks>
    [GeneratedRegex(@"[^\p{L}\p{Nd} \-_]", RegexOptions.CultureInvariant)]
    private static partial Regex NotInAnAnchor();

    /// <summary>The anchor GitHub gives every heading in <paramref name="markdown"/>.</summary>
    private static IEnumerable<string> Headings(string markdown) =>
        Heading().Matches(markdown)
            .Select(static match => NotInAnAnchor()
                .Replace(match.Groups[1].Value.ToLowerInvariant(), string.Empty)
                .Replace(' ', '-'));

    [Theory]
    // The two headings the security policy actually points at.
    [InlineData("## Response expectations", "response-expectations")]
    [InlineData("## In scope", "in-scope")]
    // Punctuation is dropped rather than replaced, so a heading naming the package loses its dot.
    [InlineData("# `Rendlio.Analyzers` security", "rendlioanalyzers-security")]
    [InlineData("### What happens next?", "what-happens-next")]
    public void A_heading_gets_the_anchor_github_would_give_it(string heading, string expected) =>
        Assert.Equal(expected, Assert.Single(Headings(heading)));

    [Theory]
    // A directive in one of the code samples these pages carry, which is not a heading. Without the
    // whitespace requirement each of these would be read as one, and the anchors on a page would be
    // checked against headings nobody wrote.
    [InlineData("#nullable enable")]
    [InlineData("#pragma warning disable RENDLIO001")]
    public void A_line_that_only_looks_like_a_heading_is_not_read_as_one(string line) =>
        Assert.Empty(Headings(line));

    [Theory]
    // An anchor on a page beside the one linking to it: the shape the security policy ships.
    [InlineData("README.md", "the [window](TRIAGE.md#response-expectations).", "TRIAGE.md", "response-expectations")]
    // The absolute spelling a page that ships inside the package has to use.
    [InlineData(
        "README.md",
        "the [scope](https://example.invalid/owner/repo/blob/main/TRIAGE.md#in-scope).",
        "TRIAGE.md",
        "in-scope")]
    // An anchor with no path in front of it names a heading on the page carrying it.
    [InlineData("TRIAGE.md", "see [what is in scope](#in-scope).", "TRIAGE.md", "in-scope")]
    // Resolved against the directory holding the page, as a path is.
    [InlineData("docs/public/guide.md", "a [sibling](other.md#usage).", "docs/public/other.md", "usage")]
    public void An_anchored_link_resolves_to_the_page_and_heading_it_names(
        string page,
        string markdown,
        string expectedPath,
        string expectedFragment)
    {
        PageLinks.AnchoredTarget anchored =
            Assert.Single(PageLinks.RepositoryAnchors(page, markdown, FixtureRepositoryUrl));

        Assert.Equal(expectedPath, anchored.Path);
        Assert.Equal(expectedFragment, anchored.Fragment);
    }

    [Theory]
    // No anchor: the link check proper already covers the file, and there is no heading to read.
    [InlineData("read the [policy](TRIAGE.md).")]
    // A bare '#' names nothing.
    [InlineData("read the [policy](TRIAGE.md#).")]
    // Someone else's site. Their headings are not ours to hold, and this repository does not get to
    // fail its own build over a rename on learn.microsoft.com.
    [InlineData("[trusted publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing#how)")]
    public void A_link_with_no_anchor_in_this_repository_is_left_alone(string markdown) =>
        Assert.Empty(PageLinks.RepositoryAnchors("README.md", markdown, FixtureRepositoryUrl));

    [Fact]
    public void An_anchor_that_names_no_heading_is_visible_to_the_check()
    {
        // Proves the walk above bites, the way the broken-link fixture does for paths: a bad
        // fragment has to survive resolution and then fail to match a heading. Without this, an
        // extractor that silently dropped anchors would report green forever. The heading name is
        // one no page here could plausibly acquire, so a later edit cannot turn this red for the
        // wrong reason.
        PageLinks.AnchoredTarget anchored = Assert.Single(
            PageLinks.RepositoryAnchors("README.md", "the [window](TRIAGE.md#no-such-heading).", RepositoryUrl));

        Assert.DoesNotContain(
            anchored.Fragment,
            Headings(File.ReadAllText(Path.Combine(RepositoryRoot, anchored.Path))),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> PublishedPages() =>
        Directory.EnumerateFiles(RepositoryRoot, "*.md", SearchOption.AllDirectories)
            .Where(IsPublished)
            .OrderBy(p => p, StringComparer.Ordinal);

    private static bool IsPublished(string path)
    {
        string normalised = Normalise(path);

        return !_unpublishedDirectories.Any(
            d => normalised.Contains($"/{d}/", StringComparison.Ordinal));
    }

    /// <summary>Separators folded to '/', so one spelling of a path works on either OS.</summary>
    private static string Normalise(string path) => path.Replace('\\', '/');
}
