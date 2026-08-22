using System.Text.RegularExpressions;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Reads the actions a GitHub Actions workflow runs, and reports the ones whose reference is not
/// a commit SHA.
/// </summary>
/// <remarks>
/// A <c>uses:</c> reference names someone else's code and a point in its history. Written as a tag
/// — <c>actions/checkout@v4</c> — the second half is a pointer its owner can move whenever they
/// like, so the code that runs here can change with no diff in this repository, no release of its
/// own, and nothing for anyone to review. Written as a commit SHA it cannot: the reference is the
/// tree that was actually read.
/// <para>
/// The tag does not stop being useful, it stops being the reference. It moves to a trailing
/// comment, where it records which version the SHA was when someone chose it and a reader does not
/// have to resolve forty hex characters to know what they are looking at. Both halves are required
/// here for that reason: a bare SHA is safe and unreadable, and unreadable pins are the ones that
/// get tidied back into tags.
/// </para>
/// <para>
/// Held as a test rather than left as a note in the workflow header, because a convention about
/// one line in one file is exactly the kind that lasts until the next person adds a step — and
/// <c>release.yml</c> is a file where a step runs in a job that can publish under this package's
/// name. <see cref="WorkflowPinsTests"/> runs this over the real workflows and over fixtures that
/// break each rule on purpose, so the guard is proven to bite.
/// </para>
/// </remarks>
internal static partial class WorkflowPins
{
    /// <summary>
    /// A step's <c>uses:</c> line: the reference it names, and its trailing comment if it has one.
    /// </summary>
    /// <remarks>
    /// Anchored at the start of the line with nothing but whitespace and an optional list dash in
    /// front, so a <c>uses:</c> inside a comment — a commented-out step, or a header paragraph
    /// explaining this very rule — is not read as a step someone forgot to pin.
    /// </remarks>
    [GeneratedRegex(
        @"^[ \t]*(?:-[ \t]+)?uses:[ \t]*(?<reference>[^ \t#]+)(?:[ \t]*#[ \t]*(?<comment>.*?))?[ \t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex UsesLine();

    /// <summary>A full commit SHA: forty hex characters and nothing else.</summary>
    /// <remarks>
    /// The whole forty. An abbreviated SHA is not a pin — GitHub will not resolve one, and a
    /// prefix short enough to be typed is short enough to be collided with deliberately.
    /// </remarks>
    [GeneratedRegex(@"^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitSha();

    /// <summary>A version in a trailing comment: <c>v</c> and a dotted number.</summary>
    [GeneratedRegex(@"\bv[0-9]+(?:\.[0-9]+)*\b", RegexOptions.CultureInvariant)]
    private static partial Regex StatedVersion();

    /// <summary>One <c>uses:</c> reference, split into the parts the rules are about.</summary>
    /// <param name="Reference">The reference as written, e.g. <c>actions/checkout@11d5960…</c>.</param>
    /// <param name="Action">Everything before the <c>@</c> — the action being run.</param>
    /// <param name="Ref">Everything after it: a commit SHA, or a tag or branch that can move.</param>
    /// <param name="StatedVersion">The version named in the trailing comment, or null if there is none.</param>
    internal readonly record struct ActionReference(
        string Reference,
        string Action,
        string Ref,
        string? StatedVersion)
    {
        /// <summary>True when this reference names a commit rather than something movable.</summary>
        internal bool IsPinned => CommitSha().IsMatch(Ref);
    }

    /// <summary>
    /// Returns every reference in <paramref name="workflowText"/> that names code from outside this
    /// repository, which is the set that can be pinned at all.
    /// </summary>
    /// <remarks>
    /// Split into lines and matched one at a time rather than with a multiline pattern: <c>$</c>
    /// stops before <c>\n</c> and leaves a <c>\r</c> behind it unmatched, so the same pattern would
    /// quietly find nothing in a file that reached a checkout with CRLF endings — the failure mode
    /// where a guard reports green because it read no input.
    /// </remarks>
    internal static IReadOnlyList<ActionReference> Read(string workflowText)
    {
        var references = new List<ActionReference>();

        foreach (string line in workflowText.Split('\n'))
        {
            Match match = UsesLine().Match(line.TrimEnd('\r'));

            if (!match.Success)
            {
                continue;
            }

            string reference = match.Groups["reference"].Value;

            if (reference.StartsWith("./", StringComparison.Ordinal)
                || reference.StartsWith("../", StringComparison.Ordinal)
                || reference.StartsWith("docker://", StringComparison.Ordinal))
            {
                // An action stored in this repository travels with the commit that uses it, so
                // there is no separate history to pin it to. A container reference pins by image
                // digest instead of by commit, which is a different rule; nothing here runs one,
                // and writing a check for a shape this repository does not use would be a guess.
                continue;
            }

            int at = reference.LastIndexOf('@');

            if (at < 0)
            {
                // No ref at all. GitHub rejects that for a third-party action, so it cannot reach
                // a run — and reporting it as an unpinned tag would name the wrong problem.
                continue;
            }

            Match version = StatedVersion().Match(match.Groups["comment"].Value);

            references.Add(
                new ActionReference(
                    reference,
                    reference[..at],
                    reference[(at + 1)..],
                    version.Success ? version.Value : null));
        }

        return references;
    }

    /// <summary>
    /// Returns one message per violation in <paramref name="workflowText"/>, or an empty list when
    /// every action it runs is pinned and legible. <paramref name="workflowPath"/> is used only to
    /// name the file in a message.
    /// </summary>
    internal static IReadOnlyList<string> Inspect(string workflowPath, string workflowText)
    {
        var violations = new List<string>();

        foreach (ActionReference reference in Read(workflowText))
        {
            if (!reference.IsPinned)
            {
                violations.Add(
                    $"{workflowPath}: runs '{reference.Reference}', whose ref '{reference.Ref}' is a tag or branch its owner can move. Pin the commit SHA and put the version in a trailing comment.");
            }
            else if (reference.StatedVersion is null)
            {
                violations.Add(
                    $"{workflowPath}: pins '{reference.Action}' to a commit with no version after it, so nobody can tell what it is without resolving the SHA. Add a trailing '# v<major>.<minor>.<patch>'.");
            }
        }

        return violations;
    }
}
