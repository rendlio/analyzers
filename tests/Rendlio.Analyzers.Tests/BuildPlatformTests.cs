using System.Xml.Linq;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds <c>Directory.Build.props</c> to the platform its projects build for, holds those projects
/// to not taking it back, and holds both rules to fixtures that break them on purpose.
/// </summary>
/// <remarks>
/// MSBuild reads environment variables as properties, and a Visual Studio Developer Command Prompt
/// exports <c>Platform=x64</c>. A solution build maps its projects onto their own platform and
/// writes <c>bin/Release</c>; the same project addressed directly inherits the shell's value,
/// moves <c>$(OutputPath)</c> to <c>bin/x64/Release</c>, and a pack that skips the build then
/// fails <c>NU5019</c> looking for an assembly nobody wrote there.
/// <para>
/// The pin that prevents it is one line that does nothing observable on a machine which sets no
/// such variable — and every hosted runner is such a machine, so no gate in CI can see the pin
/// working. That makes it the kind of line someone deletes for having no visible effect, and the
/// kind someone rewrites into what looks like the same thing: conditioned on <c>$(Platform)</c>
/// being empty, the way the SDK's own default is written, it cannot fire in the one case it
/// exists for. That second ending is read wherever the condition is written — on the property, on
/// its <c>PropertyGroup</c>, or on a <c>Choose</c> around it — because the group is where MSBuild
/// convention puts one and a rule that saw only the inline form would pass the likeliest spelling
/// of the wrong fix. A third ending rewrites nothing at all: MSBuild imports the props before a
/// project's own body, so a project that names a platform for itself is a later assignment to the
/// same property and simply wins, while the props goes on reading <c>AnyCPU</c> and a rule that
/// only read the props goes on reporting the repository clean. All three endings are checked here;
/// nothing else in the build can check any of them.
/// </para>
/// </remarks>
public sealed class BuildPlatformTests
{
    /// <summary>The file MSBuild finds by name, walking up from each project.</summary>
    private const string PropsFile = "Directory.Build.props";

    /// <summary>The only platform anything in this repository builds for.</summary>
    private const string AnyCpu = "AnyCPU";

    [Fact]
    public void The_repository_pins_the_platform_its_projects_build_for() =>
        Assert.Empty(PlatformPinViolations(PropsFile, File.ReadAllText(PropsPath)));

    [Fact]
    public void The_file_the_rule_reads_is_the_one_every_project_imports()
    {
        // Guards the guard. The spelling of the name is the whole contract — MSBuild imports
        // Directory.Build.props by walking up from a project until it finds exactly that file — so
        // one renamed, moved out of the root, or left unparseable stops applying to the build. The
        // first two throw out of the rule above rather than passing it, which is the right noise;
        // this pins the third, where a document the reader gets nothing out of is indistinguishable
        // from a repository that complies.
        Assert.True(File.Exists(PropsPath), $"{PropsFile} is not at the repository root.");
        Assert.NotEmpty(PropertyElements(File.ReadAllText(PropsPath)));

        // And that the root's copy is the one a project reaches, which is the other half of the
        // same claim. MSBuild stops at the FIRST file of this name it meets walking up, so one
        // added nearer a project detaches everything above it — this pin included — while the rule
        // goes on reading the root's copy and reporting the repository clean. A nearer file is not
        // forbidden; it just has to carry the pin itself or import the one above it with
        // GetPathOfFileAbove, and going red here is how that stays a decision somebody makes.
        Assert.Equal(PropsPath, Assert.Single(PropsFilesInRepository()));
    }

