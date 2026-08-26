using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Rendlio.Analyzers;

/// <summary>
/// RENDLIO002. Fails the build on the three ambient APIs whose result depends on when or where the
/// code ran: <c>DateTime.Now</c>, <c>System.Random</c> (<c>Random.Shared</c> and <c>new Random()</c>
/// included) and <c>Guid.NewGuid</c>.
/// </summary>
/// <remarks>
/// <para><strong>The list is closed, and shorter than "no clock".</strong> Three APIs, named.
/// <c>DateTime.UtcNow</c>, <c>DateTimeOffset.Now</c>, <c>Environment.TickCount</c> and the rest are
/// NOT on it. Adding one is a consumer-visible change — a ban at severity error turns code that
/// built yesterday red — so it belongs in a release note rather than in a quiet extra entry
/// here.</para>
/// <para><strong><c>Stopwatch</c> and <c>TimeProvider</c> are required, not tolerated.</strong>
/// Measuring elapsed time off a monotonic counter is how a timeout or a budget is written, and it
/// does not change what the code produces: two runs measure different durations and still emit the
/// same bytes. Neither type appears below, and neither may.</para>
/// <para>Detection is semantic, not textual — a project's own <c>Random</c> type or a property
/// called <c>Now</c> is legitimate, so every name is bound and the resolved symbol compared.
/// Reporting follows RENDLIO001's convention: a banned <em>type</em> carries the diagnostic for its
/// own members, so <c>Random.Shared.Next()</c> is one error on <c>Random</c> rather than
/// three.</para>
/// <para>Scope is the package reference, as it is for RENDLIO001 — see
/// <see cref="BannedApiAnalyzer"/>.</para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NonDeterminismAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// A type row rather than a member row: every way to reach a random number —
    /// <c>new Random()</c>, <c>Random.Shared</c>, a <c>Random</c> field — names the type.
    /// </summary>
    private const string RandomType = "System.Random";

    /// <summary>
    /// Member rows, because <c>DateTime</c> and <c>Guid</c> are themselves ordinary value types used
    /// freely: a date read out of a file is exactly as deterministic as the file, and
    /// <c>Guid.Parse</c> reads an id that was already there.
    /// </summary>
    private static readonly AmbientMember[] _bannedMembers =
    [
        new AmbientMember("System.DateTime", "Now"),
        new AmbientMember("System.Guid", "NewGuid"),
    ];

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(RendlioRules.NonDeterminism);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.EnableConcurrentExecution();

        // As in RENDLIO001: generated code is analysed, because a rule a generator can drive through
        // is not a rule.
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterSyntaxNodeAction(AnalyzeName, SyntaxKind.IdentifierName, SyntaxKind.GenericName);
    }

    private static void AnalyzeName(SyntaxNodeAnalysisContext context)
    {
        var node = (SimpleNameSyntax)context.Node;
        if (SymbolFacts.IsIgnoredNameReference(node))
        {
            return;
        }

        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol;

        // Namespaces are never reported: reaching System.Random means naming the type, and that
        // name is the diagnostic.
        if (symbol is null || symbol.Kind == SymbolKind.Namespace)
        {
            return;
        }

        if (symbol is ITypeSymbol type)
        {
            if (IsBannedType(type))
            {
                Report(context, node, SymbolFacts.FullName(type));
            }

            return;
        }

        // Locals, parameters and a project's own members have no containing type to match a row
        // against, so nothing below can apply to them.
        if (symbol.ContainingType is not { } containing)
        {
            return;
        }

        // See the remarks: the type reference carries the diagnostic for its members, so
        // Random.Shared and Random.Next are not reported a second time.
        if (IsBannedType(containing))
        {
            return;
        }

        string containingName = SymbolFacts.FullName(containing);
        foreach (AmbientMember banned in _bannedMembers)
        {
            if (string.Equals(symbol.Name, banned.Member, StringComparison.Ordinal)
                && string.Equals(containingName, banned.ContainingType, StringComparison.Ordinal))
            {
                Report(context, node, banned.ContainingType + "." + banned.Member);
                return;
            }
        }
    }

    private static bool IsBannedType(ITypeSymbol type) =>
        string.Equals(SymbolFacts.FullName(type), RandomType, StringComparison.Ordinal);

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, string api) =>
        context.ReportDiagnostic(Diagnostic.Create(
            RendlioRules.NonDeterminism,
            node.GetLocation(),
            "'" + api + "'"));

    private readonly struct AmbientMember
    {
        internal AmbientMember(string containingType, string member)
        {
            ContainingType = containingType;
            Member = member;
        }

        internal string ContainingType { get; }

        internal string Member { get; }
    }
}
