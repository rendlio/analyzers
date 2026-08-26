using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rendlio.Analyzers;

/// <summary>
/// The symbol and syntax predicates both rules need. Everything here works on names rather than on
/// resolved BCL symbols: a rule whose ban list is a table reads best as the same table, and matching
/// by name means a row still fires on a target framework where the type has moved assembly or is
/// absent from the reference set entirely.
/// </summary>
internal static class SymbolFacts
{
    /// <summary>
    /// The symbol's fully-qualified name without generic arguments — <c>System.Net.Http.HttpClient</c>,
    /// or <c>System.Collections.Generic.List&lt;T&gt;</c> for a constructed generic.
    /// </summary>
    internal static string FullName(ISymbol symbol) => symbol.OriginalDefinition.ToDisplayString();

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> or a namespace nested inside
    /// it. The separator check is what keeps a ban on <c>System.Net</c> off a hypothetical
    /// <c>System.Network</c>.
    /// </summary>
    internal static bool IsWithinNamespace(string? candidate, string root)
    {
        if (candidate is null)
        {
            return false;
        }

        if (string.Equals(candidate, root, StringComparison.Ordinal))
        {
            return true;
        }

        return candidate.Length > root.Length
            && candidate[root.Length] == '.'
            && candidate.StartsWith(root, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a name node is one these rules deliberately do not look at.
    /// </summary>
    /// <remarks>
    /// <para>Documentation comments: a <c>&lt;see cref="..."/&gt;</c> binds to a real symbol, so
    /// without this the doc comment explaining <em>why</em> an API is banned would itself be a use
    /// of it.</para>
    /// <para><c>var</c>: it binds to the inferred type, which for
    /// <c>var c = new HttpClient()</c> is the same banned type the initializer already names — two
    /// errors on one statement. The inference-only case (a <c>var</c> whose banned type is named
    /// nowhere in the statement) needs the value to come from somewhere, and that somewhere is
    /// either a banned API or a declaration in the analysed project whose own type reference is
    /// reported.</para>
    /// </remarks>
    internal static bool IsIgnoredNameReference(SimpleNameSyntax node) =>
        node is IdentifierNameSyntax { IsVar: true }
        || node.FirstAncestorOrSelf<DocumentationCommentTriviaSyntax>() is not null;

    /// <summary>
    /// Whether the method's first parameter is a <see cref="string"/> — the shape that separates
    /// <c>Activator.CreateInstance(string, ...)</c> and <c>Type.GetType(string)</c> (reflection over
    /// a type *name*, banned) from their overloads that take an already-resolved <c>Type</c> (not
    /// banned).
    /// </summary>
    internal static bool HasStringFirstParameter(ISymbol symbol) =>
        symbol is IMethodSymbol method
        && method.Parameters.Length > 0
        && method.Parameters[0].Type.SpecialType == SpecialType.System_String;
}