    [Fact]
    public void No_project_takes_the_platform_back_after_the_props_pinned_it()
    {
        // The other end of the same pin, and the one place it can be undone without touching the
        // file the rules above read. A project's own body is evaluated after the props it imports,
        // so a <Platform> written in a csproj is a later assignment to the same property and simply
        // wins: $(OutputPath) returns to bin/<platform>/Release, the pack that skips the build fails
        // NU5019 again, and the root props still says AnyCPU — so every rule above stays green while
        // the pin no longer reaches the project it exists for. Nothing here is platform-specific, so
        // the rule is that no project names a platform at all.
        List<string> projects = ProjectFilesInRepository();

        // Non-vacuous, for the reason the sweep below cannot be: it has to have reached the project
        // whose $(OutputPath) the pack item reads, or finding nothing to inspect would read as
        // finding nothing wrong.
        Assert.Contains(PackableProjectPath, projects);
        Assert.Empty(projects.SelectMany(path => ProjectPlatformOverrides(
            Path.GetRelativePath(RepositoryLayout.Root, path), File.ReadAllText(path))));
    }

    [Theory]
    // No pin at all: the shape this file had before, and the shape it returns to the moment someone
    // removes a line they can see no effect from.
    [InlineData(
        """
        <Project>
          <PropertyGroup>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """)]
    // The plausible wrong fix, and the reason this rule reads the condition rather than the value.
    // It is how the SDK writes its own default and how a defensive edit would write this one, it
    // looks more careful than the unconditional line, and an inherited Platform is exactly what
    // makes the property non-empty — so it fires in every case except the one it is for.
    [InlineData(
        """
        <Project>
          <PropertyGroup>
            <Platform Condition="'$(Platform)' == ''">AnyCPU</Platform>
          </PropertyGroup>
        </Project>
        """)]
    // Pinned, unconditionally, to the platform the ambient variable would have supplied anyway.
    [InlineData(
        """
        <Project>
          <PropertyGroup>
            <Platform>x64</Platform>
          </PropertyGroup>
        </Project>
        """)]
    // The same defeat one level up. This is the likeliest of the three: a condition belongs on the
    // group by MSBuild convention, the property underneath reads as an ordinary unconditional pin,
    // and the file has to be read outwards from it to see that it never fires.
    [InlineData(
        """
        <Project>
          <PropertyGroup Condition="'$(Platform)' == ''">
            <Platform>AnyCPU</Platform>
          </PropertyGroup>
        </Project>
        """)]
    // And two levels up, which is what a <Choose> is for. Rarer, same effect, and it is the case
    // that decides the rule has to walk ancestors rather than look one level out.
    [InlineData(
        """
        <Project>
          <Choose>
            <When Condition="'$(Platform)' == ''">
              <PropertyGroup>
                <Platform>AnyCPU</Platform>
              </PropertyGroup>
            </When>
          </Choose>
        </Project>
        """)]
    public void A_props_file_that_leaves_the_platform_to_the_environment_is_reported(string props) =>
        Assert.Single(PlatformPinViolations(PropsFile, props));

    [Theory]
    // The shape this repository uses.
    [InlineData(
        """
        <Project>
          <PropertyGroup>
            <Platform>AnyCPU</Platform>
          </PropertyGroup>
        </Project>
        """)]
    // The same file carrying the old MSBuild XML namespace, which SDK-style projects omit and an
    // editor re-adds. A reader that matched qualified names would find no properties here and
    // report the file clean, so the namespace-agnostic match is proven rather than assumed.
    [InlineData(
        """
        <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup>
            <Platform>AnyCPU</Platform>
          </PropertyGroup>
        </Project>
        """)]
    public void A_props_file_that_pins_the_platform_is_clean(string props) =>
        // No companion "and the reader saw something" assertion, unlike the workflow rules: a file
        // the reader finds no pin in is reported by the first branch below, so empty here can only
        // mean a pin was found and accepted.
        Assert.Empty(PlatformPinViolations(PropsFile, props));

