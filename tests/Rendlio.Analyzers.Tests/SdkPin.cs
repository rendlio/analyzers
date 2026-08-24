using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Reads the SDK pin in <c>global.json</c>, and reports the ways it lets two machines building the
/// same commit resolve different SDK feature bands — which is to say, different analyzers.
/// </summary>
/// <remarks>
/// The analyzers that decide whether this repository builds ship inside the SDK, and the rule set
/// changes with the feature band: <c>10.0.1xx</c> and <c>10.0.4xx</c> bundle different versions of
/// the .NET analyzers, so a diagnostic that does not exist on one is a build error on the other
/// under <c>TreatWarningsAsErrors</c>.
/// <para>
/// The two ends of the pin read this file differently, which is the whole of the problem.
/// <c>actions/setup-dotnet</c> takes the <c>version</c> field and installs exactly that, and never
/// looks at <c>rollForward</c> — CI ends up with one SDK, the one named. The muxer on a developer's
/// machine does the opposite: it takes whatever SDKs are already installed and applies
/// <c>rollForward</c> to choose among them. Written <c>latestFeature</c>, as this file was, the two
/// agree only until somebody installs a newer band, and then the same commit builds under
/// <c>10.0.100</c> in CI and <c>10.0.400</c> on the desk, with a green run on one side and a red one
/// on the other and nothing in the diff to explain it.
/// </para>
/// <para>
/// A policy that cannot leave the band is what makes them agree by construction instead of by luck.
/// It is not a version freeze: patches within a feature band are servicing releases that carry the
/// band's analyzers, so a machine one patch ahead still compiles under the same rules. Crossing a
/// band is the move that changes what the compiler says, so crossing a band is what this forbids.
/// </para>
/// <para>
/// The cost is deliberate and falls on the machine, not on the build: a checkout without the pinned
/// band stops with the SDK's own message naming what to install, rather than quietly compiling
/// under a different set of rules. Failing to start is the good outcome here; the outcome this
/// exists to prevent is two machines that both finish and disagree.
/// </para>
/// </remarks>
internal static partial class SdkPin
{
    /// <summary>The file at the repository root that every SDK resolution here starts from.</summary>
    internal const string FileName = "global.json";

    /// <summary>The action whose steps put an SDK on a runner.</summary>
    internal const string SetupAction = "actions/setup-dotnet";

