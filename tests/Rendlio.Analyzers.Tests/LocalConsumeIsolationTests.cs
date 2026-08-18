namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds both committed local-consume files to <see cref="LocalConsumeIsolation"/>, holds that
/// reader to fixtures which break each rule on purpose, and holds the files themselves to staying
/// out of the way of this repository's own builds and restores.
/// </summary>
/// <remarks>
/// Every fixture below is a complete, otherwise-sealed file with exactly one rule broken, so the
/// assertion can be that the reader reports precisely one thing. A fixture that omitted the parts
/// it was not about would draw collateral violations, and a check tolerating those could not tell a
/// reader that names one cause from a reader that names everything.
/// </remarks>
public sealed class LocalConsumeIsolationTests
{
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

    /// <summary>The properties that state the same thing where the environment cannot outrank it.</summary>
    private const string Properties = """
        <Project>
          <PropertyGroup>
            <RestorePackagesPath>$(MSBuildThisFileDirectory)packages</RestorePackagesPath>
            <RestoreFallbackFolders>clear</RestoreFallbackFolders>
          </PropertyGroup>
        </Project>
        """;

    [Fact]
    public void The_committed_configuration_is_sealed_against_a_stale_copy() =>
        Assert.Empty(LocalConsumeIsolation.Inspect(
            LocalConsumeIsolation.ConfigPath, File.ReadAllText(ConfigFullPath)));

    [Fact]
    public void The_committed_properties_state_it_where_the_environment_cannot_override_it() =>
        Assert.Empty(LocalConsumeIsolation.InspectProps(
            LocalConsumeIsolation.PropsPath, File.ReadAllText(PropsFullPath)));

