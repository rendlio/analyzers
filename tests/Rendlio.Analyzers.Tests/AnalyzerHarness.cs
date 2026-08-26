using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Runs one analyzer over one throwaway compilation.
/// </summary>
/// <remarks>
/// <para>References come from the test host's own shared framework, so the rules are matched against
/// the real BCL symbols (<c>System.Net.Http.HttpClient</c>, <c>System.Random</c>) rather than against
/// stubs. A rule that resolves the wrong symbol therefore fails here rather than in a consumer's
/// build.</para>
/// <para>The assembly name is a parameter because a compilation needs one, and because the rules in
/// this pack are required NOT to read it: what they apply to is decided by the package reference,
/// not by what the assembly is called. The cases that pin that pass a variety of names through
/// here.</para>
/// </remarks>
internal static class AnalyzerHarness
{
    private static readonly ImmutableArray<MetadataReference> _references = FrameworkReferences();

    /// <summary>
    /// Compiles <paramref name="sources"/> as <paramref name="assemblyName"/> and returns everything
    /// <paramref name="analyzer"/> reported.
    /// </summary>
    /// <param name="analyzer">The rule under test.</param>
    /// <param name="assemblyName">The compilation's assembly name.</param>
    /// <param name="sources">One file per element.</param>
    /// <exception cref="ArgumentException"><paramref name="sources"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// The snippet does not compile, or the analyzer threw. Both are fatal to the test rather than a
    /// verdict: a snippet with a compile error reports no analyzer diagnostics for the wrong reason,
    /// which would make every "the rule stays silent" assertion pass vacuously.
    /// </exception>
    internal static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        string assemblyName,
        params string[] sources)
    {
        // Nothing to analyse is not "the rule found nothing" either.
        if (sources.Length == 0)
        {
            throw new ArgumentException("At least one source is required.", nameof(sources));
        }

        var compilation = CSharpCompilation.Create(
            assemblyName,
            sources.Select(static source => CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Diagnose))),
            _references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        Fail("The test snippet does not compile", compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        ImmutableArray<Diagnostic> reported = await compilation
            .WithAnalyzers([analyzer])
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        // AD0001 is the compiler reporting that the analyzer itself threw. Without this it arrives
        // as "the rule found nothing".
        Fail(
            $"{analyzer.GetType().Name} threw during analysis",
            reported.Where(diagnostic => string.Equals(diagnostic.Id, "AD0001", StringComparison.Ordinal)));

        return reported;
    }

    private static void Fail(string what, IEnumerable<Diagnostic> diagnostics)
    {
        string[] messages = [.. diagnostics.Select(diagnostic => diagnostic.ToString())];
        if (messages.Length > 0)
        {
            throw new InvalidOperationException($"{what}:{Environment.NewLine}{string.Join(Environment.NewLine, messages)}");
        }
    }

    private static ImmutableArray<MetadataReference> FrameworkReferences()
    {
        string trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException(
                "TRUSTED_PLATFORM_ASSEMBLIES is unset, so no reference set can be built for the test compilations.");

        return
        [
            .. trusted
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(path => path.EndsWith(".dll", StringComparison.Ordinal))
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)),
        ];
    }

    /// <summary>
    /// Asserts the run produced exactly one diagnostic, with the expected id, error severity, and a
    /// message naming <paramref name="expectedInMessage"/>.
    /// </summary>
    internal static void ShouldBeSingleError(
        this ImmutableArray<Diagnostic> diagnostics,
        string expectedId,
        string expectedInMessage)
    {
        Diagnostic diagnostic = diagnostics.ShouldHaveSingleItem();

        diagnostic.Id.ShouldBe(expectedId);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage(CultureInfo.InvariantCulture).ShouldContain(expectedInMessage);
    }
}
