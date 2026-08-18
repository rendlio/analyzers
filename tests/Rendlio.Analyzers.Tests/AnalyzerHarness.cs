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
    /// A directory for the throwaway compilation to live in. A source needs a path before an
    /// <c>.editorconfig</c> section header can match it, and that path has to be rooted or the
    /// config machinery declines to read it. Nothing is written to disk.
    /// </summary>
    private const string WorkingDirectory = "/repo/";

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
    internal static Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        string assemblyName,
        params string[] sources) =>
        RunAsync(analyzer, assemblyName, editorConfig: null, noWarn: null, sources);

    /// <summary>
    /// As <see cref="RunAsync(DiagnosticAnalyzer, string, string[])"/>, with
    /// <paramref name="editorConfig"/> in force over <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// The text is parsed by the compiler's own <see cref="AnalyzerConfig"/> and fed in through both
    /// halves of the mechanism a real build uses: the per-tree severities a
    /// <see cref="SyntaxTreeOptionsProvider"/> carries, and the raw key/values the analyzer driver
    /// reads for bulk configuration. Handing it the file a consumer would actually write is what
    /// makes a snippet on a rule page executable, rather than a re-expression of that snippet which
    /// could agree with the page while disagreeing with a build.
    /// </remarks>
    /// <param name="analyzer">The rule under test.</param>
    /// <param name="editorConfig">The contents of an <c>.editorconfig</c> beside the source.</param>
    /// <param name="source">The file to analyse.</param>
    /// <param name="assemblyName">The compilation's assembly name.</param>
    internal static Task<ImmutableArray<Diagnostic>> RunConfiguredAsync(
        DiagnosticAnalyzer analyzer,
        string editorConfig,
        string source,
        string assemblyName = "Consumer") =>
        RunAsync(analyzer, assemblyName, editorConfig, noWarn: null, source);

    /// <summary>
    /// As <see cref="RunAsync(DiagnosticAnalyzer, string, string[])"/>, with the ids
    /// <paramref name="noWarn"/> names suppressed the way a <c>&lt;NoWarn&gt;</c> in a project file
    /// suppresses them.
    /// </summary>
    /// <remarks>
    /// A different layer from the <c>.editorconfig</c> severities: MSBuild expands the property and
    /// hands the list to the compiler as <c>/nowarn:</c>, which turns each id into
    /// <see cref="ReportDiagnostic.Suppress"/> in
    /// <see cref="CompilationOptions.SpecificDiagnosticOptions"/>. That last step is the one that
    /// decides whether a rule goes quiet, so it is the one reproduced here.
    /// </remarks>
    /// <param name="analyzer">The rule under test.</param>
    /// <param name="noWarn">
    /// The <c>NoWarn</c> value after MSBuild has expanded it, so ids only — split here on the
    /// separators the compiler's own command-line parser accepts.
    /// </param>
    /// <param name="source">The file to analyse.</param>
    /// <param name="assemblyName">The compilation's assembly name.</param>
    internal static Task<ImmutableArray<Diagnostic>> RunNoWarnAsync(
        DiagnosticAnalyzer analyzer,
        string noWarn,
        string source,
        string assemblyName = "Consumer") =>
        RunAsync(analyzer, assemblyName, editorConfig: null, noWarn, source);

    private static async Task<ImmutableArray<Diagnostic>> RunAsync(
        DiagnosticAnalyzer analyzer,
        string assemblyName,
        string? editorConfig,
        string? noWarn,
        params string[] sources)
    {
        // Nothing to analyse is not "the rule found nothing" either.
        if (sources.Length == 0)
        {
            throw new ArgumentException("At least one source is required.", nameof(sources));
        }

        SyntaxTree[] trees =
        [
            .. sources.Select(static (source, index) => CSharpSyntaxTree.ParseText(
                source,
                new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Diagnose),
                path: WorkingDirectory + "Source" + index.ToString(CultureInfo.InvariantCulture) + ".cs")),
        ];

        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable);

        if (noWarn is not null)
        {
            compilationOptions = compilationOptions.WithSpecificDiagnosticOptions(Suppressed(noWarn));
        }

        AnalyzerOptions analyzerOptions = Configure(editorConfig, trees, ref compilationOptions);

        var compilation = CSharpCompilation.Create(assemblyName, trees, _references, compilationOptions);

        Fail("The test snippet does not compile", compilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));

        ImmutableArray<Diagnostic> reported = await compilation
            .WithAnalyzers([analyzer], analyzerOptions)
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None);

        // AD0001 is the compiler reporting that the analyzer itself threw. Without this it arrives
        // as "the rule found nothing".
        Fail(
            $"{analyzer.GetType().Name} threw during analysis",
            reported.Where(diagnostic => string.Equals(diagnostic.Id, "AD0001", StringComparison.Ordinal)));

        return reported;
    }

    /// <summary>
    /// Parses <paramref name="editorConfig"/>, hangs the per-tree severities it sets off
    /// <paramref name="compilationOptions"/>, and returns the analyzer options carrying its raw
    /// key/values. With no config, returns empty options and leaves the compilation options alone.
    /// </summary>
    private static AnalyzerOptions Configure(
        string? editorConfig,
        IReadOnlyList<SyntaxTree> trees,
        ref CSharpCompilationOptions compilationOptions)
    {
        if (editorConfig is null)
        {
            return new AnalyzerOptions([], NoConfiguration.Instance);
        }

        var set = AnalyzerConfigSet.Create(
            ImmutableArray.Create(AnalyzerConfig.Parse(editorConfig, WorkingDirectory + ".editorconfig")),
            out ImmutableArray<Diagnostic> parseDiagnostics);

        // A section header the config machinery could not read would apply nothing at all, which
        // from here is indistinguishable from a rule that ignores configuration entirely.
        Fail("The test .editorconfig does not parse", parseDiagnostics);

        var severities = new Dictionary<SyntaxTree, ImmutableDictionary<string, ReportDiagnostic>>();
        var keys = new Dictionary<SyntaxTree, ImmutableDictionary<string, string>>();

        foreach (SyntaxTree tree in trees)
        {
            AnalyzerConfigOptionsResult result = set.GetOptionsForSourcePath(tree.FilePath);
            Fail($"The test .editorconfig does not apply to {tree.FilePath}", result.Diagnostics);

            severities[tree] = result.TreeOptions;
            keys[tree] = result.AnalyzerOptions;
        }

        compilationOptions = compilationOptions.WithSyntaxTreeOptionsProvider(new Severities(severities));
        return new AnalyzerOptions([], new Keys(keys));
    }

    /// <summary>
    /// Every id a <c>NoWarn</c> value names, mapped to suppressed. Separated on <c>;</c>, <c>,</c> or
    /// whitespace, all three of which the compiler accepts, and de-duplicated because the idiomatic
    /// spelling appends to the inherited value and may well name an id twice.
    /// </summary>
    private static ImmutableDictionary<string, ReportDiagnostic> Suppressed(string noWarn) =>
        noWarn
            .Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableDictionary(id => id, _ => ReportDiagnostic.Suppress, StringComparer.Ordinal);

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

    /// <summary>
    /// The severities an <c>.editorconfig</c> set per file with <c>dotnet_diagnostic.ID.severity</c>.
    /// This is the half of the mechanism the compiler applies itself, so a rule turned off here is
    /// one the analyzer may never even be asked about.
    /// </summary>
    private sealed class Severities(IReadOnlyDictionary<SyntaxTree, ImmutableDictionary<string, ReportDiagnostic>> perTree)
        : SyntaxTreeOptionsProvider
    {
        /// <summary>
        /// Unknown rather than not-generated, deliberately: the rules in this pack are required to
        /// analyse generated code, and saying so here would let a regression hide behind the answer.
        /// </summary>
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) =>
            GeneratedKind.Unknown;

        public override bool TryGetDiagnosticValue(
            SyntaxTree tree,
            string diagnosticId,
            CancellationToken cancellationToken,
            out ReportDiagnostic severity)
        {
            if (perTree.TryGetValue(tree, out ImmutableDictionary<string, ReportDiagnostic>? severities)
                && severities.TryGetValue(diagnosticId, out severity))
            {
                return true;
            }

            severity = ReportDiagnostic.Default;
            return false;
        }

        /// <summary>
        /// Nothing global: these tests configure through a file beside the source, which is what a
        /// consumer writes and what the rule pages show.
        /// </summary>
        public override bool TryGetGlobalDiagnosticValue(
            string diagnosticId,
            CancellationToken cancellationToken,
            out ReportDiagnostic severity)
        {
            severity = ReportDiagnostic.Default;
            return false;
        }
    }

    /// <summary>
    /// Every other key an <c>.editorconfig</c> set, which is where the bulk-configuration ones
    /// (<c>dotnet_analyzer_diagnostic.category-NAME.severity</c>) arrive. These the analyzer driver
    /// applies, so a rule configured this way still runs and is reported at the severity asked for.
    /// </summary>
    private sealed class Keys(IReadOnlyDictionary<SyntaxTree, ImmutableDictionary<string, string>> perTree)
        : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions => Nothing.Instance;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
            perTree.TryGetValue(tree, out ImmutableDictionary<string, string>? keys)
                ? new Lookup(keys)
                : Nothing.Instance;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Nothing.Instance;
    }

    /// <summary>The provider for a run with no <c>.editorconfig</c> at all.</summary>
    private sealed class NoConfiguration : AnalyzerConfigOptionsProvider
    {
        internal static readonly NoConfiguration Instance = new();

        public override AnalyzerConfigOptions GlobalOptions => Nothing.Instance;

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Nothing.Instance;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Nothing.Instance;
    }

    private sealed class Lookup(ImmutableDictionary<string, string> keys) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value) => keys.TryGetValue(key, out value!);
    }

    private sealed class Nothing : AnalyzerConfigOptions
    {
        internal static readonly Nothing Instance = new();

        public override bool TryGetValue(string key, out string value)
        {
            value = null!;
            return false;
        }
    }
}