    [Fact]
    public void The_properties_travel_with_the_configuration_they_back()
    {
        // Guards the guard, and the thing most likely to go wrong about this pair: they only work
        // copied together. The configuration alone is silently defeated by NUGET_PACKAGES, and the
        // properties alone leave the feed undeclared — so a file present here but not named beside
        // the other is a run that reports success having isolated nothing.
        Assert.True(
            File.Exists(PropsFullPath),
            $"{LocalConsumeIsolation.PropsPath} does not exist, so the isolation rests on settings the environment overrides.");

        Assert.Equal(
            Path.GetDirectoryName(ConfigFullPath), Path.GetDirectoryName(PropsFullPath));

        string text = File.ReadAllText(PropsFullPath);

        Assert.Contains(LocalConsumeIsolation.PackagesPathProperty, text, StringComparison.Ordinal);
        Assert.Contains(LocalConsumeIsolation.FallbackFoldersProperty, text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fully_isolating_properties_file_is_clean() =>
        Assert.Empty(LocalConsumeIsolation.InspectProps(LocalConsumeIsolation.PropsPath, Properties));

    [Theory]
    // The hole the configuration cannot close by itself, and the reason this file exists: NuGet
    // reads NUGET_PACKAGES ahead of globalPackagesFolder and takes it without a word, so a machine
    // exporting one restores into the folder holding every copy of this package it has ever seen.
    [InlineData(
        """
        <Project>
          <PropertyGroup>
            <RestoreFallbackFolders>clear</RestoreFallbackFolders>
          </PropertyGroup>
        </Project>
        """,
        "sets no <RestorePackagesPath>")]
    // A path that looks relative and is not resolved where the reader expects. An MSBuild property
    // is evaluated in the project that imports it, so this names a folder under the consumer rather
    // than beside the feed — which happens to work here and stops working the moment the consumer
    // moves.
    [InlineData(
        """
        <Project>
          <PropertyGroup>
            <RestorePackagesPath>packages</RestorePackagesPath>
            <RestoreFallbackFolders>clear</RestoreFallbackFolders>
          </PropertyGroup>
        </Project>
        """,
        "points <RestorePackagesPath> at 'packages'")]
    // Conditioned on the property being unset, which is how the SDK writes its own defaults and how
    // a careful-looking edit would write this one. Harmless-looking and fatal: it fires in every
    // case except one where something else already decided, which is the case it is for.
    [InlineData(
        """
        <Project>
          <PropertyGroup>
            <RestorePackagesPath Condition="'$(RestorePackagesPath)' == ''">$(MSBuildThisFileDirectory)packages</RestorePackagesPath>
            <RestoreFallbackFolders>clear</RestoreFallbackFolders>
          </PropertyGroup>
        </Project>
        """,
        "conditions <RestorePackagesPath>")]
    // The same defeat one level up, where MSBuild convention puts a condition and where the
    // property underneath still reads as an ordinary unconditional line. Only the one property sits
    // in the conditioned group, so this stays a fixture with exactly one rule broken.
    [InlineData(
        """
        <Project>
          <PropertyGroup Condition="'$(OS)' == 'Windows_NT'">
            <RestorePackagesPath>$(MSBuildThisFileDirectory)packages</RestorePackagesPath>
          </PropertyGroup>
          <PropertyGroup>
            <RestoreFallbackFolders>clear</RestoreFallbackFolders>
          </PropertyGroup>
        </Project>
        """,
        "conditions <RestorePackagesPath>")]
    // The second environment door left open. NUGET_FALLBACK_PACKAGES survives the configuration
    // clearing fallbackPackageFolders, so this is the only place it is answered.
    [InlineData(
        """
        <Project>
          <PropertyGroup>
            <RestorePackagesPath>$(MSBuildThisFileDirectory)packages</RestorePackagesPath>
          </PropertyGroup>
        </Project>
        """,
        "sets no <RestoreFallbackFolders>")]
    // Set to a folder rather than emptied. Adding one is the opposite of the intent, and it reads
    // as configuration rather than as the mistake it is.
    [InlineData(
        """
        <Project>
          <PropertyGroup>
            <RestorePackagesPath>$(MSBuildThisFileDirectory)packages</RestorePackagesPath>
            <RestoreFallbackFolders>$(MSBuildThisFileDirectory)fallback</RestoreFallbackFolders>
          </PropertyGroup>
        </Project>
        """,
        "rather than 'clear'")]
    public void A_properties_file_that_leaves_the_folder_to_the_environment_is_reported(
        string props, string expected)
    {
        string violation = Assert.Single(
            LocalConsumeIsolation.InspectProps("Directory.Build.props", props));

        Assert.Contains(expected, violation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_properties_file_carrying_the_old_MSBuild_namespace_is_still_read()
    {
        // The reader matches local names, so a namespace an editor puts back does not turn a
        // compliant file into an unreadable one reported clean. Proven rather than assumed: a
        // qualified-name match would find no properties here and report nothing wrong.
        string namespaced = Properties.Replace(
            "<Project>",
            "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">",
            StringComparison.Ordinal);

        Assert.NotEqual(Properties, namespaced);
        Assert.Empty(LocalConsumeIsolation.InspectProps(LocalConsumeIsolation.PropsPath, namespaced));
    }

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
    public void No_project_in_this_repository_builds_or_restores_through_either_of_them()
    {
        // Both files are exactly the kind a build picks up by accident. NuGet walks up from the
        // project being restored to find a configuration, and MSBuild does the same for properties,
        // so a copy of either on that path applies to every build here: the configuration would
        // widen the sources this repository pins — in a section RestorePinningTests does not read,
        // because it inspects the root file by name — and the properties file is worse, because
        // MSBuild stops at the FIRST one it meets and would detach the root's from everything
        // beneath it. Living off the walk is what makes both inert here and live only where copied.
        Assert.Equal(
            ["NuGet.config"],
            RepositoryLayout.FilesOnProjectWalkUpPaths(LocalConsumeIsolation.ConfigFileName));

        Assert.Equal(
            ["Directory.Build.props"],
            RepositoryLayout.FilesOnProjectWalkUpPaths(LocalConsumeIsolation.PropsFileName));
    }

    [Fact]
    public void The_walk_that_finds_them_reaches_the_projects_it_is_about()
    {
        // Guards the guard above, which reports what a walk found and would report a tidy result
        // for having walked nothing. Presence rather than a count: this repository is expected to
        // gain projects, and a guard reading "expected 2, actual 3" blames the walk for someone
        // else's addition.
        List<string> projects = RepositoryLayout.Projects();

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
        "points globalPackagesFolder at '/scratch/packages'")]
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
    // The scratch feed declared under the right key and pointed somewhere else entirely. The
    // mapping still sends the package there, so the check installs whatever that feed answers with
    // and grades a build this run did not produce — the same wrong answer the packages folder gives,
    // arrived at through the source rather than the cache.
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
            <add key="local" value="https://packages.example.invalid/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <clear />
            <packageSource key="local">
              <package pattern="Rendlio.*" />
            </packageSource>
          </packageSourceMapping>
        </configuration>
        """,
        "points the 'local' source at 'https://packages.example.invalid/v3/index.json'")]
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

    /// <summary>Its other half, on disk beside it.</summary>
    private static string PropsFullPath =>
        Path.Combine(
            RepositoryLayout.Root,
            LocalConsumeIsolation.PropsPath.Replace('/', Path.DirectorySeparatorChar));
}
