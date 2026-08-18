using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// The conventions every rule in this pack has to satisfy, expressed as a function over
/// analyzer types rather than as one assertion per rule.
/// </summary>
/// <remarks>
/// Written as a reusable inspection so it applies to whatever the shipping assembly contains
/// at the time it runs — a rule cannot be added without meeting these, and no test has to be
/// edited when one is. <see cref="AnalyzerConventionTests"/> also runs it against deliberately
/// non-compliant fixtures, so the guard is proven to bite while the pack is still empty.
/// </remarks>
internal static partial class AnalyzerConventions
{
    /// <summary>Family-scoped ids: RENDLIO plus exactly three digits.</summary>
    [GeneratedRegex(@"^RENDLIO\d{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex RuleId();

    /// <summary>
    /// References that are meaningful only inside Rendlio's own repositories. A descriptor's
    /// title, message and description are shipped public text — they show up in a stranger's
    /// build log — so they must not cite internal specifications, tracker ids or repository
    /// paths that the reader has no access to.
    /// </summary>
    [GeneratedRegex(@"\bFS-\d{2}\b|\bURS\b|docs/internal|\bwi-[0-9a-f]{8}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InternalReference();

    /// <summary>Every concrete analyzer type declared in <paramref name="assembly"/>.</summary>
    internal static IReadOnlyList<Type> AnalyzersIn(Assembly assembly) =>
        assembly.GetTypes()
            .Where(t => typeof(DiagnosticAnalyzer).IsAssignableFrom(t) && !t.IsAbstract)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Returns one message per convention violation across <paramref name="analyzerTypes"/>, or
    /// an empty list when they all comply.
    /// </summary>
    internal static IReadOnlyList<string> Inspect(IEnumerable<Type> analyzerTypes)
    {
        var violations = new List<string>();
        var idOwners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Type type in analyzerTypes)
        {
            if (type.GetCustomAttribute<DiagnosticAnalyzerAttribute>() is null)
            {
                // Without the attribute the compiler never loads it, so the rule silently does nothing.
                violations.Add($"{type.Name}: missing [DiagnosticAnalyzer] attribute.");
                continue;
            }

            if (Activator.CreateInstance(type) is not DiagnosticAnalyzer instance)
            {
                violations.Add($"{type.Name}: needs a public parameterless constructor.");
                continue;
            }

            if (instance.SupportedDiagnostics.Length == 0)
            {
                violations.Add($"{type.Name}: reports no diagnostics.");
                continue;
            }

            foreach (DiagnosticDescriptor rule in instance.SupportedDiagnostics)
            {
                InspectDescriptor(type, rule, idOwners, violations);
            }
        }

        return violations;
    }

    private static void InspectDescriptor(
        Type owner,
        DiagnosticDescriptor rule,
        Dictionary<string, string> idOwners,
        List<string> violations)
    {
        if (!RuleId().IsMatch(rule.Id))
        {
            violations.Add($"{owner.Name}: rule id '{rule.Id}' is not of the form RENDLIO000.");
        }

        // Two analyzers may share a descriptor instance; two *different* descriptors may not
        // share an id, or a consumer's suppression would silence a rule they never read about.
        string fingerprint = $"{rule.Title}|{rule.Category}|{rule.DefaultSeverity}";
        if (idOwners.TryGetValue(rule.Id, out string? existing))
        {
            if (!string.Equals(existing, fingerprint, StringComparison.Ordinal))
            {
                violations.Add($"{owner.Name}: rule id '{rule.Id}' is already used by a different descriptor.");
            }
        }
        else
        {
            idOwners.Add(rule.Id, fingerprint);
        }

        if (string.IsNullOrWhiteSpace(rule.Title.ToString(CultureInfo.InvariantCulture)))
        {
            violations.Add($"{rule.Id}: empty title.");
        }

        if (string.IsNullOrWhiteSpace(rule.MessageFormat.ToString(CultureInfo.InvariantCulture)))
        {
            violations.Add($"{rule.Id}: empty message format.");
        }

        if (string.IsNullOrWhiteSpace(rule.Category))
        {
            violations.Add($"{rule.Id}: empty category.");
        }

        // The help link is this pack's per-rule documentation. A rule a stranger cannot look up
        // is a rule they will suppress rather than fix.
        if (!Uri.TryCreate(rule.HelpLinkUri, UriKind.Absolute, out Uri? helpLink)
            || (helpLink.Scheme != Uri.UriSchemeHttps && helpLink.Scheme != Uri.UriSchemeHttp))
        {
            violations.Add($"{rule.Id}: needs an absolute http(s) HelpLinkUri pointing at its documentation.");
        }

        foreach (string text in new[] { rule.Title.ToString(CultureInfo.InvariantCulture), rule.MessageFormat.ToString(CultureInfo.InvariantCulture), rule.Description.ToString(CultureInfo.InvariantCulture) })
        {
            Match internalReference = InternalReference().Match(text);
            if (internalReference.Success)
            {
                violations.Add($"{rule.Id}: shipped text cites '{internalReference.Value}', which means nothing outside Rendlio's own repositories.");
            }
        }
    }
}
