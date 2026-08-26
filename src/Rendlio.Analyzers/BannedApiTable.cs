using Microsoft.CodeAnalysis;

namespace Rendlio.Analyzers;

/// <summary>
/// RENDLIO001's banned-API table, row by row. The <c>Reason</c> strings are what the diagnostic says
/// out loud, because the remedy differs per row — dropping the call, or moving the capability behind
/// something the caller supplies.
/// </summary>
/// <remarks>
/// The table is the whole rule: a ban that is not a row here does not exist. Adding a row turns code
/// that built yesterday red, so it is a consumer-visible change that belongs in a release note
/// rather than in a tidy-up.
/// </remarks>
internal static class BannedApiTable
{
    private const string NoProcesses = "no process spawning";
    private const string NoDynamicCode = "no dynamic code";
    private const string NoTypeNameReflection = "no reflection over input-derived type names";
    private const string NoNetwork = "zero network I/O; zero phone-home";

    /// <summary>
    /// The reason a native interop declaration is reported. Not a table row — a P/Invoke is not an
    /// API this rule resolves, it is a hole through which every row could be reached anyway.
    /// </summary>
    internal const string NativeInteropReason =
        "native interop can reach the network, the host and the loader, and no analyzer can see past it";

    /// <summary>Whole namespace trees: every type inside them is banned.</summary>
    private static readonly BannedNamespace[] _namespaces =
    [
        // No dynamic code: emitting IL at run time is writing code the build never saw.
        new BannedNamespace("System.Reflection.Emit", NoDynamicCode),

        // No network: the whole tree, not only Sockets, Http, WebRequest and Dns. WebRequest and
        // Dns live directly in System.Net, so the root namespace is banned alongside its children.
        new BannedNamespace("System.Net", NoNetwork),
    ];

    /// <summary>Single types: every reference to them is banned, members included.</summary>
    private static readonly BannedType[] _types =
    [
        // No processes: every member, Process.Start included.
        new BannedType("System.Diagnostics.Process", NoProcesses),
    ];

    /// <summary>Individual members of types that are otherwise legal.</summary>
    private static readonly BannedMember[] _members =
    [
        // Loading an assembly is loading code the build never saw. No parameter shape is named, so
        // every overload of each is banned.
        new BannedMember("System.Reflection.Assembly", "Load", stringFirstParameterOnly: false, NoDynamicCode),
        new BannedMember("System.Reflection.Assembly", "LoadFrom", stringFirstParameterOnly: false, NoDynamicCode),
        new BannedMember("System.Reflection.Assembly", "LoadFile", stringFirstParameterOnly: false, NoDynamicCode),

        // The rest of AssemblyLoadContext is not banned, so this is a member row rather than a type
        // row: naming the context is fine, feeding it bytes is not.
        new BannedMember("System.Runtime.Loader.AssemblyLoadContext", "LoadFromStream", stringFirstParameterOnly: false, NoDynamicCode),

        // The parameter shape is part of the row: reflection over a type *name* is the hazard,
        // because a name can come out of untrusted input. The overloads that take an
        // already-resolved Type are not banned.
        new BannedMember("System.Activator", "CreateInstance", stringFirstParameterOnly: true, NoTypeNameReflection),
        new BannedMember("System.Type", "GetType", stringFirstParameterOnly: true, NoTypeNameReflection),
    ];

    /// <summary>
    /// Whether the type is banned outright, either as a type row or because it lives in a banned
    /// namespace tree.
    /// </summary>
    /// <param name="type">The referenced type.</param>
    /// <param name="reason">The rationale for the matching row.</param>
    internal static bool IsBannedType(ITypeSymbol type, out string reason)
    {
        string? containing = type.ContainingNamespace?.ToDisplayString();
        foreach (BannedNamespace banned in _namespaces)
        {
            if (SymbolFacts.IsWithinNamespace(containing, banned.Name))
            {
                reason = banned.Reason;
                return true;
            }
        }

        string name = SymbolFacts.FullName(type);
        foreach (BannedType banned in _types)
        {
            if (string.Equals(name, banned.Name, StringComparison.Ordinal))
            {
                reason = banned.Reason;
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Whether the member is a member row.
    /// </summary>
    /// <param name="symbol">The referenced member.</param>
    /// <param name="display">The banned member's fully-qualified name, for the diagnostic message.</param>
    /// <param name="reason">The rationale for the matching row.</param>
    internal static bool IsBannedMember(ISymbol symbol, out string display, out string reason)
    {
        string? containing = symbol.ContainingType is { } type ? SymbolFacts.FullName(type) : null;

        foreach (BannedMember banned in _members)
        {
            if (!string.Equals(symbol.Name, banned.Member, StringComparison.Ordinal)
                || !string.Equals(containing, banned.ContainingType, StringComparison.Ordinal)
                || (banned.StringFirstParameterOnly && !SymbolFacts.HasStringFirstParameter(symbol)))
            {
                continue;
            }

            display = banned.ContainingType + "." + banned.Member;
            reason = banned.Reason;
            return true;
        }

        display = string.Empty;
        reason = string.Empty;
        return false;
    }

    private readonly struct BannedNamespace
    {
        internal BannedNamespace(string name, string reason)
        {
            Name = name;
            Reason = reason;
        }

        internal string Name { get; }

        internal string Reason { get; }
    }

    private readonly struct BannedType
    {
        internal BannedType(string name, string reason)
        {
            Name = name;
            Reason = reason;
        }

        internal string Name { get; }

        internal string Reason { get; }
    }

    private readonly struct BannedMember
    {
        internal BannedMember(string containingType, string member, bool stringFirstParameterOnly, string reason)
        {
            ContainingType = containingType;
            Member = member;
            StringFirstParameterOnly = stringFirstParameterOnly;
            Reason = reason;
        }

        internal string ContainingType { get; }

        internal string Member { get; }

        internal bool StringFirstParameterOnly { get; }

        internal string Reason { get; }
    }
}
