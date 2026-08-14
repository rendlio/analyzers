namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds the committed local-consume configuration to <see cref="LocalConsumeIsolation"/>, holds
/// that reader to fixtures which break each rule on purpose, and holds the file itself to staying
/// out of the way of this repository's own restore.
/// </summary>
/// <remarks>
/// Every fixture below is a complete, otherwise-sealed configuration with exactly one rule broken,
/// so the assertion can be that the reader reports precisely one thing. A fixture that omitted the
/// parts it was not about would draw collateral violations, and a check tolerating those could not
/// tell a reader that names one cause from a reader that names everything.
/// </remarks>
public sealed class LocalConsumeIsolationTests
{
    /// <summary>Directory segments that hold build output rather than source.</summary>
    private static readonly string[] _buildOutput = ["bin", "obj", "artifacts"];

    /// <summary>The shape everything else deviates from: the committed file, without its comment.</summary>
    private const string Isolated = """
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="packages" />
          </config>
          <fallbackPackageFolders>
            <clear />
          </fallbackPackageFolders>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="local">
              <package pattern="Rendlio.*" />
            </packageSource>
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """;

    [Fact]
    public void The_committed_configuration_is_sealed_against_a_stale_copy() =>
        Assert.Empty(LocalConsumeIsolation.Inspect(
            LocalConsumeIsolation.ConfigPath, File.ReadAllText(ConfigFullPath)));

