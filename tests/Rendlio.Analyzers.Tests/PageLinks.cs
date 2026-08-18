using System.Text.RegularExpressions;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Reads the links a published page makes into this repository, resolved to repository-relative
/// paths that a caller can check against the files on disk.
/// </summary>
/// <remarks>
/// The pages here point at each other — the README is what nuget.org renders, and it is the only
/// page a consumer is guaranteed to see, so everything else is reachable only through a link from
/// it. A link is exactly the kind of thing a rename breaks silently: the page still reads correctly
/// and the build still passes, while the reader gets a 404 from the package listing.
///
/// <para>
/// Two spellings have to resolve to the same thing. A page that ships inside the package must use
/// absolute URLs, because relative ones do not resolve on nuget.org; a page that only ever renders
/// on the repository host uses relative ones. Both name a file in this repository, so both are
/// returned here as the same repository-relative path and the caller does not have to care which
/// was written.
/// </para>
/// <para>
/// The repository URL is a parameter rather than a constant: it is declared once in
/// <c>Directory.Build.props</c> and travels into the package metadata from there, so reading it
/// back is what keeps this check honest if it ever changes. Hard-coding it here would also couple
/// the fixtures to a live value, which <see cref="AnalyzerConventionTests"/> deliberately avoids.
/// </para>
/// </remarks>
internal static partial class PageLinks
{
    /// <summary>The path GitHub serves a file from: <c>&lt;repository&gt;/blob/&lt;ref&gt;/&lt;path&gt;</c>.</summary>
    private const string BlobPathSegment = "/blob/";

    /// <summary>
    /// A Markdown inline link, capturing its target: <c>[text](target)</c>, with an optional
    /// angle-bracketed target and an optional title after it.
    /// </summary>
    /// <remarks>
    /// The link text is allowed to contain newlines because these pages are hard-wrapped at column
    /// ~90 and a link label routinely straddles the wrap. The target is not: Markdown has no way to
    /// write a raw space inside one, so whitespace ends the capture.
    /// </remarks>
    [GeneratedRegex(@"\[[^\]]*\]\(\s*<?([^)\s>]+)>?[^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineLink();

    /// <summary>
    /// Returns the repository-relative path of every link in <paramref name="text"/> that names a
    /// file in this repository. Links to anywhere else — another site, a repository page such as
    /// the issue list, an anchor on the same page — are not paths and are left out.
    /// </summary>
    /// <param name="documentPath">
    /// Repository-relative path of the page carrying the links. A relative link resolves against
    /// the directory holding the page, so this is what it is resolved against.
    /// </param>
    /// <param name="text">The page's Markdown source.</param>
    /// <param name="repositoryUrl">This repository's URL, as declared in <c>Directory.Build.props</c>.</param>
    internal static IReadOnlyList<string> RepositoryTargets(string documentPath, string text, string repositoryUrl)
    {
        string page = Normalise(documentPath);
        var targets = new List<string>();

        foreach (Match match in InlineLink().Matches(text))
        {
            if (RepositoryPath(match.Groups[1].Value, page, repositoryUrl) is { Length: > 0 } target)
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    /// <summary>
    /// A link that names a place inside a page: the repository-relative path of the page, and the
    /// anchor within it.
    /// </summary>
    internal readonly record struct AnchoredTarget(string Path, string Fragment);

    /// <summary>
    /// Returns the path-and-anchor of every link in <paramref name="text"/> that names a place
    /// inside a page of this repository. Links carrying no anchor, and anchors on someone else's
    /// site, are left out.
    /// </summary>
    /// <remarks>
    /// The companion to <see cref="RepositoryTargets"/>, which drops the anchor because the file is
    /// all it needs. An anchor fails differently from a path, and quietly enough to be worth reading
    /// separately: a heading rename leaves the link resolving to a page that still exists, so
    /// nothing 404s and the reader is simply dropped at the top of it having clicked something
    /// specific. GitHub does not report a fragment it cannot find.
    /// </remarks>
    /// <param name="documentPath">
    /// Repository-relative path of the page carrying the links — what a relative link, and an
    /// anchor with no path in front of it, resolve against.
    /// </param>
    /// <param name="text">The page's Markdown source.</param>
    /// <param name="repositoryUrl">This repository's URL, as declared in <c>Directory.Build.props</c>.</param>
    internal static IReadOnlyList<AnchoredTarget> RepositoryAnchors(
        string documentPath,
        string text,
        string repositoryUrl)
    {
        string page = Normalise(documentPath);
        var anchors = new List<AnchoredTarget>();

        foreach (Match match in InlineLink().Matches(text))
        {
            string target = match.Groups[1].Value;
            int fragment = target.IndexOf('#');

            if (fragment < 0 || fragment == target.Length - 1)
            {
                // No anchor, or a bare '#' naming nothing. Neither points at a heading.
                continue;
            }

            // A fragment with no path in front of it names a heading on this page, which
            // RepositoryTargets has no reason to report and this does.
            string? path = fragment == 0 ? page : RepositoryPath(target, page, repositoryUrl);

            if (path is { Length: > 0 })
            {
                anchors.Add(new AnchoredTarget(path, target[(fragment + 1)..]));
            }
        }

        return anchors;
    }

    /// <summary>
    /// Resolves one link target to a repository-relative path, or null when it does not name a file
    /// in this repository.
    /// </summary>
    private static string? RepositoryPath(string target, string documentPath, string repositoryUrl)
    {
        string path = WithoutFragment(target);
        if (path.Length == 0)
        {
            // An anchor on the page itself. There is no file to look for.
            return null;
        }

        string blobPrefix = repositoryUrl.TrimEnd('/') + BlobPathSegment;
        if (path.StartsWith(blobPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // Everything after the branch or tag segment is the path within the repository.
            int branchEnd = path.IndexOf('/', blobPrefix.Length);
            return branchEnd < 0 ? null : Normalise(path[(branchEnd + 1)..]);
        }

        if (path.Contains("://", StringComparison.Ordinal)
            || path.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            // Someone else's site, or a repository page rather than a file in it. Either way there
            // is nothing on this disk to check, and guessing would report a file that never existed.
            return null;
        }

        return ResolveAgainst(documentPath, path);
    }

    /// <summary>
    /// Resolves <paramref name="target"/> against the page that carries it, folding away <c>.</c>
    /// and <c>..</c> without touching the filesystem.
    /// </summary>
    private static string ResolveAgainst(string documentPath, string target)
    {
        var segments = new List<string>();

        if (!target.StartsWith('/'))
        {
            // Relative to the directory holding the page, so drop the page's own file name.
            segments.AddRange(documentPath.Split('/'));
            segments.RemoveAt(segments.Count - 1);
        }

        foreach (string segment in Normalise(target).Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private static string WithoutFragment(string target)
    {
        int fragment = target.IndexOf('#');

        return fragment < 0 ? target : target[..fragment];
    }

    /// <summary>Separators folded to '/', so one spelling of a path works on either OS.</summary>
    private static string Normalise(string path) => path.Replace('\\', '/');
}
