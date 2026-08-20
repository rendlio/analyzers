using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds the shipping pack to <see cref="AnalyzerConventions"/>, and holds
/// <see cref="AnalyzerConventions"/> itself to a set of fixtures that break each convention
/// on purpose.
/// </summary>
public sealed class AnalyzerConventionTests
{
    /// <summary>The package whose pinned version is the compiler floor this pack claims.</summary>
    private const string CompilerPackage = "Microsoft.CodeAnalysis.CSharp";

    /// <summary>
    /// The pack as it is actually built, loaded by name rather than through a type handle —
    /// the assembly is expected to be empty of rules until they are synced in, and an empty
    /// assembly offers no type to anchor a reference on.
    /// </summary>
    private static Assembly ShippingAssembly => Assembly.Load("Rendlio.Analyzers");

    [Fact]
    public void Shipping_assembly_loads()
    {
        Assert.NotNull(ShippingAssembly);
    }

    [Fact]
    public void Shipping_assembly_targets_netstandard20()
    {
        // A Roslyn host may be .NET Framework (Visual Studio) or .NET (dotnet build);
        // netstandard2.0 is the only target both can load.
        TargetFrameworkAttribute? target = ShippingAssembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.NotNull(target);
        Assert.Equal(".NETStandard,Version=v2.0", target.FrameworkName);
    }

