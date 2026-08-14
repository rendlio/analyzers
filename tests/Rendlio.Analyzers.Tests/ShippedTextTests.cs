using System.Collections.Immutable;
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
    // The package id, which every page here names repeatedly.
    [InlineData("Install Rendlio.Analyzers from nuget.org.")]
    [InlineData("The Rendlio.Analyzers package ships two rules.")]
    // A rule id. Family-scoped by design, and the same two words with no gap between them.
    [InlineData("RENDLIO001 bans network APIs.")]
    public void Guard_accepts_the_package_id_and_the_rule_ids(string page)
    {
        // The far edge of the rule, and the one worth pinning: an identifier is not a product name.
        // "Rendlio" plus a capitalised word IS the shape the rule reports, and both of these are one
        // character away from it — so a later widening of the gap between the two halves, from
        // horizontal whitespace to \s or to anything admitting '.', turns every page in this
        // repository red at once and names a product nobody wrote. The naming law is about prose;
        // the id a consumer types into a PackageReference is not prose.
        Assert.Empty(ShippedText.Inspect("fixture.md", page));
    }

    [Theory]
    // The phrasing these pages actually use, and the reason the rule is a lookahead rather than a
    // search for the full phrase: a page that qualifies the association correctly is compliant.
    [InlineData("Rendlio is built by a Swiss association in formation, with profits pledged to charities.")]
    // The same sentence hard-wrapped where the pages are wrapped. The qualifier routinely lands on
    // the next line, so this is the common case rather than an edge one.
    [InlineData("Rendlio is built by a Swiss association\nin formation, with profits pledged to charities.")]
    // The same wrap written as a Markdown HARD break. This repository preserves the construct on
    // purpose — .editorconfig turns trim_trailing_whitespace off for *.md precisely so the two
    // trailing spaces survive — and Unwrap folds only the newline, leaving them. So the gap inside
    // the phrase arrives at the pattern as THREE spaces, and any rule spelling it as one literal
    // space quietly stops applying to a page that still reads correctly.
    [InlineData("Rendlio is built by a Swiss association  \nin formation, with profits pledged to charities.")]
    public void Guard_accepts_the_identity_phrasing_these_pages_use(string page) =>
        Assert.Empty(ShippedText.Inspect("fixture.md", page));

    // Every fixture below breaks a rule on purpose, and each one is written in whichever of two
    // ways its rule allows — the distinction is worth stating once, because a sweep that greps this
    // repository for banned vocabulary lands here and nowhere else.
    //
    //   - Where a rule matches a SHAPE, the fixture uses a stand-in and never the real thing: the
    //     product names are fictitious, and the fidelity fixtures put nothing on the other side of
    //     "against". Spelling the real ones out here would publish, in a file that ships, exactly
    //     what those rules exist to keep off the pages.
    //   - Where a rule matches a PHRASE, the fixture has to carry that phrase, because a guard
    //     cannot prove it rejects something it does not contain. That is why "open source" appears
    //     below, and why "oracle" does: there is no fictitious equivalent of a specific string.
    //
    // Either way these are inputs being fed to a rule, not statements about the engine.
    [Theory]
    [InlineData("Rendlio Widgets renders charts.", "not an announced product")]
    // Emphasis is how a page would style a name, not a way around the rule — a reader sees the same
    // two words either way. Both spellings appear because they behave differently at the left edge:
    // '_' is a word character and '*' is not.
    [InlineData("**Rendlio** Widgets renders charts.", "not an announced product")]
    [InlineData("*Rendlio Widgets* renders charts.", "not an announced product")]
    [InlineData("_Rendlio_ Widgets renders charts.", "not an announced product")]
    [InlineData("Output is scored against a reference implementation.", "not something these pages describe")]
    [InlineData("The oracle decides whether a render is correct.", "not something these pages describe")]
    // The association stated as though it already existed — a one-word edit from the phrasing that
    // is true, and one that reads perfectly well, which is the whole reason it is pinned.
    [InlineData("Rendlio is built by a Swiss association.", "presents as existing")]
    // The same breach reached through a Markdown hard break, for both of the rules whose phrase
    // spans more than one word. This is the direction that matters: a gap a pattern cannot cross
    // does not make the guard noisy, it makes it SILENT. Both pages below are non-compliant, and
    // both read exactly as they would after an ordinary rewrap.
    [InlineData("Rendlio is built by a Swiss  \nassociation.", "presents as existing")]
    [InlineData("The engine is open  \nsource.", "source-available")]
    [InlineData("The engine is open source.", "source-available")]
    [InlineData("The engine is open-source.", "source-available")]
    [InlineData("Rationale lives in docs/internal/design.md.", "means nothing to a reader")]
    [InlineData("Required by FS-42 §3.", "means nothing to a reader")]
    public void Guard_rejects_a_page_that_breaks_a_rule(string page, string expected)
    {
        IReadOnlyList<string> violations = ShippedText.Inspect("fixture.md", page);

        Assert.Contains(violations, v => v.Contains(expected, StringComparison.Ordinal));
    }

    // -------------------------- the identity these pages state, and the text the walk cannot see

    /// <summary>How this repository states who is behind Rendlio.</summary>
    /// <remarks>
    /// The qualifier is the load-bearing half, and <see cref="ShippedText"/> already rejects the
    /// association stated without it — but a rule that rejects a wrong sentence cannot notice a
    /// sentence that is no longer there. This is the other half of it.
    /// <para>
    /// Gaps are <c>[ \t]+</c> for the reason <see cref="ShippedText.Unwrap"/> gives, and a pin is
    /// where that matters most: this one carries a message naming the exact phrase it wants, so a
    /// gap it cannot cross does not report a wrapping problem — it reports that the README stopped
    /// saying something the README is still saying, and sends the next reader after prose that was
    /// never edited.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"a[ \t]+Swiss[ \t]+association[ \t]+in[ \t]+formation", RegexOptions.CultureInvariant)]
    private static partial Regex AssociationInFormation();

    /// <summary>What this repository says happens to the profits.</summary>
    /// <remarks>
    /// Deliberately loose about the verb and the plural, about the gaps between the words, and
    /// anchored to no surrounding sentence: it has to keep matching when the paragraph is reworded,
    /// or the pin becomes a tax on editing prose rather than a guard on the commitment — and
    /// rewrapping a paragraph IS rewording it as far as this pattern can tell.
    /// </remarks>
    [GeneratedRegex(@"profits[ \t]+(?:are[ \t]+)?pledged[ \t]+to[ \t]+charit(?:y|ies)",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProfitsPledged();

    [Fact]
    public void The_readme_states_who_is_behind_rendlio_and_what_happens_to_the_profits()
    {
        // The README is the PackageReadmeFile, so this is what a consumer reads on the gallery page
        // when they ask who they are installing from. Both halves are commitments rather than
        // colour — the qualifier says the association does not exist yet, and the pledge is the
        // reason a pack this small is free — and both are stated in one short section that an edit
        // tightening the README would trim without anything going red.
        //
        // Unwrapped first: the sentence sits at the ~90-column wrap these pages are written to, so
        // rewrapping it is an ordinary edit that must not turn this red.
        string readme = ShippedText.Unwrap(File.ReadAllText(Path.Combine(RepositoryRoot, "README.md")));

        Assert.True(
            AssociationInFormation().IsMatch(readme),
            "README.md no longer says Rendlio is built by a Swiss association in formation.");
        Assert.True(
            ProfitsPledged().IsMatch(readme),
            "README.md no longer says the profits are pledged to charities.");
    }

    [Theory]
    // The shape the README actually ships.
    [InlineData("Rendlio is built by a Swiss association in formation, with profits pledged to charities.")]
    // A soft wrap between the words of either phrase. The pages are wrapped at ~90 columns and this
    // sentence sits at that width, so a rewrap lands here routinely.
    [InlineData("built by a Swiss association\nin formation, with profits pledged to\ncharities.")]
    // The same wrap written as a Markdown hard break — the case the pin above cannot test, because
    // it reads the real README and cannot rewrap it. Unwrap folds the newline and leaves the two
    // trailing spaces .editorconfig keeps, so the gap reaches the pattern as three spaces. Pinned
    // here because the failure it guards against is not a false green but a LYING red: the phrase
    // is still on the page, and a pattern that stopped matching it reports that it is gone.
    [InlineData("built by a Swiss association  \nin formation, with profits pledged to  \ncharities.")]
    public void The_identity_patterns_read_the_phrasing_however_the_paragraph_is_wrapped(string readme)
    {
        string unwrapped = ShippedText.Unwrap(readme);

        Assert.Matches(AssociationInFormation(), unwrapped);
        Assert.Matches(ProfitsPledged(), unwrapped);
    }

    [Theory]
    [InlineData("Description")]
    [InlineData("PackageTags")]
    public void The_package_metadata_meets_the_publishing_rules(string field)
    {
        // The walk enumerates *.md and this text is in none of them: it lives in the project file.
        // It is published text all the same — nuget.org renders the description under the package
        // title and the tags beside it, so it reaches the same reader the README does — and being
        // outside the walk means nothing else was ever going to read it either.
        //
        // Single() rather than SingleOrDefault(): a property renamed away has to fail loudly here
        // rather than leave this case inspecting nothing and reporting green, which is what every
        // guard-the-guard assertion in this file exists to prevent.
        string value = XDocument
            .Load(Path.Combine(RepositoryRoot, "src", "Rendlio.Analyzers", "Rendlio.Analyzers.csproj"))
            .Descendants(field).Single().Value.Trim();

        Assert.NotEmpty(value);
        Assert.Empty(ShippedText.Inspect($"Rendlio.Analyzers.csproj <{field}>", value));
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

    // ------------------------------------ the banned-API table, and the reasons its rows quote

    /// <summary>RENDLIO001's page, whose table restates the reason each row gives.</summary>
    private const string BannedApiPage = "docs/rules/RENDLIO001.md";

    /// <summary>
    /// The heading over that table. The page carries two, and the other one is the rule's metadata.
    /// </summary>
    private const string BannedApiTableHeading = "## What it reports";

    /// <summary>
    /// One use of every row of RENDLIO001's table, and nothing else the rule reports.
    /// </summary>
    /// <remarks>
    /// <para>Exactly one diagnostic per row is what makes the count below a guard rather than
    /// arithmetic. The three <c>Assembly</c> overloads share a row and are called once between
    /// them; members reached through a banned type stay silent on purpose, because the type
    /// reference carries their diagnostic. So a row added to the page with no use added here fails
    /// on the count before it can fail on a reason, and says which of the two went missing.</para>
    /// <para>The uses are written in the table's own order, and must stay that way. The comparison
    /// is positional — that is what makes a reason quoted against the wrong row a failure rather
    /// than a permutation nothing looks at — so this order is a contract, not a tidiness. A row
    /// moved on the page moves here in the same edit; getting it wrong fails loudly at that
    /// position rather than passing quietly, which is the safe direction for a contract held by a
    /// convention.</para>
    /// </remarks>
    private const string EveryRowOfTheTable = """
        using System.Runtime.InteropServices;

        namespace Example;

        internal static class Sut
        {
            internal static void Spawn(string name) => System.Diagnostics.Process.Start(name);

            internal static object Emit() => typeof(System.Reflection.Emit.DynamicMethod);

            internal static object Load(string name) => System.Reflection.Assembly.Load(name);

            internal static object FromStream(System.IO.Stream part) =>
                System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(part);

            internal static object? Activate(string name) => System.Activator.CreateInstance(name, name);

            internal static System.Type? Resolve(string name) => System.Type.GetType(name);

            internal static object Net() => typeof(System.Net.Http.HttpClient);

            [DllImport("native")]
            internal static extern int Version();
        }
        """;

    [Fact]
    public async Task The_banned_api_page_quotes_the_reason_each_row_actually_gives()
    {
        // The column is headed "Reason given in the message", which is a promise about the build
        // log the reader has open beside the page rather than a summary of the row. A cell that
        // paraphrases instead of quoting sends someone searching their log for a sentence the rule
        // never prints, and the row they are standing on is the one they conclude does not apply.
        // Nothing else catches it: the rule's own cases assert WHICH api each row names and never
        // what it says about it, so before this the whole column was unread.
        string[][] rows = RowsOfTheBannedApiTable();
        string[] reasons = await ReasonsRendlio001Gives(EveryRowOfTheTable);

        // Guards the guard: a heading rename or a reshaped table would leave reasons nobody
        // compared, and the walk below would pass over them green. The count is now redundant with
        // that walk — a fixture that stopped tripping a row leaves an unpaired position, which the
        // walk reports — but it is kept because it names the arithmetic directly. The walk would
        // instead report from wherever the two parted company onward, every row after it held
        // against its neighbour's reason, which describes a missing use far less plainly.
        Assert.NotEmpty(rows);
        Assert.Equal(rows.Length, reasons.Length);

        Assert.Empty(ReasonMismatches(rows, reasons));
    }

    [Fact]
    public async Task A_row_that_quotes_only_the_front_of_its_reason_does_not_count_as_quoting_it()
    {
        // The walk above used to compare by containment, and a cell holding only the front of a
        // reason satisfies that in both directions at once: the message contains the cell, and the
        // cell is contained in the message. So `zero network I/O` passed for a rule that prints
        // `zero network I/O; zero phone-home`, with the row count unmoved — and the reader who
        // greps their log for the cell as printed still finds nothing, which is the failure this
        // whole section exists to catch. Half a quotation is a paraphrase. This cuts each cell of
        // the real page down to its front half in turn and insists the walk says so every time.
        string[][] rows = RowsOfTheBannedApiTable();
        string[] reasons = await ReasonsRendlio001Gives(EveryRowOfTheTable);

        // Cutting a cell short only proves something if the page is intact first. These two also
        // earn FrontHalf its cell to cut: a row too short to carry a reason could not have matched.
        Assert.NotEmpty(rows);
        Assert.Empty(ReasonMismatches(rows, reasons));

        foreach (int row in Enumerable.Range(0, rows.Length))
        {
            string[][] truncated = [.. rows.Select((cells, index) => index == row ? FrontHalf(cells) : cells)];

            Assert.NotEmpty(ReasonMismatches(truncated, reasons));
        }
    }

    [Fact]
    public async Task A_reason_quoted_against_another_row_does_not_count_as_this_row_quoting_it()
    {
        // Comparing the two as SETS certifies a permutation. Exchange the reasons on the `Process`
        // and `System.Net.*` rows and every cell is still word for word a reason the rule gives,
        // every reason is still quoted by some cell, and the multiset is unchanged — so both walks
        // and the row count stayed green while the page told a reader that spawning a process is
        // banned for zero phone-home. Exactness cannot see this: each cell is a perfect quotation
        // of the wrong row. Only position can, which is why the walk pairs the page's Nth row with
        // the rule's Nth diagnostic instead of asking whether each appears somewhere in the other.
        // This exchanges every pair of rows a swap would actually move and insists it is reported.
        string[][] rows = RowsOfTheBannedApiTable();
        string[] reasons = await ReasonsRendlio001Gives(EveryRowOfTheTable);

        // As above: cutting or moving a cell proves something only if the page is intact first —
        // and this is also what earns the swap its cell, since a row too short to carry a reason
        // could not have matched one here.
        Assert.NotEmpty(rows);
        Assert.Empty(ReasonMismatches(rows, reasons));

        List<(int Left, int Right)> pairs = RowPairsQuotingDifferentReasons(rows);

        // Guards the guard: rows that all quoted one reason would make every exchange a no-op and
        // leave the loop below asserting nothing at all.
        Assert.NotEmpty(pairs);

        foreach ((int left, int right) in pairs)
        {
            Assert.NotEmpty(ReasonMismatches(WithReasonsExchanged(rows, left, right), reasons));
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task The_reason_column_reads_the_same_under_any_culture(string culture)
    {
        // The rule-side culture cases pin the verdict and the message; this pins the page-side walk
        // that holds one against the other. A contributor edits the page on their own machine under
        // their own locale, and this guard is what tells them the edit is wrong. Every comparison it
        // makes is ordinal or invariant today — the cell/reason equality, the split on the message
        // format's two arguments, the `|` and `#` line tests the extractor runs — so what is pinned
        // is the invariance itself rather than any one of those flags. The hazard is a later tidy-up
        // into a culture-aware or case-insensitive form: the guard would then hold the page to the
        // rule on some machines and not others, and CI, which runs neither culture, would agree with
        // whichever it happened to be. Both directions are asserted, because a walk gone
        // culture-sensitive can fail either way round — reporting a clean page, or passing a bad one.
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            var target = new CultureInfo(culture);
            CultureInfo.CurrentCulture = target;
            CultureInfo.CurrentUICulture = target;

            string[] reasons = await ReasonsRendlio001Gives(EveryRowOfTheTable);

            // Probed after the await rather than before it, so what is checked is the culture in
            // force where the page is read and compared, not merely where the test started.
            ShouldBeUnderARealCulture(culture);

            string[][] rows = RowsOfTheBannedApiTable();

            Assert.NotEmpty(rows);
            Assert.Empty(ReasonMismatches(rows, reasons));
            Assert.NotEmpty(ReasonMismatches([FrontHalf(rows[0]), .. rows[1..]], reasons));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    /// <summary>
    /// That the ambient culture is the one asked for, and that it carries its own data. The name
    /// alone would not do: under <c>InvariantGlobalization</c> a fabricated culture still reports
    /// its name while carrying invariant data, so the name check passes and every assertion after it
    /// is vacuous. Formatting a number is the cheapest probe that reads the real data — both
    /// cultures here write the separator as a comma, where the invariant one writes a point.
    /// </summary>
    private static void ShouldBeUnderARealCulture(string expected)
    {
        Assert.Equal(expected, CultureInfo.CurrentCulture.Name);
        Assert.Equal("1,5", 1.5.ToString(CultureInfo.CurrentCulture));
    }

    /// <summary>The rows of RENDLIO001's table, as the page on disk carries them.</summary>
    private static string[][] RowsOfTheBannedApiTable() =>
    [
        .. TableRowsUnder(
            File.ReadAllText(Path.Combine(RepositoryRoot, BannedApiPage)),
            BannedApiTableHeading),
    ];

    /// <summary>The api a table row names, for naming the row in a message.</summary>
    private static string Banned(string[] cells) => cells.Length > 0 ? cells[0] : "<an empty row>";

    /// <summary>
    /// The reason a table row quotes, stripped of the page's own emphasis. A row too short to carry
    /// one reads as a mismatch rather than an exception, the way a reshaped index row does above.
    /// </summary>
    private static string Reason(string[] cells) =>
        cells.Length > 1 ? cells[1].Trim('`') : "<no such column>";

    /// <summary>That row with its reason cut in half — the shape of a truncated quotation.</summary>
    private static string[] FrontHalf(string[] cells)
    {
        string reason = Reason(cells);
        string[] truncated = [.. cells];
        truncated[1] = reason[..(reason.Length / 2)];

        return truncated;
    }

    /// <summary>
    /// Every pair of rows an exchange would actually move. Three rows quote <c>no dynamic code</c>
    /// and two quote the type-name reason, and swapping two of those leaves the page identical —
    /// so pairing them would assert that an edit which changed nothing is caught, which no walk
    /// can do and none should.
    /// </summary>
    private static List<(int Left, int Right)> RowPairsQuotingDifferentReasons(string[][] rows)
    {
        List<(int Left, int Right)> pairs = [];

        foreach (int left in Enumerable.Range(0, rows.Length))
        {
            foreach (int right in Enumerable.Range(left + 1, rows.Length - left - 1))
            {
                if (!string.Equals(Reason(rows[left]), Reason(rows[right]), StringComparison.Ordinal))
                {
                    pairs.Add((left, right));
                }
            }
        }

        return pairs;
    }

    /// <summary>
    /// The table with two rows' reasons exchanged — the shape of a reason quoted against the wrong
    /// api. The rows are copied before the swap so the caller keeps the page as the disk holds it
    /// and can go on exchanging further pairs against it.
    /// </summary>
    private static string[][] WithReasonsExchanged(string[][] rows, int left, int right)
    {
        string[][] exchanged = [.. rows.Select(static cells => cells.ToArray())];

        (exchanged[left][1], exchanged[right][1]) = (exchanged[right][1], exchanged[left][1]);

        return exchanged;
    }

    /// <summary>
    /// Everything the page's reason column and the reasons the rule gives disagree about, pairing
    /// the table's Nth row with the rule's Nth diagnostic so that a cell nothing prints, a reason
    /// no cell quotes, and a reason quoted against the wrong row are all one kind of failure.
    /// </summary>
    /// <remarks>
    /// <para>Two things are load-bearing. The comparison is <em>exact</em>: containment reads a cell
    /// holding only the front of a reason as a match BOTH ways round and leaves the row count
    /// untouched, so it certifies precisely the half-quotation this column must not carry. And it
    /// is <em>positional</em>: comparing the two as sets certifies a permutation, where every cell
    /// quotes some reason perfectly and no cell quotes its own. Exactness cannot see that one and
    /// position cannot see the other, so the column needs both. Pinned by
    /// <see cref="A_row_that_quotes_only_the_front_of_its_reason_does_not_count_as_quoting_it"/> and
    /// <see cref="A_reason_quoted_against_another_row_does_not_count_as_this_row_quoting_it"/>.</para>
    /// <para>Position means the table's order and the fixture's order are one contract; see the
    /// remark on <see cref="EveryRowOfTheTable"/>. Walking to the longer of the two rather than to
    /// the shorter keeps a page and a fixture that have drifted apart in length reportable here
    /// instead of silently comparing only the overlap.</para>
    /// </remarks>
    private static List<string> ReasonMismatches(string[][] rows, string[] reasons)
    {
        List<string> wrong = [];

        foreach (int at in Enumerable.Range(0, Math.Max(rows.Length, reasons.Length)))
        {
            bool onThePage = at < rows.Length;
            bool fromTheRule = at < reasons.Length;

            if (onThePage && fromTheRule
                && string.Equals(Reason(rows[at]), reasons[at], StringComparison.Ordinal))
            {
                continue;
            }

            wrong.Add((onThePage, fromTheRule) switch
            {
                (true, true) =>
                    $"{BannedApiPage} row {at + 1}, for {Banned(rows[at])}, gives its reason as "
                    + $"'{Reason(rows[at])}', but what the rule says there is '{reasons[at]}'.",
                (true, false) =>
                    $"{BannedApiPage} row {at + 1}, for {Banned(rows[at])}, gives its reason as "
                    + $"'{Reason(rows[at])}', but the rule reports nothing at that row.",
                _ => $"RENDLIO001 says '{reasons[at]}', but {BannedApiPage} has no row {at + 1}.",
            });
        }

        return wrong;
    }

    /// <summary>The reason half of every RENDLIO001 diagnostic <paramref name="source"/> trips.</summary>
    /// <remarks>
    /// <para>The reason is argument 1 of the rule's message format, so it is whatever follows the
    /// text that format puts between its two arguments. That text is read off the descriptor the
    /// rule reports with rather than spelled out here, so rewording the message carries this with
    /// it instead of leaving it splitting on punctuation the rule no longer prints. RENDLIO002
    /// wraps its reason into the sentence rather than appending it, which is why the split is keyed
    /// to one descriptor — and why the single-descriptor assertion below is not decoration.</para>
    /// <para>Ordered by where in the source each one landed, because the caller compares by
    /// position and the order a compilation hands its diagnostics back in is not part of any
    /// contract. Sorting here rather than trusting that order is what lets the fixture's own
    /// layout — one use per row, in the table's order — decide which reason is held against which
    /// row.</para>
    /// </remarks>
    private static async Task<string[]> ReasonsRendlio001Gives(string source)
    {
        var analyzer = new BannedApiAnalyzer();
        string separator = BetweenTheArgumentsOf(Assert.Single(analyzer.SupportedDiagnostics));

        ImmutableArray<Diagnostic> reported = await AnalyzerHarness.RunAsync(analyzer, "Consumer", source);

        return
        [
            .. reported
                .OrderBy(static d => d.Location.SourceSpan.Start)
                .Select(d => ReasonIn(d.GetMessage(CultureInfo.InvariantCulture), separator)),
        ];
    }

    /// <summary>
    /// The text a message format puts between <c>{0}</c> and <c>{1}</c>. A format not carrying both
    /// in that order yields something no message contains, which leaves every reason unsplit and
    /// every row mismatched — loudly, where returning the empty string would split every message at
    /// its first character and quietly compare nothing against nothing.
    /// </summary>
    private static string BetweenTheArgumentsOf(DiagnosticDescriptor descriptor)
    {
        const string FirstArgument = "{0}";
        const string SecondArgument = "{1}";

        string format = descriptor.MessageFormat.ToString(CultureInfo.InvariantCulture);
        int from = format.IndexOf(FirstArgument, StringComparison.Ordinal);
        int to = format.IndexOf(SecondArgument, StringComparison.Ordinal);

        return from >= 0 && to > from + FirstArgument.Length
            ? format[(from + FirstArgument.Length)..to]
            : "<not a two-argument format>";
    }

    /// <summary>
    /// What follows the first <paramref name="separator"/> in <paramref name="message"/>, or the
    /// whole of a message carrying none.
    /// </summary>
    private static string ReasonIn(string message, string separator)
    {
        int at = message.IndexOf(separator, StringComparison.Ordinal);

        return at < 0 ? message : message[(at + separator.Length)..];
    }

    /// <summary>
    /// The data rows of the first Markdown table under <paramref name="heading"/>, each as its
    /// cells with the surrounding whitespace gone.
    /// </summary>
    /// <remarks>
    /// The row naming the columns and the <c>---</c> rule beneath it are punctuation rather than
    /// data and are dropped, as are the empty leading and trailing fields a pipe-delimited row
    /// splits into. Returns nothing when the section carries no table, which the caller's own
    /// guard turns into a failure — an extractor that quietly returned nothing would otherwise let
    /// every rule read over the table report green.
    /// </remarks>
    private static IReadOnlyList<string[]> TableRowsUnder(string markdown, string heading)
    {
        List<string[]> rows = [];
        bool inSection = false;

        foreach (string line in markdown.Split('\n'))
        {
            // Trimmed rather than read as written, so a page with CRLF endings reads the same as
            // one without: the carriage return is the last character of every line there.
            string trimmed = line.Trim();

            if (!inSection)
            {
                inSection = string.Equals(trimmed, heading, StringComparison.Ordinal);
                continue;
            }

            if (trimmed.StartsWith('|'))
            {
                rows.Add([.. trimmed.Split('|')[1..^1].Select(static cell => cell.Trim())]);
                continue;
            }

            // Once the table has started, the first line that is not a row has ended it; before it,
            // this is the prose between the heading and the table. A new heading ends the section
            // either way, so a section carrying no table cannot borrow the next one's.
            if (rows.Count > 0 || trimmed.StartsWith('#'))
            {
                break;
            }
        }

        return [.. rows.Skip(1).Where(static cells => !IsRule(cells))];
    }

    /// <summary>Whether every cell of a row is the <c>---</c> the table is drawn from.</summary>
    private static bool IsRule(string[] cells) =>
        cells.Length > 0
        && cells.All(static cell => cell.Length > 0 && cell.All(static character => character is '-' or ':'));

    [Fact]
    public void A_table_is_read_as_its_data_rows_and_not_as_its_header()
    {
        // The extractor decides how many reasons get compared, so one that kept the header row or
        // the rule under it would hold a diagnostic against punctuation and fail for a reason that
        // has nothing to do with the page.
        IReadOnlyList<string[]> rows = TableRowsUnder(
            "## What it reports\n\nProse first.\n\n| Banned | Reason |\n| --- | :--- |\n"
            + "| `A` | first |\n| `B` | second |\n\nProse after.\n",
            "## What it reports");

        Assert.Equal(2, rows.Count);
        Assert.Equal("`A`", rows[0][0]);
        Assert.Equal("first", rows[0][1]);
        Assert.Equal("`B`", rows[1][0]);
        Assert.Equal("second", rows[1][1]);
    }

    [Fact]
    public void A_table_belonging_to_another_section_is_not_read_as_this_one()
    {
        // The page opens with a metadata table and this one sits further down, so an extractor that
        // took the first table it found, or ran on past an empty section into the next one, would
        // compare a category and a severity against a diagnostic message.
        Assert.Empty(TableRowsUnder(
            "| | |\n| --- | --- |\n| **Category** | `Rendlio.Security` |\n\n## What it reports\n\n"
            + "Nothing yet.\n\n## Something else\n\n| Banned | Reason |\n| --- | --- |\n| `A` | first |\n",
            "## What it reports"));
    }

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
    /// A <c>[SuppressMessage]</c>, capturing the category it names — its first argument.
    /// </summary>
    /// <remarks>
    /// Matched on the attribute name alone, so the assembly-scoped spelling the pages publish for a
    /// <c>GlobalSuppressions.cs</c> is read the same way as the one that sits on a declaration.
    /// <para>
    /// Held to a shipped category even though nothing at build time is: the attribute matches on
    /// its id and ignores this argument, which
    /// <see cref="SuppressionTests.An_attribute_matches_on_the_id_and_not_on_the_category_beside_it"/>
    /// pins. That is the reason to check it here rather than a reason not to — a wrong category
    /// costs a reader nothing at build time and would sit on the page uncorrected forever.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"SuppressMessage\(\s*""([^""]+)""\s*,\s*""[^""]+""", RegexOptions.CultureInvariant)]
    private static partial Regex SuppressedCategory();

    /// <summary>
    /// The same attribute, capturing the rule id it names — its second argument.
    /// </summary>
    /// <remarks>
    /// Stops at a colon as well as at the quote, because the argument accepts an
    /// <c>ID:Title</c> spelling and it is the part before the colon that has to name a shipped rule.
    /// </remarks>
    [GeneratedRegex(@"SuppressMessage\(\s*""[^""]+""\s*,\s*""([^"":]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SuppressedRule();

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
                         (SuppressedRule(), shippedRules, "rule"),
                         (ConfiguredCategory(), shippedCategories, "category"),
                         (SuppressedCategory(), shippedCategories, "category"),
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