    [Theory]
    // A project naming its own platform. This is the shape that defeats the pin while leaving it
    // written where the rules above read it.
    [InlineData(
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>netstandard2.0</TargetFramework>
            <Platform>x64</Platform>
          </PropertyGroup>
        </Project>
        """)]
    // Conditioned, and reported all the same — unlike on the props, where a condition is what stops
    // a pin firing. Here it only narrows when the override applies; whenever it holds, the project's
    // value is still the later assignment and still wins.
    [InlineData(
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup Condition="'$(OS)' == 'Windows_NT'">
            <Platform>x86</Platform>
          </PropertyGroup>
        </Project>
        """)]
    // Even set to the platform the pin already names. Agreeing today is not the point: the property
    // has one home, and a second copy is what drifts from it.
    [InlineData(
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <Platform>AnyCPU</Platform>
          </PropertyGroup>
        </Project>
        """)]
    public void A_project_that_names_its_own_platform_is_reported(string project) =>
        Assert.Single(ProjectPlatformOverrides("Some.csproj", project));

    [Theory]
    // The shape every project in this repository has: it says nothing about a platform and inherits
    // the one the props pinned.
    [InlineData(
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>netstandard2.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """)]
    // PlatformTarget is a different property — it names what the compiler emits, not where the
    // build writes — and $(OutputPath) does not read it. A rule that fired on a prefix or a
    // near-miss would forbid a legitimate setting, so the exact-name match is proven, not assumed.
    [InlineData(
        """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <PlatformTarget>AnyCPU</PlatformTarget>
          </PropertyGroup>
        </Project>
        """)]
    public void A_project_that_leaves_the_platform_to_the_pin_is_clean(string project) =>
        Assert.Empty(ProjectPlatformOverrides("Some.csproj", project));

    /// <summary>The props file this repository is held to, at the root every project walks up to.</summary>
    private static string PropsPath => Path.Combine(RepositoryLayout.Root, PropsFile);

    /// <summary>
    /// The project whose <c>$(OutputPath)</c> the pack item reads, which is the one the pin exists
    /// for and therefore the one the sweep has to have seen.
    /// </summary>
    private static string PackableProjectPath =>
        Path.Combine(RepositoryLayout.Root, "src", "Rendlio.Analyzers", "Rendlio.Analyzers.csproj");

    /// <summary>Every project file in the repository, build output aside.</summary>
    /// <remarks>
    /// Enumerated rather than listed, because a project added later inherits the pin and can undo it
    /// the same way, and a rule naming today's two would not notice.
    /// </remarks>
    private static List<string> ProjectFilesInRepository() =>
        [.. Directory.EnumerateFiles(RepositoryLayout.Root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)];

