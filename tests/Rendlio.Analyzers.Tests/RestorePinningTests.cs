namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds this repository's restore configuration to <see cref="RestorePinning"/>, holds
/// <see cref="RestorePinning"/> itself to fixtures that break each rule on purpose, and holds every
/// project to carrying the lock file a locked-mode restore has nothing to compare against without.
/// </summary>
/// <remarks>
/// Every fixture below is a complete, otherwise-pinned config with exactly one rule broken, so the
/// assertion can be that the reader reports precisely one thing. A fixture that omitted the halves
/// it was not about would draw collateral violations, and a check that tolerated those could not
/// tell a reader that names one cause from a reader that names everything.
/// </remarks>
public sealed class RestorePinningTests
{
    /// <summary>The lock file NuGet writes beside a project when it records a resolved graph.</summary>
    private const string LockFile = "packages.lock.json";

    /// <summary>A config with both sections cleared and mapped: the shape everything else deviates from.</summary>
    private const string Pinned = """
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """;

    /// <summary>Directory segments that hold build output rather than source.</summary>
    private static readonly string[] _buildOutput = ["bin", "obj", "artifacts"];

    [Fact]
    public void The_restore_configuration_pins_both_halves_of_source_resolution()
    {
        string path = Path.Combine(RepositoryLayout.Root, "NuGet.config");

        Assert.True(
            File.Exists(path),
            $"{path} does not exist, so restore inherits every feed and mapping rule the machine defines.");
        Assert.Empty(RestorePinning.Inspect("NuGet.config", File.ReadAllText(path)));
    }

    [Fact]
    public void The_configuration_that_gets_checked_is_the_one_restore_reads()
    {
        // Guards the guard. The check above reports "no violations found", which is also what a
        // reader pointed at a file that says nothing returns. The real file has to name the feed
        // for that pass to have inspected a pin rather than an absence.
        string text = File.ReadAllText(Path.Combine(RepositoryLayout.Root, "NuGet.config"));

        Assert.Contains(RestorePinning.PublicFeed, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fully_pinned_configuration_is_clean()
    {
        Assert.Empty(RestorePinning.Inspect("NuGet.config", Pinned));
    }

    [Theory]
    // Cleared, but after the source it was meant to replace — which discards the entry above it
    // and leaves the machine's feeds standing in its place.
    [InlineData(
        """
        <configuration>
          <packageSources>
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
            <clear />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "does not open with <clear />")]
    // A second feed. Not necessarily hostile — a mirror, a corporate proxy, a preview feed — and
    // still a second place a package of this name could come from.
    [InlineData(
        """
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
            <add key="internal" value="https://packages.example.invalid/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "packages.example.invalid")]
    // The finding this file was written for: sources pinned, mapping left inherited. Green on the
    // machine that wrote it, NU1101 on the one whose config routes a pattern somewhere else.
    [InlineData(
        """
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
        </configuration>
        """,
        "declares no <packageSourceMapping>")]
    // The mapping section present but not cleared, so the machine's rules apply alongside it.
    [InlineData(
        """
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "does not open with <clear />")]
    // A mapping onto a source this file does not declare: the NU1101 trap, written down here
    // rather than inherited from the machine.
    [InlineData(
        """
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="mirror">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "which <packageSources> does not declare")]
    // Cleared and mapped, but only for the ids someone thought to name. Every other id falls back
    // to the rules the clear existed to discard.
    [InlineData(
        """
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="nuget.org">
              <package pattern="Microsoft.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "maps no '*' pattern")]
    public void A_configuration_that_leaves_resolution_open_is_reported(string config, string expected)
    {
        string violation = Assert.Single(RestorePinning.Inspect("NuGet.config", config));

        Assert.Contains(expected, violation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configuration_that_declares_no_sources_is_reported_for_both_sections()
    {
        // The one deviation that cannot break a single rule: with no <packageSources> there is no
        // declared key for the mapping section to name, so the mapping is reported as well. Both
        // sentences are true and each names its own cause, which is what is worth pinning — a
        // reader that collapsed them would tell the next contributor half the story.
        IReadOnlyList<string> violations = RestorePinning.Inspect(
            "NuGet.config",
            """
            <configuration>
              <packageSourceMapping>
                <clear />
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.Contains("declares no <packageSources>", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("which <packageSources> does not declare", StringComparison.Ordinal));
    }

    [Fact]
    public void A_source_key_that_differs_only_in_case_still_names_the_declared_source()
    {
        // NuGet matches a source key case-insensitively, so a mapping onto "NuGet.org" resolves to
        // the source declared as "nuget.org". Reporting it would be a rule about spelling wearing
        // the words of a rule about resolution.
        string mixedCase = Pinned.Replace(
            "<packageSource key=\"nuget.org\">",
            "<packageSource key=\"NuGet.org\">",
            StringComparison.Ordinal);

        Assert.NotEqual(Pinned, mixedCase);
        Assert.Empty(RestorePinning.Inspect("NuGet.config", mixedCase));
    }

    [Fact]
    public void Every_project_carries_the_lock_file_a_locked_restore_reads()
    {
        // The hole in a locked-mode restore: it compares the resolved graph against a lock file
        // only when it finds one. A restore that finds none writes it and exits 0 — so deleting a
        // packages.lock.json, or adding a project that never had one, switches the drift gate off
        // and leaves CI green. Nothing else in the repository notices, which is why this is here.
        var missing = Projects()
            .Where(project => !File.Exists(
                Path.Combine(Path.GetDirectoryName(project) ?? RepositoryLayout.Root, LockFile)))
            .Select(project => Path.GetRelativePath(RepositoryLayout.Root, project).Replace('\\', '/'))
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void The_projects_that_get_checked_are_the_ones_the_solution_builds()
    {
        // Guards the guard, the way the workflow sweep does: a rule enforced by walking a
        // directory reports green the moment the walk stops finding anything, and an empty walk is
        // indistinguishable from a repository that complies.
        List<string> projects = Projects();

        Assert.Equal(2, projects.Count);
        Assert.Contains(projects, path => Path.GetFileName(path) == "Rendlio.Analyzers.csproj");
        Assert.Contains(projects, path => Path.GetFileName(path) == "Rendlio.Analyzers.Tests.csproj");
    }

    /// <summary>Every project in this repository, in a stable order.</summary>
    /// <remarks>
    /// Walked rather than read out of the solution file: a project that builds is not the only
    /// project that restores, and one added to the tree but not yet to the solution is exactly the
    /// case where a lock file is most likely to be missing.
    /// </remarks>
    private static List<string> Projects() =>
        Directory.EnumerateFiles(RepositoryLayout.Root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(RepositoryLayout.Root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => _buildOutput.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
}
