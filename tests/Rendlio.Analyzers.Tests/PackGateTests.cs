using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds the two gates under <c>eng/</c> that inspect what a pack produced — the package's entry
/// list, and whether the analyzer loads in the lowest compiler host the README claims — to the
/// project they are statements about, and holds the workflows to the gates.
/// </summary>
/// <remarks>
/// Nothing in this suite packs. The tests construct the analyzer classes and drive them through a
/// compilation, so none of them goes near <c>analyzers/dotnet/cs</c> — the only path NuGet reads to
/// install a C# analyzer — and the checks that do are shell scripts the workflows run. That leaves
/// two ways for the arrangement to rot with every test green: a gate's expected-entry list can drift
/// away from what the project produces, and a workflow can stop running a gate at all.
/// <para>
/// Both are checked here because both are invisible in a passing run. The first is why the list is
/// read back and compared against the project rather than trusted — it names an assembly and a path,
/// and a project file is free to change either. The second is the more ordinary failure: a step
/// renamed, a gate moved, a pack added to a new workflow that nobody thought to gate. It ends with a
/// package published on the word of a check that was not run, to a feed where a version can be
/// unlisted but never replaced.
/// </para>
/// </remarks>
public sealed partial class PackGateTests
{
    /// <summary>The gate that asserts a package contains exactly the entries it ships.</summary>
    private const string LayoutGate = "eng/verify-package-layout.sh";

    /// <summary>The gate that installs the package under a named SDK and proves it reports there.</summary>
    private const string HostFloorGate = "eng/verify-host-floor.sh";

    /// <summary>The packable project both gates are statements about.</summary>
    private const string PackableProject = "src/Rendlio.Analyzers/Rendlio.Analyzers.csproj";

    /// <summary>The workflow whose artifact reaches nuget.org.</summary>
    private const string PublishingWorkflow = ".github/workflows/release.yml";

    /// <summary>The workflow that gates a pull request.</summary>
    private const string IntegrationWorkflow = ".github/workflows/ci.yml";

    /// <summary>The only path NuGet reads to install a C# analyzer.</summary>
    private const string AnalyzerPath = "analyzers/dotnet/cs";

