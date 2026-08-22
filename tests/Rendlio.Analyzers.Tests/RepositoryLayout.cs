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
