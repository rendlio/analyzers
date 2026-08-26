namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds every page this repository publishes to <see cref="ShippedText"/>, and holds
/// <see cref="ShippedText"/> itself to fixtures that break each rule on purpose.
/// </summary>
public sealed class ShippedTextTests
{
    private const string SolutionFile = "Rendlio.Analyzers.slnx";

    /// <summary>
    /// Directory segments that are not published: the private notes tree, build output, and the
    /// repository's own plumbing. Anything else carrying a page is fair game for the rules.
    /// </summary>
    private static readonly string[] _unpublishedDirectories = ["docs/internal", ".git", ".conductor", "bin", "obj", "artifacts"];

    /// <summary>
    /// The repository root, walked up from the test binary.
    /// </summary>
    /// <remarks>
    /// Found by walking rather than taken from a compile-time <c>[CallerFilePath]</c>, because CI
    /// builds with <c>ContinuousIntegrationBuild</c> — which normalises embedded source paths to a
    /// form that does not exist on any disk. A compile-time path would resolve to nothing precisely
    /// in the run where these rules matter most.
    /// </remarks>
    private static string RepositoryRoot { get; } = FindRepositoryRoot();

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

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not find {SolutionFile} in any directory above {AppContext.BaseDirectory}.");
    }
}
