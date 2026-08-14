using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// The ways out, as the rule pages under <c>docs/rules</c> write them down: a <c>#pragma</c> around
/// one call site, a <c>[SuppressMessage]</c> on a declaration or naming one from a
/// <c>GlobalSuppressions.cs</c>, a severity in <c>.editorconfig</c>, a <c>NoWarn</c> in the project
/// file, and a category switch back in the <c>.editorconfig</c>.
/// </summary>
/// <remarks>
/// <para>These rules report at severity error by default, which makes the escape hatch part of the
/// contract rather than a footnote. A consumer who hits a false positive on a Friday needs the
/// documented suppression to work on the first try, and the cost of it not working is not a wrong
/// diagnostic — it is a build they cannot get green and a package they remove.</para>
/// <para>So each case drives the mechanism a page publishes, in the spelling that page publishes it
/// in, through the same <see cref="AnalyzerConfig"/> and
/// <see cref="CompilationOptions.SpecificDiagnosticOptions"/> machinery a real build uses. A
/// documented route that stops working fails here rather than in a stranger's build log.</para>
/// <para>What these cases do not do is read the pages. The config lines are rebuilt from a format
/// string over the rule id, so what is pinned here is that each mechanism behaves as documented; that
/// the pages spell the ids and categories correctly is pinned separately, by
/// <c>Every_suppression_a_published_page_shows_names_something_this_pack_ships</c>.</para>
/// <para>The other half is what suppression must NOT do. A category switch names a category, so it
/// has to leave the other category alone — the two are separate on purpose, because wanting
/// reproducible output is not the same want as wanting a sealed box — a pragma names a span, so it
/// has to stop at the restore, and an attribute names a declaration, so it has to stop at the one
/// it covers.</para>
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
        // is the narrowest of them, covering a span rather than a whole declaration, with the
        // reason it gives sitting next to the code it excuses.
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

    // ---- SuppressMessage, per rule ----

    [Theory]
    [MemberData(nameof(EveryRuleAndCategory))]
    public async Task An_attribute_on_the_declaration_silences_the_rule(string rule, string category)
    {
        // The mechanism an IDE reaches for on the consumer's behalf: "Suppress → in Source" writes
        // this, so it is the hatch a reader is most likely to end up using without ever having read
        // a page. It went undocumented and unpinned until now, which is the worst combination —
        // widely used, and free to stop working unnoticed.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(rule, Attributed(rule, category, rule));

        diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryRuleAndCategory))]
    public async Task An_attribute_stops_at_the_declaration_it_sits_on(string rule, string category)
    {
        // What makes it a one-declaration hatch rather than a file-wide one, and the reason the
        // pages place it a step wider than a pragma rather than beside it: it covers the whole
        // member it sits on, and nothing past that member.
        string source = Attributed(rule, category, rule) + Environment.NewLine + SecondViolationOf(rule);

        ImmutableArray<Diagnostic> diagnostics = await RunAsync(rule, source);

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe(rule);
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Error);

        // The second violation is the one that survived, as in the pragma case: an attribute that
        // covered the wrong declaration would also leave exactly one diagnostic behind.
        Span(diagnostics[0], source).ShouldBe(SurvivingSpanOf(rule));
    }

    [Theory]
    [MemberData(nameof(EveryRuleAndCategory))]
    public async Task An_attribute_on_the_type_covers_the_members_inside_it(string rule, string category)
    {
        // The other half of what the index promises the attribute covers — "the whole member or
        // type it sits on". A reader who puts it on the type is not reaching for a sixth mechanism,
        // but they are leaning on a second claim, and it is the one the member cases cannot reach:
        // every case above splices the attribute onto the member carrying the violation, so all of
        // them would still pass if the attribute only ever covered the declaration it sat on.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(rule, AttributedType(rule, category, rule));

        diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryRuleAndCategory))]
    public async Task An_attribute_on_the_type_stops_at_that_type(string rule, string category)
    {
        // And it stops there. This is the direction that would hurt: an attribute reaching past its
        // type would be the broadest suppression on the page wearing the label of one of the
        // narrowest, and the code it silently covered would carry no sign of it — which is the
        // failure the pragma's restore is guarded against, one declaration up.
        string source = AttributedType(rule, category, rule)
            + Environment.NewLine
            + SecondViolationOf(rule);

        ImmutableArray<Diagnostic> diagnostics = await RunAsync(rule, source);

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe(rule);
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Error);

        // The survivor is the violation in the sibling type, not the covered one.
        Span(diagnostics[0], source).ShouldBe(SurvivingSpanOf(rule));
    }

    [Theory]
    [MemberData(nameof(EveryRuleAndCategory))]
    public async Task An_attribute_for_another_rule_does_not_silence_this_one(string rule, string category)
    {
        // Same family-scoping promise as the severity, pragma and NoWarn cases. This is the
        // spelling where getting it wrong is quietest: a mistyped id here is a compiling attribute
        // sitting in plain sight on the member, which reads exactly like a suppression that works.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(rule, Attributed(rule, category, "RENDLIO999"));

        diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task An_attribute_matches_on_the_id_and_not_on_the_category_beside_it(string rule)
    {
        // Pinned because the pages say it. The category argument is documentation for whoever reads
        // the attribute, not a second thing that has to match, and the asymmetry is worth stating:
        // a consumer who mistypes the category is suppressed anyway and never finds out, while one
        // who mistypes the id is not suppressed at all and is told nothing either. Only one of
        // those two costs them a red build, so only one of them gets discovered — which is exactly
        // why the pages point at the id as the part to get right.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(
            rule,
            Attributed(rule, "Not.A.Category.This.Pack.Ships", rule));

        diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryRuleAndCategory))]
    public async Task An_assembly_scoped_attribute_silences_the_member_it_names(string rule, string category)
    {
        // The same attribute written into a GlobalSuppressions.cs, which is the file an IDE creates
        // for it. A separate source rather than a prelude to the violation, because that is where a
        // consumer's copy lives — and because an assembly attribute cannot follow the file-scoped
        // namespace the fixtures declare anyway.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(
            rule,
            ViolationOf(rule),
            GlobalSuppressions(category, rule, TargetOf(rule)));

        diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryRuleAndCategory))]
    public async Task An_assembly_scoped_attribute_stops_covering_a_member_that_was_renamed(
        string rule,
        string category)
    {
        // The cost the index gives this spelling, made concrete. Its subject is a string rather
        // than the member it sits on, so a rename severs the two with nothing in the compiler
        // objecting — and what a consumer sees is the error returning in code they did not touch.
        // Pinned as the SAFE direction it is: the alternative, a stale target quietly matching
        // something else, would be the one worth being frightened of.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(
            rule,
            ViolationOf(rule),
            GlobalSuppressions(category, rule, "~M:Example.Sut.Renamed"));

        diagnostics.ShouldHaveSingleItem().Id.ShouldBe(rule);
        diagnostics[0].Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    // ---- NoWarn, per rule ----

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task NoWarn_silences_the_rule(string rule)
    {
        // The project-file spelling, and the least obvious way out on the page: NoWarn reads as a
        // switch about *warnings*, and these rules report at error. It is named in the triage
        // policy as part of the contract, so what it does to an error-severity rule is pinned here
        // rather than left for a consumer to discover in a build they cannot get green.
        ImmutableArray<Diagnostic> diagnostics = await RunNoWarnAsync(rule, rule, ViolationOf(rule));

        diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task NoWarn_for_another_rule_leaves_this_one_alone(string rule)
    {
        // Same family-scoping promise as the severity, pragma and attribute cases, through the
        // mechanism furthest from the code it acts on: a NoWarn is invisible at the call site, so
        // a rule answering to an id that is not its own would be near-impossible to spot from the
        // source.
        ImmutableArray<Diagnostic> diagnostics = await RunNoWarnAsync(rule, "RENDLIO999", ViolationOf(rule));

        diagnostics.ShouldHaveSingleItem().Severity.ShouldBe(DiagnosticSeverity.Error);
    }

    [Theory]
    [MemberData(nameof(EveryRule))]
    public async Task NoWarn_takes_the_whole_list_it_is_given(string rule)
    {
        // NoWarn is a list, and the spelling the pages publish appends to the inherited value — so
        // the id that matters arrives beside ids belonging to other packs, and typically not first.
        // A mechanism that only honoured the head of the list would pass every case above.
        ImmutableArray<Diagnostic> diagnostics = await RunNoWarnAsync(
            rule,
            $"CS1591;RENDLIO999;{rule}",
            ViolationOf(rule));

        diagnostics.ShouldBeEmpty();
    }

    private static Task<ImmutableArray<Diagnostic>> RunAsync(string rule, params string[] sources) =>
        AnalyzerHarness.RunAsync(AnalyzerFor(rule), "Consumer", sources);

    private static Task<ImmutableArray<Diagnostic>> RunConfiguredAsync(
        string rule,
        string editorConfig,
        string source) =>
        AnalyzerHarness.RunConfiguredAsync(AnalyzerFor(rule), editorConfig, source);

    private static Task<ImmutableArray<Diagnostic>> RunNoWarnAsync(
        string rule,
        string noWarn,
        string source) =>
        AnalyzerHarness.RunNoWarnAsync(AnalyzerFor(rule), noWarn, source);

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

    /// <summary>
    /// The member each fixture declares the violation in, as it is spelled in the source. Matched
    /// on rather than assumed so <see cref="Attributed"/> can splice an attribute above it, and
    /// distinct from the declaration <see cref="SecondViolationOf"/> adds so the splice cannot land
    /// on the wrong one.
    /// </summary>
    private static string DeclarationOf(string rule) => rule switch
    {
        BannedApiRule => "    internal static void Run()",
        NonDeterminismRule => "    internal static string Name()",
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No analyzer ships that rule."),
    };

    /// <summary>
    /// The documentation-comment id of that member, which is what the <c>Target</c> of an
    /// assembly-scoped attribute names. The leading <c>~</c> is the spelling an IDE writes into a
    /// <c>GlobalSuppressions.cs</c>, and the one the pages publish.
    /// </summary>
    private static string TargetOf(string rule) => rule switch
    {
        BannedApiRule => "~M:Example.Sut.Run",
        NonDeterminismRule => "~M:Example.Sut.Name",
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "No analyzer ships that rule."),
    };

    /// <summary>
    /// The type each fixture declares that member on. Spelled the same way in both, so unlike
    /// <see cref="DeclarationOf"/> it does not need to be chosen per rule — and distinct from the
    /// type <see cref="SecondViolationOf"/> adds, so a splice onto it cannot land on that one.
    /// </summary>
    private const string TypeDeclaration = "internal static class Sut";

    /// <summary>
    /// The violation of <paramref name="rule"/> with a <c>[SuppressMessage]</c> naming
    /// <paramref name="category"/> and <paramref name="checkId"/> on the member declaring it.
    /// </summary>
    private static string Attributed(string rule, string category, string checkId) =>
        Spliced(
            rule,
            DeclarationOf(rule),
            $"""
                [SuppressMessage("{category}", "{checkId}",
                    Justification = "{Justification}")]
            """);

    /// <summary>
    /// The same violation with the same attribute on the type declaring that member, which is the
    /// other thing the index says the attribute can sit on.
    /// </summary>
    private static string AttributedType(string rule, string category, string checkId) =>
        Spliced(
            rule,
            TypeDeclaration,
            $"""
            [SuppressMessage("{category}", "{checkId}",
                Justification = "{Justification}")]
            """);

    /// <summary>
    /// The violation of <paramref name="rule"/> with <paramref name="attribute"/> spliced in on the
    /// line above <paramref name="anchor"/>, and the namespace it needs imported at the top.
    /// </summary>
    /// <remarks>
    /// Spliced in rather than written into a fixture of its own, so the suppressed source and the
    /// unsuppressed one cannot drift apart into two snippets that differ in more than the attribute.
    /// <para>
    /// The import is unqualified rather than spelled out in full, because that is what the pages
    /// publish — and because it is the first thing that can go wrong with a pasted attribute.
    /// Compiling the qualified form would prove the mechanism and skip the paste.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The fixture no longer contains <paramref name="anchor"/>. Fatal rather than a silent no-op:
    /// a splice that quietly did nothing would leave the cases asserting the rule goes quiet
    /// failing loudly, but the ones asserting it still reports passing with no attribute in the
    /// source at all.
    /// </exception>
    private static string Spliced(string rule, string anchor, string attribute)
    {
        string source = ViolationOf(rule);

        string attributed = source.Replace(
            anchor,
            attribute + Environment.NewLine + anchor,
            StringComparison.Ordinal);

        return string.Equals(attributed, source, StringComparison.Ordinal)
            ? throw new InvalidOperationException(
                $"The {rule} fixture no longer contains '{anchor}', so no attribute was applied.")
            : Import + Environment.NewLine + attributed;
    }

    /// <summary>
    /// A <c>GlobalSuppressions.cs</c> naming one member, in the spelling the pages publish.
    /// </summary>
    private static string GlobalSuppressions(string category, string checkId, string target) =>
        $"""
        {Import}

        [assembly: SuppressMessage("{category}", "{checkId}",
            Justification = "{Justification}",
            Scope = "member", Target = "{target}")]
        """;

    /// <summary>The namespace the attribute lives in, as both spellings of it need importing.</summary>
    private const string Import = "using System.Diagnostics.CodeAnalysis;";

    /// <summary>
    /// What the reason reads as. The attribute's whole advantage over a pragma is that this is an
    /// argument rather than a comment, so every case that drives one carries it.
    /// </summary>
    private const string Justification = "A worked example, in a fixture that never ships.";

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
