using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Rendlio.Analyzers;

/// <summary>
/// RENDLIO001. Fails the build on any use of the banned-API table (<see cref="BannedApiTable"/>) and
/// on any <c>DllImport</c>/<c>LibraryImport</c> declaration.
/// </summary>
/// <remarks>
/// <para><strong>Scope is the package reference.</strong> The rule applies to every project that
/// references this package and to no other, so which code is held to it is decided where that
/// reference is written — not by anything the analyzer knows about the assembly it is looking at. A
/// project that legitimately needs one of these APIs drops the reference for that project, or sets
/// the rule's severity in its own <c>.editorconfig</c>.</para>
/// <para>Detection is semantic, never textual. "Process" is an ordinary word — a method called
/// <c>Process</c> or a parameter called <c>process</c> is legitimate — so the rule binds every name
/// it sees and compares the resolved symbol against the table. That is also what makes an alias
/// (<c>using P = System.Diagnostics.Process;</c>) or a fully-qualified call indistinguishable from a
/// plain one.</para>
/// <para>Reporting is anchored on the <em>type</em> reference, not on the member reached through it:
/// <c>Process.Start(…)</c> is one diagnostic on <c>Process</c>, not two. A member of a banned type is
/// therefore skipped — it is unreachable without the type being named somewhere in the project, and
/// that name is where the error belongs.</para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BannedApiAnalyzer : DiagnosticAnalyzer
{
    private const string DllImportAttribute = "System.Runtime.InteropServices.DllImportAttribute";
    private const string LibraryImportAttribute = "System.Runtime.InteropServices.LibraryImportAttribute";

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(RendlioRules.BannedApi);

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.EnableConcurrentExecution();

        // Generated code is analysed deliberately. A ban a generator can drive through is not a ban,
        // and a committed generated file is source like any other.
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterSyntaxNodeAction(AnalyzeName, SyntaxKind.IdentifierName, SyntaxKind.GenericName);

        // TWO dispatch paths for one construct, and both are load-bearing. Roslyn drives symbol
        // actions from a named type's declared members, so they never see a local function — and a
        // local `static extern` function carrying [DllImport] is a real P/Invoke, not dead syntax
        // (drop the attribute and the compiler demands it back with CS0626). With only the symbol
        // action registered, that spelling compiles clean. Do not collapse these into one.
        context.RegisterSymbolAction(AnalyzeNativeInterop, SymbolKind.Method);
        context.RegisterSyntaxNodeAction(AnalyzeLocalFunctionNativeInterop, SyntaxKind.LocalFunctionStatement);
    }

    private static void AnalyzeName(SyntaxNodeAnalysisContext context)
    {
        var node = (SimpleNameSyntax)context.Node;
        if (SymbolFacts.IsIgnoredNameReference(node))
        {
            return;
        }

        ISymbol? symbol = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol;

        // Namespaces are never reported. Reaching a banned namespace means naming a type inside it,
        // and that type reference is the diagnostic; an import on its own reaches nothing.
        if (symbol is null || symbol.Kind == SymbolKind.Namespace)
        {
            return;
        }

        if (symbol is ITypeSymbol type)
        {
            if (BannedApiTable.IsBannedType(type, out string typeReason))
            {
                Report(context, node, Quote(SymbolFacts.FullName(type)), typeReason);
            }

            return;
        }

        // See the remarks: the type reference carries the diagnostic for its members.
        if (symbol.ContainingType is { } containing && BannedApiTable.IsBannedType(containing, out _))
        {
            return;
        }

        if (BannedApiTable.IsBannedMember(symbol, out string member, out string memberReason))
        {
            Report(context, node, Quote(member), memberReason);
        }
    }

    /// <summary>Member methods, property accessors and <c>LibraryImport</c> partial declarations.</summary>
    private static void AnalyzeNativeInterop(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;

        // A LibraryImport declaration is a partial method, and Roslyn merges the attributes of both
        // halves onto BOTH symbols — so without this the same [LibraryImport] is reported twice at
        // the same span, once for the declaration the developer wrote and once for the part the
        // interop generator emitted. Report the defining half, which is the one in their source.
        // Local functions need no equivalent: they cannot be partial.
        if (method.PartialDefinitionPart is not null)
        {
            return;
        }

        if (NativeInteropDiagnostic(method) is { } diagnostic)
        {
            context.ReportDiagnostic(diagnostic);
        }
    }

    /// <summary>
    /// The spelling <see cref="AnalyzeNativeInterop"/> cannot reach. <c>LibraryImport</c> is absent
    /// by construction — it requires a partial method — so in practice this catches
    /// <c>[DllImport] static extern</c> local functions.
    /// </summary>
    private static void AnalyzeLocalFunctionNativeInterop(SyntaxNodeAnalysisContext context)
    {
        var node = (LocalFunctionStatementSyntax)context.Node;

        if (context.SemanticModel.GetDeclaredSymbol(node, context.CancellationToken) is IMethodSymbol method
            && NativeInteropDiagnostic(method) is { } diagnostic)
        {
            context.ReportDiagnostic(diagnostic);
        }
    }

    /// <summary>
    /// The diagnostic for a method that declares a P/Invoke, or null when it declares none. Shared
    /// so the two dispatch paths cannot drift into reporting different things for the same code.
    /// </summary>
    private static Diagnostic? NativeInteropDiagnostic(IMethodSymbol method)
    {
        foreach (AttributeData attribute in method.GetAttributes())
        {
            string? name = attribute.AttributeClass is { } attributeClass
                ? SymbolFacts.FullName(attributeClass)
                : null;

            bool isDllImport = string.Equals(name, DllImportAttribute, StringComparison.Ordinal);
            if (!isDllImport && !string.Equals(name, LibraryImportAttribute, StringComparison.Ordinal))
            {
                continue;
            }

            // One diagnostic per declaration: a method cannot carry both attributes meaningfully,
            // and repeating the same error on the same method helps nobody.
            return Diagnostic.Create(
                RendlioRules.BannedApi,
                NativeInteropLocation(attribute, method),
                isDllImport ? "A [DllImport] declaration" : "A [LibraryImport] declaration",
                BannedApiTable.NativeInteropReason);
        }

        return null;
    }

    /// <summary>
    /// The attribute's own span when it is in source, so the squiggle lands on <c>[DllImport(…)]</c>
    /// rather than on the whole method. A P/Invoke declared in a referenced assembly has no syntax
    /// in this compilation and cannot be reported at all.
    /// </summary>
    private static Location NativeInteropLocation(AttributeData attribute, IMethodSymbol method)
    {
        if (attribute.ApplicationSyntaxReference is { } reference)
        {
            return Location.Create(reference.SyntaxTree, reference.Span);
        }

        return method.Locations.Length > 0 ? method.Locations[0] : Location.None;
    }

    private static void Report(SyntaxNodeAnalysisContext context, SyntaxNode node, string api, string reason) =>
        context.ReportDiagnostic(Diagnostic.Create(RendlioRules.BannedApi, node.GetLocation(), api, reason));

    private static string Quote(string name) => "'" + name + "'";
}