    /// <summary>
    /// An exact SDK version: major, minor, and the three-digit component carrying both the feature
    /// band and the patch level inside it.
    /// </summary>
    /// <remarks>
    /// Three digits exactly, so <c>10.0.4</c> and <c>10.0.x</c> are not read as pins — neither names
    /// a band for a roll-forward policy to stay inside. A prerelease version fails this too, which
    /// is the same rule said twice: <c>allowPrerelease</c> is false below, so a version carrying a
    /// suffix would pin resolution to something the same file forbids.
    /// </remarks>
    [GeneratedRegex(@"^[0-9]+\.[0-9]+\.[0-9]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersion();

    /// <summary>
    /// A step that runs <see cref="SetupAction"/>, however the <c>uses:</c> is laid out.
    /// </summary>
    /// <remarks>
    /// Anchored at the start of the line with nothing but whitespace and an optional list dash in
    /// front, for the reason <see cref="WorkflowPins"/> gives: these workflows carry paragraphs of
    /// prose about their own steps, and a pattern that matched anywhere on a line would read a
    /// sentence as a step.
    /// </remarks>
    [GeneratedRegex(
        @"^[ \t]*(?:-[ \t]+)?uses:[ \t]*actions/setup-dotnet@",
        RegexOptions.CultureInvariant)]
    private static partial Regex SetupStepLine();

    /// <summary>A <c>key: value</c> line, which is the shape of every input a step is given.</summary>
    [GeneratedRegex(
        @"^[ \t]*(?<key>[A-Za-z0-9_-]+):[ \t]*(?<value>[^#\r\n]*?)[ \t]*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex InputLine();

    /// <summary>The key that opens the section every job in a workflow is written under.</summary>
    [GeneratedRegex(@"^jobs:[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex JobsKeyLine();

    /// <summary>
    /// A job's own key: two spaces of indent, a name, and nothing after the colon.
    /// </summary>
    /// <remarks>
    /// Two spaces exactly, which is what separates a job from everything written inside one —
    /// <c>runs-on:</c> and <c>steps:</c> sit deeper, and a <c>run:</c> block's shell text deeper
    /// still. Requiring the line to end at the colon is the other half: <c>group:
    /// release-${{ github.ref }}</c> is indented the same and names a value, so a pattern that
    /// stopped at the colon would read it as a job.
    /// </remarks>
    [GeneratedRegex(@"^  (?<name>[A-Za-z0-9_-]+):[ \t]*$", RegexOptions.CultureInvariant)]
    private static partial Regex JobHeaderLine();

    /// <summary>The <c>dotnet</c> muxer invoked as a command.</summary>
    /// <remarks>
    /// Bounded on the left so the word has to start a token: <c>setup-dotnet@…</c> names the action
    /// that installs one rather than a build that uses one, and <c>analyzers/dotnet/cs</c> is a path
    /// inside a package. Bounded on the right by whitespace for the same reason from the other
    /// side, since every real invocation is followed by a verb.
    /// </remarks>
    [GeneratedRegex(@"(?<![\w./-])dotnet[ \t]", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetCommand();

    /// <summary>The roll-forward policies that cannot resolve outside the band the version names.</summary>
    private static readonly string[] _withinBand = ["patch", "latestPatch", "disable"];

    /// <summary>The policies that can, in increasing order of how far they reach.</summary>
    private static readonly string[] _crossesBand =
        ["feature", "latestFeature", "minor", "latestMinor", "major", "latestMajor"];

    /// <summary>The three fields of an <c>sdk</c> section that decide what a machine resolves.</summary>
    /// <param name="Version">The version named, or null when the section names none.</param>
    /// <param name="RollForward">The roll-forward policy, or null when it is left unstated.</param>
    /// <param name="AllowPrerelease">Whether previews may win, or null when it is left unstated.</param>
    internal readonly record struct Pin(string? Version, string? RollForward, bool? AllowPrerelease);

    /// <summary>One <see cref="SetupAction"/> step, reduced to how it chooses an SDK.</summary>
    /// <param name="PinFile">Its <c>global-json-file:</c> input, or null when it has none.</param>
    /// <param name="NamedVersion">Its <c>dotnet-version:</c> input, or null when it has none.</param>
    internal readonly record struct SetupStep(string? PinFile, string? NamedVersion);

    /// <summary>One job in a workflow: its name, and the lines written under it.</summary>
    /// <param name="Name">The job's key, which is what a log and a required-check name it by.</param>
    /// <param name="Text">Everything indented under that key, up to the next job.</param>
    internal readonly record struct Job(string Name, string Text);

    /// <summary>
    /// Reads the <c>sdk</c> section of <paramref name="pinText"/>, or null when there is not one.
    /// </summary>
    /// <remarks>
    /// Fields are matched by their exact spelling, which is stricter than the resolver and stricter
    /// in the safe direction: a key written some other way reads here as unstated and is reported,
    /// where guessing at the casing could accept a band-crossing policy for a policy this file never
    /// states.
    /// <para>
    /// JSON that will not parse throws out of here rather than being reported. A <c>global.json</c>
    /// the SDK cannot read is not a repository with a weak pin, it is a repository that does not
    /// build at all, and the parse error names that better than a sentence about roll-forward would.
    /// </para>
    /// </remarks>
    internal static Pin? Read(string pinText)
    {
        using var document = JsonDocument.Parse(pinText);

        if (!document.RootElement.TryGetProperty("sdk", out JsonElement sdk)
            || sdk.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new Pin(
            StringOrNull(sdk, "version"),
            StringOrNull(sdk, "rollForward"),
            sdk.TryGetProperty("allowPrerelease", out JsonElement allowPrerelease)
                && allowPrerelease.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? allowPrerelease.GetBoolean()
                    : null);
    }

    /// <summary>
    /// The feature band <paramref name="version"/> belongs to, written the way the SDK's own
    /// documentation writes one: <c>10.0.400</c> is in the <c>10.0.4xx</c> band.
    /// </summary>
    /// <remarks>
    /// This is the unit the pin is really about, and the unit the contributor-facing page has to
    /// state: two SDKs in one band carry the same analyzers, and two in different bands do not.
    /// </remarks>
    /// <exception cref="FormatException">
    /// <paramref name="version"/> is not an exact SDK version, and so names no band.
    /// </exception>
    internal static string FeatureBand(string version) =>
        ExactVersion().IsMatch(version)
            ? $"{version[..version.LastIndexOf('.')]}.{version[^3]}xx"
            : throw new FormatException(
                $"'{version}' is not an exact SDK version, so it names no feature band.");

    /// <summary>
    /// Returns one message per way <paramref name="pinText"/> lets two machines resolve different
    /// analyzers, or an empty list when it holds them to one band. <paramref name="pinPath"/> only
    /// names the file in a message; nothing is read from disk.
    /// </summary>
    internal static IReadOnlyList<string> Inspect(string pinPath, string pinText)
    {
        if (Read(pinText) is not { } pin)
        {
            return
            [
                $"{pinPath}: has no \"sdk\" section, so it pins nothing and every machine resolves whichever SDK it happens to have. State a version, a roll-forward policy that stays inside its feature band, and allowPrerelease: false.",
            ];
        }

        var violations = new List<string>();

        if (pin.Version is not { } version)
        {
            violations.Add(
                $"{pinPath}: names no \"version\", so there is no feature band for a roll-forward policy to stay inside and nothing for CI to install.");
        }
        else if (!ExactVersion().IsMatch(version))
        {
            violations.Add(
                $"{pinPath}: pins \"version\" to \"{version}\", which is not an exact SDK version. Write major.minor and the three-digit feature-band component — 10.0.400 — because setup-dotnet installs this string literally, and a wildcard or a prerelease resolves to whatever a given machine happens to hold.");
        }

        if (pin.RollForward is not { } rollForward)
        {
            violations.Add(
                $"{pinPath}: leaves \"rollForward\" unstated, so which SDK a machine resolves rests on a default that CI never applies at all. State one of {string.Join(", ", _withinBand)}.");
        }
        else if (_crossesBand.Contains(rollForward, StringComparer.OrdinalIgnoreCase))
        {
            violations.Add(
                $"{pinPath}: sets \"rollForward\" to \"{rollForward}\", which resolves outside the feature band the version names. setup-dotnet installs that version exactly and never rolls forward, so this is the line that lets CI and a developer's machine compile one commit under two different sets of analyzers. Use latestPatch.");
        }
        else if (!_withinBand.Contains(rollForward, StringComparer.Ordinal))
        {
            violations.Add(
                $"{pinPath}: sets \"rollForward\" to \"{rollForward}\", which is not a policy the SDK documents — spelled this way it is rejected outright rather than meaning what it looks like. Use one of {string.Join(", ", _withinBand)}.");
        }

        if (pin.AllowPrerelease != false)
        {
            violations.Add(
                $"{pinPath}: does not state \"allowPrerelease\": false, so a preview SDK sitting on a machine can win the resolution — and a preview carries analyzers of its own, which is the same split this pin exists to close.");
        }

        return violations;
    }

    /// <summary>
    /// Returns every <see cref="SetupAction"/> step in <paramref name="workflowText"/>, with the
    /// inputs that decide which SDK it installs.
    /// </summary>
    /// <remarks>
    /// A step runs to the next line that opens a list item, which is the coarse part of this: the
    /// inputs of a step are indented under it and nothing between them opens one, but a <c>run:</c>
    /// block whose own text began with a dash would end the step early. Nothing here does that, and
    /// a reader that understood YAML properly would be a larger thing than the rule it serves.
    /// <para>
    /// Split on <c>'\n'</c> with the carriage return stripped per line rather than matched with a
    /// multiline pattern, for the reason <see cref="WorkflowPins.Read"/> gives: <c>$</c> stops
    /// before <c>\n</c> and leaves the <c>\r</c> unmatched, so the same rule would quietly find
    /// nothing in a checkout with CRLF endings — a guard reporting green for having read no input.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<SetupStep> ReadSetupSteps(string workflowText)
    {
        string[] lines = [.. workflowText.Split('\n').Select(line => line.TrimEnd('\r'))];
        var steps = new List<SetupStep>();

        for (int i = 0; i < lines.Length; i++)
        {
            if (!SetupStepLine().IsMatch(lines[i]))
            {
                continue;
            }

            string? pinFile = null;
            string? namedVersion = null;

            for (int j = i + 1; j < lines.Length && !OpensListItem(lines[j]); j++)
            {
                // Comments dropped before the inputs are read, because these steps carry prose
                // explaining the very rule below; folded in beside the inputs, a commented-out
                // dotnet-version would be reported as one the step passes.
                if (lines[j].TrimStart().StartsWith('#'))
                {
                    continue;
                }

                Match input = InputLine().Match(lines[j]);

                if (!input.Success)
                {
                    continue;
                }

                switch (input.Groups["key"].Value)
                {
                    case "global-json-file":
                        pinFile = input.Groups["value"].Value;
                        break;

                    case "dotnet-version":
                        namedVersion = input.Groups["value"].Value;
                        break;

                    default:
                        break;
                }
            }

            steps.Add(new SetupStep(pinFile, namedVersion));
        }

        return steps;
    }

    /// <summary>
    /// Returns one message per <see cref="SetupAction"/> step in <paramref name="workflowText"/>
    /// that decides its SDK anywhere other than <see cref="FileName"/>, or an empty list when every
    /// step reads the pin. <paramref name="workflowPath"/> only names the file in a message.
    /// </summary>
    /// <remarks>
    /// This is the far end of the same pin. <see cref="Inspect"/> holds the file to naming one band;
    /// this holds CI to being the thing that reads it, because a step that names a version of its
    /// own is a second copy of the pin — and a second copy is the one that drifts, silently, with a
    /// green run either side of the disagreement.
    /// </remarks>
    internal static IReadOnlyList<string> SetupStepViolations(string workflowPath, string workflowText)
    {
        var violations = new List<string>();

        foreach (SetupStep step in ReadSetupSteps(workflowText))
        {
            if (step.NamedVersion is { } named)
            {
                violations.Add(
                    $"{workflowPath}: installs the SDK with \"dotnet-version: {named}\", a second copy of the pin that can drift from {FileName} with nothing anywhere to read. Take the version from \"global-json-file: {FileName}\" instead.");
            }

            if (step.PinFile is not { } pinFile)
            {
                violations.Add(
                    $"{workflowPath}: installs the SDK without \"global-json-file: {FileName}\", so the runner gets whatever the step defaults to and the repository's pin decides nothing here.");
            }
            else if (pinFile != FileName)
            {
                violations.Add(
                    $"{workflowPath}: takes its SDK from \"{pinFile}\" rather than {FileName}, which is the file every build in this repository resolves against.");
            }
        }

        return violations;
    }

    /// <summary>
    /// Returns every job in <paramref name="workflowText"/>, in the order the file writes them.
    /// </summary>
    /// <remarks>
    /// Jobs rather than steps, because the unit an SDK is installed for is the job: a runner is a
    /// fresh machine per job, so a <see cref="SetupAction"/> step in one reaches none of the
    /// others. A file-wide sweep cannot see that — it folds over the steps a file has and says
    /// nothing about the jobs that have none.
    /// <para>
    /// The section runs from the <c>jobs:</c> key to the next key at the left margin, and a job
    /// runs to the next one. Coarse in the same way <see cref="ReadSetupSteps"/> is: a heredoc
    /// inside a <c>run:</c> block whose own text sat at two spaces and ended in a colon would read
    /// as a job. Nothing here does, and the alternative is a YAML parser for a rule that needs to
    /// know where one job stops.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<Job> ReadJobs(string workflowText)
    {
        string[] lines = [.. workflowText.Split('\n').Select(line => line.TrimEnd('\r'))];
        int start = Array.FindIndex(lines, JobsKeyLine().IsMatch);
        var jobs = new List<Job>();

        if (start < 0)
        {
            return jobs;
        }

        string? name = null;
        var body = new List<string>();

        for (int i = start + 1; i < lines.Length && !LeavesTheJobsSection(lines[i]); i++)
        {
            Match header = JobHeaderLine().Match(lines[i]);

            if (!header.Success)
            {
                if (name is not null)
                {
                    body.Add(lines[i]);
                }

                continue;
            }

            if (name is not null)
            {
                jobs.Add(new Job(name, string.Join('\n', body)));
            }

            name = header.Groups["name"].Value;
            body.Clear();
        }

        if (name is not null)
        {
            jobs.Add(new Job(name, string.Join('\n', body)));
        }

        return jobs;
    }

    /// <summary>True when <paramref name="jobText"/> invokes the <c>dotnet</c> muxer anywhere.</summary>
    /// <remarks>
    /// Comments dropped first, for the reason <see cref="ReadSetupSteps"/> drops them: these
    /// workflows explain their own <c>dotnet</c> steps in prose, and a job that only talks about
    /// building is not a job that builds.
    /// </remarks>
    internal static bool RunsDotnet(string jobText) =>
        jobText.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))
            .Any(DotnetCommand().IsMatch);

    /// <summary>
    /// Returns one message per job in <paramref name="workflowText"/> that runs <c>dotnet</c>
    /// without installing an SDK first, or an empty list when every such job does.
    /// <paramref name="workflowPath"/> only names the file in a message.
    /// </summary>
    /// <remarks>
    /// The gap this closes is one a file-wide sweep leaves open by its shape. <see
    /// cref="SetupStepViolations"/> reports steps that install the wrong SDK, and a job with no
    /// step at all contributes nothing for it to report — so a release whose publishing job lost
    /// its install step reads as compliant while packing under whatever SDK the runner image
    /// happens to ship. That is this repository's split moved into CI and out of sight: not two
    /// machines disagreeing about a build, but one machine shipping a package built under
    /// analyzers nobody pinned.
    /// </remarks>
    internal static IReadOnlyList<string> MissingSetupStepViolations(
        string workflowPath, string workflowText) =>
        [.. ReadJobs(workflowText)
            .Where(job => RunsDotnet(job.Text) && ReadSetupSteps(job.Text).Count == 0)
            .Select(job =>
                $"{workflowPath}: job \"{job.Name}\" runs dotnet without an {SetupAction} step, so it uses whatever SDK the runner image ships rather than the one {FileName} names. A runner is a fresh machine per job, so a step in another job does not reach this one.")];

    /// <summary>True when <paramref name="line"/> is a key at the left margin, ending the section.</summary>
    private static bool LeavesTheJobsSection(string line) =>
        line.Length > 0 && !char.IsWhiteSpace(line[0]) && line[0] != '#';

    /// <summary>True when <paramref name="line"/> opens a YAML list item, at any indentation.</summary>
    private static bool OpensListItem(string line) =>
        line.TrimStart().StartsWith("- ", StringComparison.Ordinal);

    /// <summary>
    /// The string value of <paramref name="name"/> in <paramref name="sdk"/>, or null when it is
    /// absent or is written as something other than a string.
    /// </summary>
    private static string? StringOrNull(JsonElement sdk, string name) =>
        sdk.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
