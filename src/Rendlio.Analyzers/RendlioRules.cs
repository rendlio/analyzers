using Microsoft.CodeAnalysis;

namespace Rendlio.Analyzers;

/// <summary>
/// The descriptors this pack ships, in one place so their ids, categories and severities can be
/// read side by side rather than hunted through the analyzers that report them.
/// </summary>
/// <remarks>
/// <para>Ids are family-scoped — <c>RENDLIO</c> plus three digits — and are never reused for a
/// different rule, so a suppression written today keeps meaning what its author meant by it. An id
/// belonging to a rule this pack does not ship stays unused rather than being recycled.</para>
/// <para>Every string here is read by strangers in their own build logs, so none of it may cite a
/// specification, tracker or repository the reader cannot open. The test suite enforces that.</para>
/// </remarks>
internal static class RendlioRules
{
    /// <summary>
    /// Where a rule's own page lives: one page per rule, named for its id. Repeated from
    /// <c>Directory.Build.props</c>, which is where the package metadata takes the same URL from;
    /// the test suite reads both and fails if they part company.
    /// </summary>
    private const string HelpLinkPrefix = "https://github.com/Rendlio/analyzers/blob/main/docs/rules/";

    /// <summary>
    /// RENDLIO001's category. Reaching the network, spawning a process or loading code are all
    /// ways out of the same box, so they share one category and one switch.
    /// </summary>
    internal const string SecurityCategory = "Rendlio.Security";

    /// <summary>
    /// RENDLIO002's category, deliberately not <see cref="SecurityCategory"/>. A category is the
    /// axis a consumer configures in bulk, and wanting reproducible output is not the same want as
    /// wanting a sealed box — the two must not be switchable as one.
    /// </summary>
    internal const string DeterminismCategory = "Rendlio.Determinism";

    internal const string BannedApiId = "RENDLIO001";
    internal const string NonDeterminismId = "RENDLIO002";

    /// <summary>
    /// The banned-API table (<see cref="BannedApiTable"/>) and the P/Invoke rule that closes the
    /// same door natively. Argument 0 names the API (or the declaration), argument 1 gives the
    /// reason it is banned — the message has to say why, because the remedy differs per row.
    /// </summary>
    internal static readonly DiagnosticDescriptor BannedApi = new(
        BannedApiId,
        title: "Banned API",
        messageFormat: "{0} is banned in this project — {1}",
        SecurityCategory,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Bans process spawning, dynamic code loading, reflection over type names and "
            + "network I/O, plus the native interop declarations that would let any of them back "
            + "in through a library. Code that must not reach the network or the host cannot be "
            + "held to that by review alone.",
        helpLinkUri: HelpLinkPrefix + BannedApiId + ".md");

    /// <summary>
    /// The three ambient APIs whose result depends on when or where the build ran. Argument 0 names
    /// the one that was reached. The message does not prescribe the replacement: injecting the
    /// value and deriving it from the input are both answers, and which one fits is the caller's
    /// decision.
    /// </summary>
    internal static readonly DiagnosticDescriptor NonDeterminism = new(
        NonDeterminismId,
        title: "Non-deterministic API",
        messageFormat: "{0} makes output vary between runs and is banned in this project — inject "
            + "the value or derive it from the input",
        DeterminismCategory,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Bans three ambient APIs — DateTime.Now, System.Random (Random.Shared "
            + "included) and Guid.NewGuid — so that the same input produces the same output on "
            + "every run and on every machine. Measuring elapsed time is not on the list: "
            + "Stopwatch, TimeProvider, DateTime.UtcNow and DateTimeOffset stay legal, because "
            + "reading a duration does not change what the code produces.",
        helpLinkUri: HelpLinkPrefix + NonDeterminismId + ".md");
}