    [Fact]
    public void Shipping_assembly_binds_no_compiler_newer_than_the_declared_floor()
    {
        // The other half of the rule above. netstandard2.0 settles which runtime can load this
        // assembly; the Roslyn version it binds against settles which compiler host can.
        // Directory.Packages.props pins Microsoft.CodeAnalysis.CSharp deliberately below the
        // version the engine repository builds on, and calls raising that pin a consumer-visible
        // breaking change — a claim that until now lived only in a comment, inside the very file
        // someone edits in order to raise it.
        //
        // Read off the built assembly rather than off the project files, because the binding is
        // what a host actually resolves: a PackageReference added without a central version, or a
        // transitive bump reaching the compile, moves the floor without touching the pin.
        Version floor = DeclaredCompilerFloor();

        var compilerReferences = ShippingAssembly.GetReferencedAssemblies()
            .Where(reference => reference.Name is not null
                && reference.Name.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            .ToList();

        // Guards the guard: a rule about Roslyn references passes over an empty set the moment this
        // assembly stops referencing Roslyn, which for an analyzer means it stopped being one.
        Assert.NotEmpty(compilerReferences);

        foreach (AssemblyName reference in compilerReferences)
        {
            Assert.NotNull(reference.Version);

            // Compared on three components: an assembly version always carries a fourth and a
            // package version never does, so 4.8.0.0 would otherwise read as above 4.8.0.
            var bound = new Version(
                reference.Version.Major,
                reference.Version.Minor,
                reference.Version.Build);

            Assert.True(
                bound <= floor,
                $"{reference.Name} is bound at {bound}, above the {floor} floor Directory.Packages.props pins. Every compiler host older than the new floor stops loading this pack, so raising it is a breaking change for consumers rather than a dependency bump.");
        }
    }

    [Fact]
    public void Every_shipped_rule_meets_the_conventions()
    {
        // Vacuous while the pack carries no rules, and live the moment it carries one —
        // which is the point: the guard is in place before the rules arrive, not after.
        IReadOnlyList<string> violations =
            AnalyzerConventions.Inspect(AnalyzerConventions.AnalyzersIn(ShippingAssembly));

        Assert.Empty(violations);
    }

    [Fact]
    public void Guard_accepts_a_compliant_analyzer()
    {
        Assert.Empty(AnalyzerConventions.Inspect([typeof(CompliantAnalyzer)]));
    }

    [Theory]
    [InlineData(typeof(UnattributedAnalyzer), "missing [DiagnosticAnalyzer]")]
    [InlineData(typeof(SilentAnalyzer), "reports no diagnostics")]
    [InlineData(typeof(ForeignIdAnalyzer), "is not of the form RENDLIO000")]
    [InlineData(typeof(UndocumentedAnalyzer), "HelpLinkUri")]
    [InlineData(typeof(InternallyWordedAnalyzer), "means nothing outside")]
    [InlineData(typeof(UnconstructableAnalyzer), "public parameterless constructor")]
    public void Guard_rejects_a_non_compliant_analyzer(Type analyzer, string expected)
    {
        IReadOnlyList<string> violations = AnalyzerConventions.Inspect([analyzer]);

        Assert.Contains(violations, v => v.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Guard_rejects_two_different_descriptors_sharing_one_id()
    {
        IReadOnlyList<string> violations =
            AnalyzerConventions.Inspect([typeof(CompliantAnalyzer), typeof(IdSquatterAnalyzer)]);

        Assert.Contains(violations, v => v.Contains("already used by a different descriptor", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void Guard_reads_descriptor_text_the_same_under_any_culture(string culture)
    {
        // A consumer's build runs under their locale, not ours, and these conventions read
        // descriptor text through it. What is pinned here is that the ambient culture cannot change
        // what the guard reports — not any single flag: the patterns are source-generated, so their
        // case tables are already fixed at compile time. The hazard this guards is a later rewrite
        // to culture-sensitive matching or casing, which is live rather than theoretical — under
        // tr-TR, "DOCS/INTERNAL".ToLower() is "docs/ınternal", and a runtime Regex built with
        // IgnoreCase alone stops matching the shouted reference the fixture below carries.
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            var target = new CultureInfo(culture);
            CultureInfo.CurrentCulture = target;
            CultureInfo.CurrentUICulture = target;

            Assert.Empty(AnalyzerConventions.Inspect([typeof(CompliantAnalyzer)]));
            Assert.Contains(
                AnalyzerConventions.Inspect([typeof(ShoutingInternalReferenceAnalyzer)]),
                v => v.Contains("means nothing outside", StringComparison.Ordinal));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    /// <summary>
    /// The compiler version this pack is pinned to build against, read from the one file that
    /// decides it — so the floor cannot be raised in one place and asserted from another.
    /// </summary>
    private static Version DeclaredCompilerFloor()
    {
        var props = XDocument.Load(
            Path.Combine(RepositoryLayout.Root, "Directory.Packages.props"));

        string pinned = props.Descendants("PackageVersion")
            .Where(element => element.Attribute("Include")?.Value == CompilerPackage)
            .Select(element => element.Attribute("Version")?.Value)
            .FirstOrDefault(version => !string.IsNullOrEmpty(version))
            ?? throw new InvalidOperationException(
                $"Directory.Packages.props pins no {CompilerPackage} version, so the compiler floor this rule is about has no stated value to compare against.");

        // A prerelease suffix is not something an assembly version can express, and this pin has
        // never carried one; taking the release part keeps the comparison meaningful if it ever
        // does.
        return Version.Parse(pinned.Split('-')[0]);
    }

    // ------------------------------------------------------------------ fixtures
    // Deliberately non-compliant analyzers. They exist only so the conventions above are
    // proven to fail on something; none of them is ever registered with a compilation.

    /// <summary>
    /// Stand-in help link. Deliberately NOT the real repository URL: a fixture only needs some
    /// absolute URI, and hard-coding the live one here would silently couple the tests to a value
    /// that is still being confirmed. `.invalid` is reserved by RFC 2606 and can never resolve.
    /// </summary>
    private const string FixtureHelpLink = "https://example.invalid/rules/RENDLIO900";

    private static DiagnosticDescriptor Descriptor(
        string id,
        string helpLink = FixtureHelpLink,
        string message = "{0} is not allowed here",
        string description = "A fixture descriptor.") =>
        new(id, "Fixture rule", message, "Rendlio.Fixture", DiagnosticSeverity.Warning,
            isEnabledByDefault: true, description: description, helpLinkUri: helpLink);

    private abstract class FixtureAnalyzer : DiagnosticAnalyzer
    {
        public override void Initialize(AnalysisContext context)
        {
            // Never registered; the conventions only read SupportedDiagnostics.
        }
    }

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class CompliantAnalyzer : FixtureAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            [Descriptor("RENDLIO900")];
    }

    private sealed class UnattributedAnalyzer : FixtureAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            [Descriptor("RENDLIO901")];
    }

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class SilentAnalyzer : FixtureAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [];
    }

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class ForeignIdAnalyzer : FixtureAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            [Descriptor("XY7")];
    }

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class UndocumentedAnalyzer : FixtureAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            [Descriptor("RENDLIO902", helpLink: "")];
    }

    /// <summary>
    /// Cites a specification that does not exist anywhere — the guard keys off the *shape* of
    /// an internal reference, so the fixture must not smuggle in a real one to match against.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class InternallyWordedAnalyzer : FixtureAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            [Descriptor("RENDLIO903", message: "{0} is banned by FS-99 §1")];
    }

    /// <summary>
    /// Cannot be constructed the way Roslyn constructs analyzers, so the rule would never run.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class UnconstructableAnalyzer : FixtureAnalyzer
    {
        public UnconstructableAnalyzer(string reason)
        {
            Reason = reason;
        }

        public string Reason { get; }

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            [Descriptor("RENDLIO904")];
    }

    /// <summary>
    /// Shouts its internal reference. The uppercase is the whole point: it is exactly what a
    /// culture-sensitive case-insensitive match stops recognising under a Turkish locale.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class ShoutingInternalReferenceAnalyzer : FixtureAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            [Descriptor("RENDLIO905", message: "{0} is banned; see DOCS/INTERNAL/rules.md")];
    }

    /// <summary>Reuses RENDLIO900 for an unrelated rule.</summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    private sealed class IdSquatterAnalyzer : FixtureAnalyzer
    {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            [new("RENDLIO900", "A different rule", "{0}", "Rendlio.Other", DiagnosticSeverity.Error,
                isEnabledByDefault: true, description: "Another fixture.", helpLinkUri: FixtureHelpLink)];
    }
}
