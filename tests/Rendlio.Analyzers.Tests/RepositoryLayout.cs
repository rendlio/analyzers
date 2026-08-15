namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Locates this repository on disk, for the checks whose subject is the repository itself rather
/// than the analyzers in it.
/// </summary>
/// <remarks>
/// Several rules here are about files rather than about code — the pages this repository
/// publishes, the workflows it runs — and every one of them starts from the same place. It is
/// resolved once, here, so the rule for finding the root and the reason behind that rule live in
/// one place instead of being copied into each new check that needs a path.
/// </remarks>
internal static class RepositoryLayout
{
    /// <summary>The file whose presence marks the repository root.</summary>
    private const string SolutionFile = "Rendlio.Analyzers.slnx";

    /// <summary>The repository root, walked up from the test binary.</summary>
    /// <remarks>
    /// Found by walking rather than taken from a compile-time <c>[CallerFilePath]</c>, because CI
    /// builds with <c>ContinuousIntegrationBuild</c> — which normalises embedded source paths to a
    /// form that does not exist on any disk. A compile-time path would resolve to nothing precisely
    /// in the run where these rules matter most.
    /// </remarks>
    internal static string Root { get; } = FindRoot();

    /// <summary>Directory segments that hold build output rather than source.</summary>
    private static readonly string[] _buildOutput = ["bin", "obj", "artifacts"];

    /// <summary>Every project in this repository, build output aside, in a stable order.</summary>
    /// <remarks>
    /// Walked rather than read out of the solution file: a project that builds is not the only
    /// project that restores, and one added to the tree but not yet to the solution still imports
    /// everything above it.
    /// </remarks>
    internal static List<string> Projects() =>
        [.. Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)];

    /// <summary>Every workflow file in this repository, in a stable order.</summary>
    /// <remarks>
    /// Both extensions, because GitHub accepts both and a rule enforced over one would be silently
    /// skippable by naming a file the other way. Resolved here rather than in each test class that
    /// sweeps workflows: the rules about action pins, about a pack step's platform and about the
    /// gates that read a packed artifact are separate rules over the same set of files, and a
    /// second copy of the walk is a second place for that set to quietly shrink.
    /// </remarks>
    internal static List<string> Workflows()
    {
        string directory = Path.Combine(Root, ".github", "workflows");

        return
        [
            .. Directory.EnumerateFiles(directory, "*.yml", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly))
                .OrderBy(path => path, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Every file called <paramref name="fileName"/> that a build here would find by walking up
    /// from a project, named relative to the root, in a stable order and without duplicates.
    /// </summary>
    /// <remarks>
    /// MSBuild and NuGet both discover their per-directory files this way — <c>Directory.Build.props</c>
    /// and <c>nuget.config</c> alike — by walking from the project being built towards the root. So
    /// the question worth asking about a copy of either is not whether one exists in the repository
    /// but whether anything imports it: one on that path changes every build here, and one off it is
    /// inert to the build and live only where it is deliberately copied.
    /// <para>
    /// The walk stops at the repository root. Above it is the developer's own machine, which this
    /// repository does not get to have opinions about — clearing what it contributes is the job of
    /// the files themselves. Names are matched without regard to case, because that is how both
    /// tools discover them, so a copy differing only in casing is one a build reads and this walk
    /// has to see.
    /// </para>
    /// </remarks>
    internal static List<string> FilesOnProjectWalkUpPaths(string fileName)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string project in Projects())
        {
            for (DirectoryInfo? directory = new(Path.GetDirectoryName(project) ?? Root);
                 directory is not null;
                 directory = directory.Parent)
            {
                foreach (string file in Directory.EnumerateFiles(directory.FullName)
                             .Where(path => string.Equals(
                                 Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(Path.GetRelativePath(Root, file).Replace('\\', '/'));
                }

                if (string.Equals(
                        Path.TrimEndingDirectorySeparator(directory.FullName),
                        Path.TrimEndingDirectorySeparator(Root),
                        StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }

        return [.. found];
    }

    /// <summary>True when <paramref name="path"/> sits under a build-output directory.</summary>
    /// <remarks>
    /// Tested against the path relative to the root, not the absolute one: a checkout living under
    /// a directory of one of those names — <c>C:\bin\analyzers</c> — would otherwise classify every
    /// file in the repository as output and leave the walks above with nothing to inspect.
    /// </remarks>
    private static bool IsBuildOutput(string path) =>
        Path.GetRelativePath(Root, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => _buildOutput.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static string FindRoot()
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