    /// <summary>
    /// Every file of that name in the repository, build output aside — which should be the root's
    /// and nothing else, because a nearer one is what a project would import instead.
    /// </summary>
    /// <remarks>
    /// <c>bin</c> and <c>obj</c> are skipped because a copy under either is output rather than
    /// input: nothing walks up out of a build directory to find it, so reporting one would fail
    /// the build over an artifact of having built.
    /// </remarks>
    private static List<string> PropsFilesInRepository() =>
        [.. Directory.EnumerateFiles(RepositoryLayout.Root, PropsFile, SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path))
            .OrderBy(path => path, StringComparer.Ordinal)];

    /// <summary>True when <paramref name="path"/> sits under a <c>bin</c> or <c>obj</c> directory.</summary>
    /// <remarks>
    /// Tested against the path relative to the repository root, not the absolute one: a checkout
    /// living under a directory of either name — <c>C:\bin\analyzers</c> — would otherwise classify
    /// every file in the repository as output and leave both sweeps above with nothing to inspect.
    /// The leading slash is what lets one test cover <c>bin/</c> at the root and <c>src/x/bin/</c>
    /// alike.
    /// </remarks>
    private static bool IsBuildOutput(string path)
    {
        string normalised = "/" + Path.GetRelativePath(RepositoryLayout.Root, path).Replace('\\', '/');

        return normalised.Contains("/bin/", StringComparison.Ordinal)
            || normalised.Contains("/obj/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Every property element in <paramref name="msbuildText"/> — anything written directly inside a
    /// <c>PropertyGroup</c>, in a props file or a project alike.
    /// </summary>
    /// <remarks>
    /// Matched on local names, so the reader works whether or not the file carries the old MSBuild
    /// XML namespace. Nothing here does, because SDK-style projects dropped it; a namespace is
    /// still the kind of detail a tool puts back, and a reader that silently found nothing
    /// afterwards would report the repository clean for having become unreadable.
    /// </remarks>
    private static List<XElement> PropertyElements(string msbuildText) =>
        [.. XDocument.Parse(msbuildText)
            .Descendants()
            .Where(element => element.Parent?.Name.LocalName == "PropertyGroup")];

    /// <summary>
    /// Returns one message per way <paramref name="propsText"/> leaves the build platform to the
    /// environment, or an empty list when it pins it. <paramref name="propsPath"/> only names the
    /// file in a message; nothing is read from disk.
    /// </summary>
    private static List<string> PlatformPinViolations(string propsPath, string propsText)
    {
        List<XElement> pins =
            [.. PropertyElements(propsText).Where(property => property.Name.LocalName == "Platform")];

        if (pins.Count == 0)
        {
            return
            [
                $"{propsPath}: sets no <Platform>, so a project addressed directly takes one from the environment and writes its output where the solution build did not. Add <Platform>{AnyCpu}</Platform>.",
            ];
        }

        List<string> violations = [];

        foreach (XElement pin in pins)
        {
            // The nearest condition on the way out, rather than the one written on the property
            // itself. A <PropertyGroup> is where MSBuild convention puts a condition and where the
            // SDK writes this very property; a <Choose>/<When> around it is a third spelling of the
            // same thing. Each one stops the pin firing exactly as an inline condition would, so a
            // rule that read only the element would report the likeliest wrong fix as clean.
            XAttribute? condition = pin.AncestorsAndSelf()
                .Select(element => element.Attribute("Condition"))
                .FirstOrDefault(attribute => attribute is not null);

            if (condition is not null)
            {
                violations.Add(
                    $"{propsPath}: conditions <Platform> on \"{condition.Value}\", written on <{condition.Parent?.Name.LocalName}>. An inherited Platform is what makes the property non-empty, so a condition is how this pin stops firing in the one case it is for. State it unconditionally — an explicit -p:Platform= is a global property and overrides it regardless.");
            }

            string pinned = pin.Value.Trim();

            if (pinned != AnyCpu)
            {
                violations.Add(
                    $"{propsPath}: pins <Platform> to \"{pinned}\". Nothing here is platform-specific — an analyzer is loaded into whatever compiler host the consumer runs — so the pin has to say {AnyCpu}.");
            }
        }

        return violations;
    }

    /// <summary>
    /// Returns one message per platform <paramref name="projectText"/> names for itself, or an empty
    /// list when it leaves the property to the pin it inherits. <paramref name="projectPath"/> only
    /// names the file in a message; nothing is read from disk.
    /// </summary>
    /// <remarks>
    /// Conditions are not read here, unlike on the props: there a condition is how a pin stops
    /// firing, while in a project it only narrows when an override applies — and whenever it applies
    /// it is still the later assignment and still wins. No project in this repository has a reason to
    /// name a platform, which makes "none, however written" the whole rule.
    /// </remarks>
    private static List<string> ProjectPlatformOverrides(string projectPath, string projectText) =>
        [.. PropertyElements(projectText)
            .Where(property => property.Name.LocalName == "Platform")
            .Select(property =>
                $"{projectPath}: sets <Platform>{property.Value.Trim()}</Platform> for itself. A project body is evaluated after the {PropsFile} it imports, so this is the value $(OutputPath) follows — back out to bin/<platform>/, where a pack that skips the build finds nothing and reports NU5019. Delete it and inherit the repository-wide pin.")];
}
