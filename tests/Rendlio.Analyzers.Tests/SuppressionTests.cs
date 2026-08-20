using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// The ways out, exactly as the rule pages under <c>docs/rules</c> write them down: a severity in
/// <c>.editorconfig</c>, a category switch in the same file, and a <c>#pragma</c> around one call
/// site.
/// </summary>
/// <remarks>
/// <para>These rules report at severity error by default, which makes the escape hatch part of the
/// contract rather than a footnote. A consumer who hits a false positive on a Friday needs the
/// documented suppression to work on the first try, and the cost of it not working is not a wrong
/// diagnostic — it is a build they cannot get green and a package they remove.</para>
/// <para>So each case runs the snippet the page publishes, through the same
/// <see cref="AnalyzerConfig"/> machinery a real build uses. A page that drifts from what the
/// compiler does fails here rather than in a stranger build log.</para>
/// <para>The other half is what suppression must NOT do. A category switch names a category, so it
/// has to leave the other category alone — the two are separate on purpose, because wanting
/// reproducible output is not the same want as wanting a sealed box — and a pragma names a span, so
/// it has to stop at the restore.</para>
/// </remarks>
public sealed class SuppressionTests
{
    private const string BannedApiRule = "RENDLIO001";
    private const string NonDeterminismRule = "RENDLIO002";

    private const string SecurityCategory = "Rendlio.Security";
    private const string DeterminismCategory = "Rendlio.Determinism";

    /// <summary>
    /// One violation of RENDLIO001, reported once: the type reference carries the diagnostic, so
    /// naming <c>Process</c> once is one error however many members are reached through it.
    /// </summary>
    private const string ReachesTheHost = """
        namespace Example;

        internal static class Sut
        {
            internal static void Run() => System.Diagnostics.Process.Start("cmd");
        }
        """;

    /// <summary>
    /// One violation of RENDLIO002, reported once. <c>Guid</c> itself is legal, so only the member
    /// reference is an error.
    /// </summary>
    private const string ReadsAmbientState = """
        using System;

        namespace Example;

        internal static class Sut
        {
            internal static string Name() => Guid.NewGuid().ToString("N");
        }
        """;

    public static TheoryData<string> EveryRule => [BannedApiRule, NonDeterminismRule];

    /// <summary>Each rule with the category it belongs to.</summary>
    public static TheoryData<string, string> EveryRuleAndCategory => new()
    {
        { BannedApiRule, SecurityCategory },
        { NonDeterminismRule, DeterminismCategory },
    };

    /// <summary>Each rule with the category that is NOT its own.</summary>
    public static TheoryData<string, string> EveryRuleAndTheOtherCategory => new()
    {
        { BannedApiRule, DeterminismCategory },
        { NonDeterminismRule, SecurityCategory },
    };