    [Fact]
    public void The_configuration_that_gets_checked_is_the_one_the_check_copies()
    {
        // Guards the guard. The check above reports "no violations found", which is also what a
        // reader pointed at a file that says nothing returns — and a missing file throws out of it
        // rather than passing, which is the right noise, but a hollowed-out one would not. The two
        // strings named here are the ones the whole check hangs on, so their presence is what makes
        // that pass mean a seal was inspected rather than an absence.
        Assert.True(
            File.Exists(ConfigFullPath),
            $"{LocalConsumeIsolation.ConfigPath} does not exist, so the check has no configuration to copy and every consume run inherits the machine's.");

        string text = File.ReadAllText(ConfigFullPath);

        Assert.Contains("globalPackagesFolder", text, StringComparison.Ordinal);
        Assert.Contains(LocalConsumeIsolation.PackagePattern, text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_project_in_this_repository_restores_through_it()
    {
        // The file is a NuGet configuration with a cleared source list and a feed of its own, sitting
        // in the repository it tests. NuGet finds a configuration by walking up from the project
        // being restored, so one placed on that path would merge into every build here — silently
        // widening the sources this repository pins, in a section the rule that pins them does not
        // read, because that rule inspects the root file by name. Somewhere off the walk is what
        // makes the file inert to the build and live only where it is copied.
        Assert.Equal(["NuGet.config"], ConfigurationsOnProjectWalkUpPaths());
    }

    [Fact]
    public void The_walk_that_finds_them_reaches_the_projects_it_is_about()
    {
        // Guards the guard above, which reports what a walk found and would report a tidy result
        // for having walked nothing. Presence rather than a count: this repository is expected to
        // gain projects, and a guard reading "expected 2, actual 3" blames the walk for someone
        // else's addition.
        List<string> projects = Projects();

        Assert.Contains(projects, path => Path.GetFileName(path) == "Rendlio.Analyzers.csproj");
        Assert.Contains(projects, path => Path.GetFileName(path) == "Rendlio.Analyzers.Tests.csproj");
    }

    [Fact]
    public void A_fully_isolated_configuration_is_clean() =>
        Assert.Empty(LocalConsumeIsolation.Inspect(LocalConsumeIsolation.ConfigPath, Isolated));

    [Theory]
    // The finding this file was written for: every source and mapping pinned, and the packages
    // folder left to the machine. Restore succeeds, the consumer builds, and the pack just written
    // to the feed is never opened because an extraction of the same id and version is already there.
    [InlineData(
        """
        <configuration>
          <fallbackPackageFolders>
            <clear />
          </fallbackPackageFolders>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="local">
              <package pattern="Rendlio.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "sets no globalPackagesFolder")]
    // The same line written as somebody's own path. It isolates the run that wrote it and nothing
    // else: copied to another machine it names a folder that either does not exist or is shared
    // with every other check that folder was ever used for.
    [InlineData(
        """
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="/scratch/packages" />
          </config>
          <fallbackPackageFolders>
            <clear />
          </fallbackPackageFolders>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="local">
              <package pattern="Rendlio.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "absolute path")]
    // The same trap through the door nobody configures by hand. A Visual Studio install puts a
    // fallback folder on the machine, restore may satisfy an id and version out of it without
    // downloading, and an isolated globalPackagesFolder does not cover it.
    [InlineData(
        """
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="packages" />
          </config>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="local">
              <package pattern="Rendlio.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "declares no <fallbackPackageFolders>")]
    // Cleared, but after the entry it was meant to replace — which discards that entry and leaves
    // the machine's folders standing. It reads like isolation and is the absence of it.
    [InlineData(
        """
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="packages" />
          </config>
          <fallbackPackageFolders>
            <add key="shared" value="shared-packages" />
            <clear />
          </fallbackPackageFolders>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="local">
              <package pattern="Rendlio.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "<fallbackPackageFolders> does not open with <clear />")]
    // The scratch feed missing from the sources while the mapping still names it: NU1101 for a
    // package that is sitting in the feed, with the error naming the package.
    [InlineData(
        """
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="packages" />
          </config>
          <fallbackPackageFolders>
            <clear />
          </fallbackPackageFolders>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="local">
              <package pattern="Rendlio.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "declares no source keyed 'local'")]
    // Sources pinned, mapping left inherited. Green on a machine with no mapping rules of its own,
    // NU1101 on one whose user-level config routes '*' somewhere this file does not declare.
    [InlineData(
        """
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="packages" />
          </config>
          <fallbackPackageFolders>
            <clear />
          </fallbackPackageFolders>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
          </packageSources>
        </configuration>
        """,
        "declares no <packageSourceMapping>")]
    // The mapping section present but not cleared, so the machine's rules apply alongside it.
    [InlineData(
        """
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="packages" />
          </config>
          <fallbackPackageFolders>
            <clear />
          </fallbackPackageFolders>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="local">
              <package pattern="Rendlio.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "<packageSourceMapping> does not open with <clear />")]
    // Cleared and mapped, with the package under test left to the catch-all. Harmless today because
    // nothing of this name is published; the day one is, the check installs the released package
    // and reports on that instead of on the pack in front of it.
    [InlineData(
        """
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="packages" />
          </config>
          <fallbackPackageFolders>
            <clear />
          </fallbackPackageFolders>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "resolves Rendlio.Analyzers at the source 'nuget.org'")]
    // The same ending written the other way round: the family pattern named, and pointed at the
    // public feed rather than at the pack.
    [InlineData(
        """
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="packages" />
          </config>
          <fallbackPackageFolders>
            <clear />
          </fallbackPackageFolders>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="nuget.org">
              <package pattern="Rendlio.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "resolves Rendlio.Analyzers at the source 'nuget.org'")]
    public void A_configuration_that_lets_another_copy_answer_is_reported(string config, string expected)
    {
        string violation = Assert.Single(LocalConsumeIsolation.Inspect("nuget.config", config));

        Assert.Contains(expected, violation, StringComparison.Ordinal);
    }

    [Theory]
    // The package's own id instead of the family pattern. Where an id resolves from is decided by
    // the longest pattern matching it, so this is the tighter mapping, not a broken one — and a
    // rule that matched on the spelling would report the better version of itself.
    [InlineData("<package pattern=\"Rendlio.Analyzers\" />")]
    // The same id in another casing. NuGet compares ids case-insensitively, so this resolves at the
    // scratch feed exactly as the line above does.
    [InlineData("<package pattern=\"rendlio.analyzers\" />")]
    // A pattern narrower than the family and wider than the id, which still beats the catch-all.
    [InlineData("<package pattern=\"Rendlio.Analy*\" />")]
    public void A_mapping_that_resolves_the_package_at_the_feed_however_written_is_clean(string pattern)
    {
        string config = Isolated.Replace(
            "<package pattern=\"Rendlio.*\" />", pattern, StringComparison.Ordinal);

        // The replacement has to have happened, or this asserts that the unmodified shape is clean
        // — which the test above already says, and which would make every case here vacuous.
        Assert.NotEqual(Isolated, config);
        Assert.Empty(LocalConsumeIsolation.Inspect("nuget.config", config));
    }

    [Fact]
    public void A_mapping_that_names_only_other_packages_is_reported_for_resolving_nothing()
    {
        // A pattern that does not match is not a shorter match than one that does. Scored as if it
        // were, a section naming nothing but other packages would read as one that resolves this id
        // — at whichever source happened to be listed first.
        string violation = Assert.Single(LocalConsumeIsolation.Inspect(
            "nuget.config",
            Isolated.Replace("<package pattern=\"*\" />", "<package pattern=\"Contoso.*\" />", StringComparison.Ordinal)
                   .Replace("<package pattern=\"Rendlio.*\" />", "<package pattern=\"Fabrikam.*\" />", StringComparison.Ordinal)));

        Assert.Contains("maps no pattern that matches Rendlio.Analyzers", violation, StringComparison.Ordinal);
    }

    /// <summary>The committed configuration, on disk.</summary>
    private static string ConfigFullPath =>
        Path.Combine(
            RepositoryLayout.Root,
            LocalConsumeIsolation.ConfigPath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Every NuGet configuration a restore in this repository would merge, named relative to the
    /// root, in a stable order and without duplicates.
    /// </summary>
    /// <remarks>
    /// Walked from each project up to the repository root and no further: above the root is the
    /// developer's own machine, which this repository does not get to have opinions about — that is
    /// what the root file's own <c>&lt;clear /&gt;</c> elements are for. Names are matched without
    /// regard to case because that is how NuGet discovers the file, so a copy that differs only in
    /// casing is one restore reads and this walk has to see.
    /// </remarks>
    private static List<string> ConfigurationsOnProjectWalkUpPaths()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string project in Projects())
        {
            for (DirectoryInfo? directory = new(Path.GetDirectoryName(project) ?? RepositoryLayout.Root);
                 directory is not null;
                 directory = directory.Parent)
            {
                foreach (string file in Directory.EnumerateFiles(directory.FullName)
                             .Where(path => string.Equals(
                                 Path.GetFileName(path),
                                 LocalConsumeIsolation.ConfigFileName,
                                 StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(Path.GetRelativePath(RepositoryLayout.Root, file).Replace('\\', '/'));
                }

                if (string.Equals(
                        Path.TrimEndingDirectorySeparator(directory.FullName),
                        Path.TrimEndingDirectorySeparator(RepositoryLayout.Root),
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }

        return [.. found];
    }

    /// <summary>Every project in this repository, build output aside, in a stable order.</summary>
    private static List<string> Projects() =>
        [.. Directory.EnumerateFiles(RepositoryLayout.Root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(RepositoryLayout.Root, path)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => _buildOutput.Contains(segment, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.Ordinal)];
}
