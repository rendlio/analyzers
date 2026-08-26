using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// The contracts every rule in the pack shares: that the verdict and the message never move with the
/// culture, that repeating a run reproduces it exactly, and that the public
/// <see cref="DiagnosticAnalyzer.Initialize"/> override honours the argument check its documentation
/// promises.
/// </summary>
/// <remarks>
/// <para>The culture cases are not ceremony. A consumer's build runs under their locale, and every
/// string these rules turn on is matched ordinally today — <c>System.Random</c>,
/// <c>System.Diagnostics.Process</c>, <c>System.Runtime.InteropServices.DllImportAttribute</c>. The
/// moment one of those comparisons is "tidied" into a case-insensitive or culture-aware form, the
/// rule stops enforcing on a Turkish developer's machine, and that failure would be invisible in CI,
/// which runs neither culture.</para>
/// <para>Each culture test opens with <see cref="ShouldBeARealCulture"/>. Asserting on
/// <c>CultureInfo.Name</c> would not do: under <c>InvariantGlobalization</c> a fabricated
/// <c>tr-TR</c> still reports the name <c>tr-TR</c> while carrying invariant data, so the name check
/// passes and every assertion after it becomes vacuous. Formatting a number is the cheapest probe
/// that reads the culture's actual data.</para>
/// </remarks>
public sealed class AnalyzerContractTests
{
    private const string Consumer = "Consumer";

    /// <summary>
    /// One file that violates both rules, so each of them reports something on the same run: three
    /// RENDLIO001 findings and two RENDLIO002. The P/Invoke is a local function deliberately —
    /// RENDLIO001 reaches it through a second dispatch path (a syntax action, not the symbol action
    /// the other rows use), and without it here every assertion below would hold for only half that
    /// rule.
    /// </summary>
    private const string Violations = """
        using System;
        using System.Runtime.InteropServices;

        namespace Example;

        internal static class Sut
        {
            internal static void Spawn() => System.Diagnostics.Process.Start("cmd");

            internal static object Net() => typeof(System.Net.Http.HttpClient);

            internal static int Stamp() => DateTime.Now.Year;

            internal static Guid Name() => Guid.NewGuid();

            internal static int Interop()
            {
                [DllImport("native")]
                static extern int Inner();

                return Inner();
            }
        }
        """;

    /// <summary>Every rule the pack ships, as the type that reports it.</summary>
    private static readonly Type[] _liveAnalyzers =
    [
        typeof(BannedApiAnalyzer),
        typeof(NonDeterminismAnalyzer),
    ];

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task Every_verdict_is_the_same_under_any_current_culture(string culture)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            ShouldBeARealCulture(CultureInfo.CurrentCulture);

            (await RunAsync(new BannedApiAnalyzer())).Length.ShouldBe(3);
            (await RunAsync(new NonDeterminismAnalyzer())).Length.ShouldBe(2);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task The_message_text_is_the_same_in_any_culture(string culture)
    {
        // The rationale a message quotes is not a localisable resource and the API it names is an
        // identifier, so neither may move with the culture the message is formatted in. Errors that
        // read differently per machine are harder to grep for than the rule is worth.
        var formatted = new CultureInfo(culture);
        ShouldBeARealCulture(formatted);

        foreach (Type analyzer in _liveAnalyzers)
        {
            ImmutableArray<Diagnostic> diagnostics = await RunAsync(New(analyzer));

            diagnostics.ShouldNotBeEmpty(analyzer.Name);
            foreach (Diagnostic diagnostic in diagnostics)
            {
                diagnostic.GetMessage(formatted)
                    .ShouldBe(diagnostic.GetMessage(CultureInfo.InvariantCulture));
            }
        }
    }

    [Theory]
    [InlineData(typeof(BannedApiAnalyzer))]
    [InlineData(typeof(NonDeterminismAnalyzer))]
    public async Task Analysing_the_same_input_twice_reports_exactly_the_same_diagnostics(Type analyzer)
    {
        // A pack that is partly about reproducible output has to be reproducible itself. Every
        // analyzer here calls EnableConcurrentExecution, so the order Roslyn hands the diagnostics
        // back in is not guaranteed — but the set, the spans and the messages are. A rule that
        // reported a violation only sometimes would fail a build only sometimes.
        string[] first = Fingerprint(await RunAsync(New(analyzer)));
        string[] second = Fingerprint(await RunAsync(New(analyzer)));

        first.ShouldNotBeEmpty();
        second.ShouldBe(first);

        static string[] Fingerprint(ImmutableArray<Diagnostic> diagnostics) =>
            [
                .. diagnostics
                    .Select(diagnostic => string.Create(
                        CultureInfo.InvariantCulture,
                        $"{diagnostic.Id} {diagnostic.Location.GetLineSpan()} {diagnostic.GetMessage(CultureInfo.InvariantCulture)}"))
                    .Order(StringComparer.Ordinal),
            ];
    }

    [Theory]
    [InlineData(typeof(BannedApiAnalyzer))]
    [InlineData(typeof(NonDeterminismAnalyzer))]
    public void Initialize_rejects_a_null_context(Type analyzer)
    {
        // Every override documents this <exception>, and Initialize is public on a public sealed
        // type: it is part of the surface a Roslyn host calls.
        ArgumentNullException thrown =
            Should.Throw<ArgumentNullException>(() => New(analyzer).Initialize(null!));

        thrown.ParamName.ShouldBe("context");
    }

    private static Task<ImmutableArray<Diagnostic>> RunAsync(DiagnosticAnalyzer analyzer) =>
        AnalyzerHarness.RunAsync(analyzer, Consumer, Violations);

    private static DiagnosticAnalyzer New(Type analyzer) =>
        (DiagnosticAnalyzer)(Activator.CreateInstance(analyzer)
            ?? throw new InvalidOperationException($"{analyzer.Name} has no parameterless constructor."));

    /// <summary>
    /// Proves the culture carries real data rather than being an invariant stand-in wearing its
    /// name. Both cultures used here write 1.5 with a comma; the invariant culture writes a dot.
    /// </summary>
    private static void ShouldBeARealCulture(CultureInfo culture) =>
        1.5.ToString(culture).ShouldBe(
            "1,5",
            $"'{culture.Name}' carries invariant data — InvariantGlobalization is not switched off "
            + "for this project, so every culture assertion here would be vacuous.");
}