    // ---- The guard on all of it ----

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task Nothing_is_suppressed_that_was_not_asked_for(string rule)
    {
        // Guards every case below. Each of them proves a violation stopped being reported; none of
        // them can tell the difference between "the suppression worked" and "the snippet stopped
        // violating the rule". This is the case that tells the difference, so a snippet that decays
        // into clean code fails here instead of turning the rest of the file green and vacuous.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(rule, ViolationOf(rule));

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe(rule);
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    // ---- Severity, per rule ----

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task Setting_the_severity_to_none_silences_the_rule(string rule)
    {
        // The whole-rule escape hatch each page publishes, verbatim. A project that wants the ban
        // everywhere except one folder writes this in that folder.
        ImmutableArray<Diagnostic> diagnostics = await RunConfiguredAsync(
            rule,
            $"""
            [*.cs]
            dotnet_diagnostic.{rule}.severity = none
            """,
            ViolationOf(rule));

        diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task Lowering_the_severity_keeps_the_diagnostic_and_unfails_the_build(string rule)
    {
        // The middle setting, and the one a project migrating onto the pack actually wants: see
        // every violation, fix them in order, and do not have the build stop until you are done.
        // It is only useful if the diagnostic survives the downgrade, so that is what is pinned.
        ImmutableArray<Diagnostic> diagnostics = await RunConfiguredAsync(
            rule,
            $"""
            [*.cs]
            dotnet_diagnostic.{rule}.severity = warning
            """,
            ViolationOf(rule));

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe(rule);
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Warning);
    }

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task A_severity_set_for_another_rule_leaves_this_one_alone(string rule)
    {
        // Ids are family-scoped and never reused, which is what makes a suppression written today
        // still mean what its author meant by it. A rule that answered to an id belonging to
        // something else would break that promise from the other direction.
        ImmutableArray<Diagnostic> diagnostics = await RunConfiguredAsync(
            rule,
            """
            [*.cs]
            dotnet_diagnostic.RENDLIO999.severity = none
            """,
            ViolationOf(rule));

        diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    // ---- Category, per rule ----

    [Theory]
    [MemberData(nameof(EveryRuleAndCategory))]
    public async Task The_category_switch_moves_its_own_rule(string rule, string category)
    {
        // The bulk switch each page publishes. It is a different mechanism from the one above — the
        // compiler applies a severity, the analyzer driver applies a category — so it is pinned
        // separately rather than assumed to follow.
        ImmutableArray<Diagnostic> diagnostics = await RunConfiguredAsync(
            rule,
            $"""
            [*.cs]
            dotnet_analyzer_diagnostic.category-{category}.severity = warning
            """,
            ViolationOf(rule));

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe(rule);
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Warning);
    }

    [Theory]
    [MemberData(nameof(EveryRuleAndTheOtherCategory))]
    public async Task The_two_categories_are_separate_switches(string rule, string otherCategory)
    {
        // The reason the pack has two categories rather than one. Wanting reproducible output and
        // wanting a sealed box are different wants, and a consumer who turns one off has said
        // nothing about the other. Collapsing the categories in a tidy-up would silently widen
        // every such decision, and this is the case that stops it.
        ImmutableArray<Diagnostic> diagnostics = await RunConfiguredAsync(
            rule,
            $"""
            [*.cs]
            dotnet_analyzer_diagnostic.category-{otherCategory}.severity = none
            """,
            ViolationOf(rule));

        diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task The_category_switch_is_keyed_on_the_category_and_not_on_a_rule_id()
    {
        // The RENDLIO001 page says the category switch "also covers any later rule in it", which is a
        // promise about rules that do not exist yet. It can only be shown with a second rule in the
        // same category, so this uses a fixture one — carrying a different id, so a mechanism that
        // secretly matched on RENDLIO001 would leave it at error and fail here.
        ImmutableArray<Diagnostic> diagnostics = await AnalyzerHarness.RunConfiguredAsync(
            new SecondSecurityRuleFixture(),
            $"""
            [*.cs]
            dotnet_analyzer_diagnostic.category-{SecurityCategory}.severity = warning
            """,
            ReachesTheHost);

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe(SecondSecurityRuleFixture.Id);
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Warning);
    }

    // ---- Pragma, per rule ----

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task A_pragma_silences_the_span_it_covers(string rule)
    {
        // The one-call-site hatch, which is the one a page asks a consumer to reach for first: it
        // is the only suppression that leaves a comment next to the code explaining itself.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(rule, Disabled(ViolationOf(rule), rule));

        diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task A_pragma_stops_at_the_restore(string rule)
    {
        // What makes it the one-call-site hatch rather than a file-wide one. A pragma that leaked
        // past its restore would turn the narrowest suppression a consumer can write into the
        // broadest, and it would do it invisibly — the code below the restore looks covered by
        // nothing at all.
        string source = Disabled(ViolationOf(rule), rule) + Environment.NewLine + SecondViolationOf(rule);

        ImmutableArray<Diagnostic> diagnostics = await RunAsync(rule, source);

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe(rule);
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Error);

        // And it is the second one that survived, not the first: a pragma that covered the wrong
        // span would also leave exactly one diagnostic behind.
        Span(diagnostics[0], source).ShouldBe(SurvivingSpanOf(rule));
    }

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task A_pragma_for_another_rule_does_not_silence_this_one(string rule)
    {
        // Same promise as the severity case, through the mechanism a consumer is most likely to
        // copy from one file to another.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(rule, Disabled(ViolationOf(rule), "RENDLIO999"));

        diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    private static Task<ImmutableArray<Diagnostic>> RunAsync(string rule, string source) =>
        AnalyzerHarness.RunAsync(AnalyzerFor(rule), "Consumer", source);

    private static Task<ImmutableArray<Diagnostic>> RunConfiguredAsync(
        string rule,
        string editorConfig,
        string source) =>
        AnalyzerHarness.RunConfiguredAsync(AnalyzerFor(rule), editorConfig, source);

    private static DiagnosticAnalyzer AnalyzerFor(string rule) => rule switch
    {
        BannedApiRule => new BannedApiAnalyzer(),
        NonDeterminismRule => new NonDeterminismAnalyzer(),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No analyzer ships that rule."),
    };

    private static string ViolationOf(string rule) => rule switch
    {
        BannedApiRule => ReachesTheHost,
        NonDeterminismRule => ReadsAmbientState,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No analyzer ships that rule."),
    };

    /// <summary>
    /// A second violation of the same rule, as a standalone declaration that can be appended after a
    /// suppressed one. Deliberately a different API from <see cref="ViolationOf"/> so the surviving
    /// diagnostic can be told apart from the suppressed one by what it names.
    /// </summary>
    private static string SecondViolationOf(string rule) => rule switch
    {
        BannedApiRule => """
            internal static class Other
            {
                internal static object Reach() => typeof(System.Net.Http.HttpClient);
            }
            """,
        NonDeterminismRule => """
            internal static class Other
            {
                internal static object Draw() => new System.Random();
            }
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No analyzer ships that rule."),
    };

    /// <summary>
    /// The source text the diagnostic from <see cref="SecondViolationOf"/> sits on: the name that
    /// resolves to the banned type, which in a qualified reference is its last segment. Each of
    /// these appears exactly once in the combined source, so matching it identifies which of the two
    /// violations survived rather than merely that one did.
    /// </summary>
    private static string SurvivingSpanOf(string rule) => rule switch
    {
        BannedApiRule => "HttpClient",
        NonDeterminismRule => "Random",
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No analyzer ships that rule."),
    };

    /// <summary>Wraps <paramref name="source"/> in a disable/restore pair for <paramref name="rule"/>.</summary>
    private static string Disabled(string source, string rule) =>
        $"#pragma warning disable {rule}" + Environment.NewLine
        + source + Environment.NewLine
        + $"#pragma warning restore {rule}";

    private static string Span(Diagnostic diagnostic, string source) =>
        source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    /// <summary>
    /// A stand-in for a rule this pack has not published yet, in RENDLIO001's category. Exists only so
    /// the category switch can be shown to act on a category rather than on one id; it is never
    /// loaded by a consumer and reports on a syntax node that always exists.
    /// </summary>
    private static DiagnosticDescriptor Descriptor(string id) =>
        new(id, "Fixture rule", "Fixture rule", SecurityCategory, DiagnosticSeverity.Error,
            isEnabledByDefault: true, description: "A fixture rule in the shipped security category.",
            helpLinkUri: "https://example.invalid/owner/repo/blob/main/docs/rules/" + id + ".md");

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class SecondSecurityRuleFixture : DiagnosticAnalyzer
    {
        internal const string Id = "RENDLIO998";

        /// <summary>
        /// Held on the property rather than in a field of its own, the way the fixtures in
        /// <see cref="AnalyzerConventionTests"/> hold theirs: a <c>DiagnosticDescriptor</c> field in
        /// this project is one the release-tracking analyzer asks to see in a release note, and a
        /// fixture rule is exactly what must never appear in one.
        /// </summary>
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            [Descriptor(Id)];

        public override void Initialize(AnalysisContext context)
        {
            DiagnosticDescriptor rule = SupportedDiagnostics[0];

            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(
                GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.RegisterSyntaxNodeAction(
                node => node.ReportDiagnostic(Diagnostic.Create(rule, node.Node.GetLocation())),
                SyntaxKind.ClassDeclaration);
        }
    }
}
