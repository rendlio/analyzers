namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds every workflow this repository runs to <see cref="WorkflowPins"/>, and holds
/// <see cref="WorkflowPins"/> itself to fixtures that break each rule on purpose.
/// </summary>
public sealed class WorkflowPinsTests
{
    /// <summary>
    /// The file whose name the publishing policy pins, so it is the workflow that must exist for
    /// the sweep below to be checking the thing this rule is for.
    /// </summary>
    private const string PublishingWorkflow = "release.yml";

    [Fact]
    public void Every_action_the_workflows_run_is_pinned_to_a_commit()
    {
        List<string> violations = [];

        foreach (string workflow in WorkflowFiles())
        {
            violations.AddRange(
                WorkflowPins.Inspect(
                    Path.GetRelativePath(RepositoryLayout.Root, workflow).Replace('\\', '/'),
                    File.ReadAllText(workflow)));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void The_workflows_that_actually_run_are_the_ones_that_get_checked()
    {
        // Guards the guard, twice over. A rule enforced by walking a directory reports green the
        // moment the walk stops finding anything — a renamed directory, a workflow written as
        // .yaml, a pattern that no longer matches a `uses:` line. Each of those leaves the check
        // passing over an empty set, which is indistinguishable from a repository that complies.
        List<string> workflows = WorkflowFiles();

        Assert.NotEmpty(workflows);
        Assert.Contains(workflows, path => Path.GetFileName(path) == PublishingWorkflow);

        // Every workflow runs at least one action, so a file the reader finds no references in
        // means the reader stopped reading, not that the file got simpler.
        Assert.All(workflows, path => Assert.NotEmpty(WorkflowPins.Read(File.ReadAllText(path))));
    }

    [Theory]
    // The moving major tag, which is the shape these workflows used before the pins went in.
    [InlineData("      - uses: actions/checkout@v4", "v4")]
    // A full version tag is still a tag: nothing stops a published version being repointed, and
    // "it looks specific" is the reason that goes unnoticed when it happens.
    [InlineData("      - uses: actions/checkout@v4.4.0", "v4.4.0")]
    // A branch is the most mobile reference there is.
    [InlineData("      - uses: actions/checkout@main", "main")]
    // An abbreviated SHA is not a pin — GitHub will not resolve one.
    [InlineData("      - uses: actions/checkout@11d5960", "11d5960")]
    // Thirty-nine hex characters. Close enough to read as a pin at a glance, which is exactly why
    // the length is checked rather than the alphabet.
    [InlineData("      - uses: actions/checkout@11d5960a326750d5838078e36cf38b85af67726", "11d5960a326750d5838078e36cf38b85af67726")]
    // A reusable workflow is called at job level: no list dash, no `steps:` above it, and a path
    // carrying its own dots and slashes. It is still someone else's code at a ref that can move,
    // and it runs with whatever permissions the calling job hands it — so it is worth catching and
    // is the shape least likely to look like a step.
    [InlineData("    uses: owner/repo/.github/workflows/build.yml@v1", "v1")]
    public void A_reference_that_is_not_a_commit_is_reported(string line, string expectedRef)
    {
        Assert.Equal(expectedRef, Assert.Single(WorkflowPins.Read(line)).Ref);
        Assert.Single(WorkflowPins.Inspect("ci.yml", line));
    }

    [Fact]
    public void A_commit_with_no_version_after_it_is_reported()
    {
        // The half of the rule that is about the reader rather than the attacker. This reference is
        // safe; it is just opaque, and an opaque pin is the one someone later replaces with a tag
        // to find out what it was.
        string violation = Assert.Single(
            WorkflowPins.Inspect(
                "ci.yml",
                "      - uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262"));

        Assert.Contains("no version after it", violation, StringComparison.Ordinal);
    }

    [Theory]
    // A trailing comment naming the exact release the SHA was.
    [InlineData("      - uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4.4.0", "v4.4.0")]
    // A step written with `uses:` on its own line rather than after the list dash.
    [InlineData("        uses: NuGet/login@8d196754b4036150537f80ac539e15c2f1028841 # v1.2.0", "v1.2.0")]
    // Whatever else the comment says, the version in it is what is read.
    [InlineData("      - uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4.4.0, resolved 2026-08", "v4.4.0")]
    // The pinned form of that reusable-workflow call. The path in front of the ref carries both
    // dots and slashes, so this holds the split to the LAST '@' rather than to the first character
    // that could be mistaken for one.
    [InlineData("    uses: owner/repo/.github/workflows/build.yml@11d5960a326750d5838078e36cf38b85af677262 # v1.2.0", "v1.2.0")]
    public void A_commit_with_its_version_after_it_is_clean(string line, string expectedVersion)
    {
        WorkflowPins.ActionReference reference = Assert.Single(WorkflowPins.Read(line));

        Assert.True(reference.IsPinned);
        Assert.Equal(expectedVersion, reference.StatedVersion);
        Assert.Empty(WorkflowPins.Inspect("ci.yml", line));
    }

    [Theory]
    // A commented-out step is not a step.
    [InlineData("      # - uses: actions/checkout@v4")]
    // Prose in a workflow header that mentions a reference while explaining this very rule. These
    // files carry paragraphs of it, so a pattern that matched anywhere on the line would fail the
    // build over a sentence.
    [InlineData("# Written as a tag — actions/checkout@v4 — the ref is a pointer its owner can move.")]
    // An action kept in this repository: its code is in the commit that uses it, so there is no
    // third party's tag to move and no separate history to pin it to.
    [InlineData("      - uses: ./.github/actions/setup")]
    // A container image pins by digest, not by commit. Nothing here runs one; the point is that it
    // is not reported as an unpinned tag, which would be the wrong sentence about the wrong rule.
    [InlineData("      - uses: docker://alpine:3.20")]
    // A `run:` line that happens to contain the word.
    [InlineData("        run: echo \"this step uses: nothing\"")]
    public void A_line_that_names_no_pinnable_action_is_left_alone(string line)
    {
        Assert.Empty(WorkflowPins.Read(line));
        Assert.Empty(WorkflowPins.Inspect("ci.yml", line));
    }

    [Fact]
    public void The_reader_finds_every_step_in_a_file_rather_than_the_first()
    {
        // The sweep above is a fold over whatever the reader returns, so a reader that stopped at
        // the first match would report green on a file whose second step is unpinned.
        const string workflow = """
            jobs:
              build:
                steps:
                  - uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4.4.0
                  - name: Restore
                    run: dotnet restore
                  - uses: actions/setup-dotnet@v4
            """;

        Assert.Equal(2, WorkflowPins.Read(workflow).Count);
        Assert.Single(WorkflowPins.Inspect("ci.yml", workflow));
    }

    [Fact]
    public void A_workflow_with_CRLF_endings_is_read_the_same_as_one_without()
    {
        // .gitattributes holds this repository to LF, so nothing exercises the other ending today
        // — which is the reason to pin it rather than the reason not to. The reader splits on '\n'
        // and strips the carriage return itself; an implementation that anchored a multiline
        // pattern instead would leave it inside the ref, where forty hex characters plus one stop
        // being forty hex characters. Both halves of the rule then invert at once: the tag below
        // goes unreported, and the clean pin beside it is reported as a violation.
        //
        // Built by converting a raw literal rather than by spelling the escapes out, so the test
        // states CRLF regardless of how this source file itself was checked out.
        string workflow = """
            jobs:
              build:
                steps:
                  - uses: actions/checkout@v4
                  - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1
            """.ReplaceLineEndings("\r\n");

        IReadOnlyList<WorkflowPins.ActionReference> references = WorkflowPins.Read(workflow);

        Assert.Equal(2, references.Count);
        Assert.Equal("v4", references[0].Ref);

        // The pin survives intact: forty characters still, and a trailing comment still read for a
        // version rather than swallowed along with the carriage return.
        Assert.True(references[1].IsPinned);
        Assert.Equal("v4.3.1", references[1].StatedVersion);

        // One violation — the tag — and not the pin next to it.
        Assert.Single(WorkflowPins.Inspect("ci.yml", workflow));
    }

    /// <summary>Every workflow file in this repository, in a stable order.</summary>
    /// <remarks>
    /// Both extensions, because GitHub accepts both and a rule that only knew about one would be
    /// silently skippable by naming a file the other way.
    /// </remarks>
    private static List<string> WorkflowFiles()
    {
        string directory = Path.Combine(RepositoryLayout.Root, ".github", "workflows");

        return Directory.EnumerateFiles(directory, "*.yml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
    }
}