    private static string Absolute(string relativePath) =>
        Path.Combine(RepositoryLayout.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static XDocument Project() => XDocument.Load(Absolute(PackableProject));

    // --------------------------------------- the layout gate's list, against the project file

    /// <summary>
    /// The entries the layout gate expects a package to contain, read out of the heredoc that
    /// declares them.
    /// </summary>
    /// <remarks>
    /// Read from the script rather than repeated here, for the reason the README pins are read from
    /// the files that declare them: a copy would agree with the script on the day it was written and
    /// never again.
    /// </remarks>
    [GeneratedRegex(@"<<'ENTRIES'\r?\n(?<entries>.*?)\r?\nENTRIES\r?\n",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ExpectedEntries();

    private static List<string> LayoutGateEntries()
    {
        Match match = ExpectedEntries().Match(File.ReadAllText(Absolute(LayoutGate)));

        // Guards the guard: a script rewritten so the list is no longer a heredoc leaves every
        // assertion below reading an empty set, which agrees with anything.
        Assert.True(
            match.Success,
            $"{LayoutGate} no longer declares its expected entries as an ENTRIES heredoc, so nothing below is reading them.");

        List<string> entries =
        [
            .. match.Groups["entries"].Value
                .Split('\n')
                .Select(static line => line.Trim('\r').Trim())
                .Where(static line => line.Length > 0)
        ];

        Assert.NotEmpty(entries);

        return entries;
    }

    [Fact]
    public void The_layout_gate_expects_the_assembly_the_project_actually_produces()
    {
        // The gate's list is written out rather than derived from the project, deliberately: a list
        // derived from the project would agree with it by construction and check nothing. This is
        // the other half of that trade — the two are compared here, so the list cannot quietly stop
        // describing the package while the gate goes on passing every package it is handed.
        //
        // AssemblyName is not stated in the project file, so it falls back to the file's own name,
        // which is what the packed <None Include> resolves $(AssemblyName) to.
        string assembly = Project().Descendants("AssemblyName").SingleOrDefault()?.Value.Trim()
            ?? Path.GetFileNameWithoutExtension(PackableProject);

        Assert.Contains($"{AnalyzerPath}/{assembly}.dll", LayoutGateEntries(), StringComparer.Ordinal);
    }

    [Fact]
    public void The_layout_gate_expects_the_readme_the_project_packs()
    {
        // PackageReadmeFile is what nuget.org renders on every version's listing, and it is packed by
        // a <None Include> of its own rather than by anything automatic. A package that stopped
        // carrying it lists with no description at all, which is not a layout detail.
        string readme = Project().Descendants("PackageReadmeFile").Single().Value.Trim();

        Assert.Contains(readme, LayoutGateEntries(), StringComparer.Ordinal);
    }

    [Fact]
    public void The_layout_gate_expects_no_library_for_anyone_to_compile_against()
    {
        // The finding this gate was written for. Measured: `dotnet pack -p:IncludeBuildOutput=true`
        // adds lib/netstandard2.0/*.dll and *.xml to this package, and the presence check that came
        // before exited 0 on it — an analyzer shipping a reference assembly, from a project whose
        // IncludeBuildOutput=false and NU5128 suppression both say it ships no library. A lib/ entry
        // creeping into the EXPECTED list is how that comes back, so the list is checked for one
        // rather than only the packages the list is compared against.
        Assert.DoesNotContain(
            LayoutGateEntries(),
            entry => entry.StartsWith("lib/", StringComparison.Ordinal));

        Assert.Equal(
            "false",
            Project().Descendants("IncludeBuildOutput").Single().Value.Trim());
    }

    [Fact]
    public void The_packed_assembly_carries_its_own_debug_information()
    {
        // This package ships exactly one file a consumer's toolchain reads, so debug information
        // reaches anyone only by being inside that assembly. A portable PDB is a separate file and
        // nothing here packs it: the default leaves a consumer whose build hits an analyzer crash
        // with method tokens and nowhere to step.
        //
        // Pinned here because the alternative does not exist rather than because embedded is
        // preferred. Measured: `-p:IncludeSymbols=true` on this project FAILS the pack with NU5017,
        // there being no build output whose symbols a .snupkg could carry — so a later edit that
        // "modernised" this to a symbol package would not degrade the pack, it would break it.
        Assert.Equal("embedded", Project().Descendants("DebugType").Single().Value.Trim());
    }

    // ------------------------------------------------- the workflows, against the gates they run

    [Fact]
    public void Every_workflow_that_packs_this_package_also_runs_the_layout_gate()
    {
        // The failure that would otherwise be silent: publishing on the word of a check nobody ran.
        // A pack step in a workflow with no gate beside it produces an artifact nothing read the
        // inside of, and in release.yml that artifact goes to nuget.org.
        var ungated = new List<string>();
        var packing = new List<string>();

        foreach (string workflow in RepositoryLayout.Workflows())
        {
            string relative = Path.GetRelativePath(RepositoryLayout.Root, workflow).Replace('\\', '/');
            string text = File.ReadAllText(workflow);

            if (!text.Contains("dotnet pack", StringComparison.Ordinal))
            {
                continue;
            }

            packing.Add(relative);

            if (!text.Contains(LayoutGate, StringComparison.Ordinal))
            {
                ungated.Add(
                    $"{relative}: packs this package and never runs {LayoutGate}, so whatever it produces is uploaded or published with nothing having read the inside of it.");
            }
        }

        // Guards the guard, twice. A rule enforced by searching workflows for a verb reports green
        // the moment the search stops finding it, and one enforced over a directory reports green
        // the moment the walk stops finding files. release.yml is named because it is the workflow
        // whose artifact reaches nuget.org: a sweep not seeing that one is not checking the case
        // this exists for.
        Assert.NotEmpty(packing);
        Assert.Contains(PublishingWorkflow, packing, StringComparer.Ordinal);
        Assert.Empty(ungated);
    }

    /// <summary>The workflows that run the host-floor gate, relative to the root, in a stable order.</summary>
    private static List<string> WorkflowsRunningTheHostFloorGate() =>
        [
            .. RepositoryLayout.Workflows()
                .Where(static workflow =>
                    File.ReadAllText(workflow).Contains(HostFloorGate, StringComparison.Ordinal))
                .Select(static workflow =>
                    Path.GetRelativePath(RepositoryLayout.Root, workflow).Replace('\\', '/'))
        ];

    [Fact]
    public void The_host_floor_the_readme_states_is_run_against_before_a_release_and_on_a_pull_request()
    {
        // The README tells a consumer the lowest host this package supports. Everything else in CI
        // runs on the SDK global.json pins, which is the other end of that range — so without a
        // workflow running the floor gate the sentence is a claim nobody has executed, which is the
        // state this gate was written to leave.
        //
        // Both workflows, for different reasons. ci.yml is where a change that breaks the floor
        // should be caught. release.yml is where it matters that the commit being PUBLISHED was the
        // one checked: the README carrying the promise is packed into the artifact that job pushes,
        // and a version on nuget.org can be unlisted but never replaced.
        List<string> running = WorkflowsRunningTheHostFloorGate();

        Assert.Contains(IntegrationWorkflow, running, StringComparer.Ordinal);
        Assert.Contains(PublishingWorkflow, running, StringComparer.Ordinal);
    }

    [Fact]
    public void Every_workflow_running_the_host_floor_gate_names_the_version_the_readme_does()
    {
        // The floor now lives in three places that cannot see each other: a sentence on the README,
        // and an SDK version in each workflow that installs it. Left uncoupled, raising one leaves
        // the others checking or promising a host nobody meant — and the direction that fails
        // quietly is a gate running a NEWER SDK than the README promises, which passes while saying
        // nothing at all about the host a consumer actually has.
        //
        // Unwrapped first, through the same fold every rule about these pages uses. The sentence is
        // hard-wrapped at column ~90 and the break falls between "SDK" and the number, so a pattern
        // run over the raw file finds nothing — the failure direction where a pin goes red claiming
        // the page no longer says something it plainly still says.
        Match stated = StatedSdkFloor().Match(
            ShippedText.Unwrap(File.ReadAllText(Absolute("README.md"))));

        // Guards the guard: a README that stopped carrying the sentence, or a pattern that stopped
        // matching it, would have nothing to disagree with and would report green forever.
        Assert.True(stated.Success, "README.md no longer states the .NET SDK version it needs.");

        List<string> running = WorkflowsRunningTheHostFloorGate();

        // And the same for the sweep: an empty set satisfies Assert.All.
        Assert.NotEmpty(running);

        // Matched as text anywhere in the file rather than against one key, because the two
        // workflows spell it differently — a matrix entry in one, a job-level env in the other — and
        // a rule that knew only one spelling would go green on the file it could not read.
        Assert.All(
            running,
            workflow => Assert.Contains(
                stated.Groups[1].Value,
                File.ReadAllText(Absolute(workflow)),
                StringComparison.Ordinal));
    }

    /// <summary>The .NET SDK version the README names as the floor.</summary>
    [GeneratedRegex(@"\.NET[ \t]+SDK[ \t]+([0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex StatedSdkFloor();

    [Theory]
    // The shape the README ships, on one line.
    [InlineData("Visual Studio 2022 17.8 or .NET SDK 8.0.100 and upwards.", "8.0.100")]
    // The same sentence as it is actually WRAPPED on the page: the break falls between "SDK" and the
    // number. Folded first, exactly as the check above folds it — this case is what says the fold is
    // load-bearing rather than tidy.
    [InlineData("or .NET SDK\n8.0.100 and upwards.", "8.0.100")]
    // A Markdown hard break arrives from the fold as three spaces, not one, which is why every gap
    // in these patterns is quantified.
    [InlineData(".NET SDK  \n10.20.300 and upwards", "10.20.300")]
    public void The_sdk_floor_pattern_reads_the_version_the_sentence_states(
        string sentence, string expected) =>
        Assert.Equal(
            expected,
            StatedSdkFloor().Match(ShippedText.Unwrap(sentence)).Groups[1].Value);

    // ------------------------------------------------- the gates as files a runner can execute

    [Theory]
    [InlineData(LayoutGate)]
    [InlineData(HostFloorGate)]
    public void A_gate_a_workflow_names_is_a_file_a_linux_runner_can_run(string gate)
    {
        // A workflow naming a script that is not there fails at the step, loudly — but it fails
        // AFTER the pack, in a release run, which is the least useful moment to learn it. And the
        // reference is a string in YAML: renaming the file is not a compile error anywhere.
        string path = Absolute(gate);

        Assert.True(File.Exists(path), $"{gate} does not exist, and a workflow runs it by name.");

        string text = File.ReadAllText(path);

        // The shebang has to be the first line, and it has to end in LF: a CRLF line ending there
        // makes the script unrunnable on the Linux runner that runs it, and the error names an
        // interpreter rather than the line ending — a path that plainly exists, reported as missing.
        // .gitattributes holds `* text=auto eol=lf` today, which is a file someone can edit.
        Assert.StartsWith("#!/usr/bin/env bash\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", text, StringComparison.Ordinal);

        // Both gates fail closed. `set -e` alone lets a failing stage of a pipeline pass its exit
        // code over, and `set -u` is what stops an unset variable becoming an empty argument — which
        // in a gate means checking nothing and saying so in the affirmative.
        Assert.Contains("set -euo pipefail", text, StringComparison.Ordinal);
    }
}
