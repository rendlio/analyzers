using System.Xml.Linq;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds <c>Directory.Build.props</c> to the platform its projects build for, and holds the rule
/// itself to props fixtures that break it on purpose.
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
/// exists for. Both endings are checked here, because nothing else in the build can check either.
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

    /// <summary>The props file this repository is held to, at the root every project walks up to.</summary>
    private static string PropsPath => Path.Combine(RepositoryLayout.Root, PropsFile);

    /// <summary>
    /// Every property element in <paramref name="propsText"/> — anything written directly inside a
    /// <c>PropertyGroup</c>.
    /// </summary>
    /// <remarks>
    /// Matched on local names, so the reader works whether or not the file carries the old MSBuild
    /// XML namespace. This one does not, because SDK-style projects dropped it; a namespace is
    /// still the kind of detail a tool puts back, and a reader that silently found nothing
    /// afterwards would report the repository clean for having become unreadable.
    /// </remarks>
    private static List<XElement> PropertyElements(string propsText) =>
        [.. XDocument.Parse(propsText)
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
            XAttribute? condition = pin.Attribute("Condition");

            if (condition is not null)
            {
                violations.Add(
                    $"{propsPath}: conditions <Platform> on \"{condition.Value}\". An inherited Platform is what makes the property non-empty, so a condition is how this pin stops firing in the one case it is for. State it unconditionally — an explicit -p:Platform= is a global property and overrides it regardless.");
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
}
