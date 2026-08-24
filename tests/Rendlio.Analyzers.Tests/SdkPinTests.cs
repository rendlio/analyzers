using System.Globalization;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// Holds <c>global.json</c> to pinning one SDK feature band, holds the workflows to installing the
/// band it names, holds the contributor-facing page to stating it, and holds
/// <see cref="SdkPin"/> itself to fixtures that break each rule on purpose.
/// </summary>
/// <remarks>
/// The three claims are one claim taken at each end. A pin that names a band is worth nothing if CI
/// installs a version from somewhere else, and both are worth nothing to somebody who cannot find
/// out what to install — so the page that says it is checked against the same file rather than left
/// to go stale beside it.
/// <para>
/// None of this can be observed from a build. A repository whose SDK pin lets machines drift builds
/// perfectly well on every one of them, right up to the day two of them disagree about the same
/// commit, and by then the evidence is a red run on someone's desk and a green one in CI with
/// nothing in between to read. These rules are the only place that disagreement is visible before
/// it happens.
/// </para>
/// </remarks>
public sealed class SdkPinTests
{
    /// <summary>The workflow that installs an SDK and then publishes under this package's name.</summary>
    private const string PublishingWorkflow = "release.yml";

    /// <summary>The workflow that installs an SDK for every pull request.</summary>
    private const string IntegrationWorkflow = "ci.yml";

    /// <summary>The page a contributor reads before their first build.</summary>
    private const string ContributorPage = "README.md";

    [Fact]
    public void The_repository_pins_one_SDK_feature_band() =>
        Assert.Empty(SdkPin.Inspect(SdkPin.FileName, File.ReadAllText(PinPath)));

    [Fact]
    public void The_file_the_rule_reads_is_the_one_every_build_resolves_against()
    {
        // Guards the guard. A missing or unparseable file throws out of the rule above rather than
        // passing it, which is the right noise; this pins the case where the reader finds a document
        // it gets nothing out of, because "no violations" and "nothing was read" are the same result
        // and only one of them means the repository complies.
        Assert.True(File.Exists(PinPath), $"{SdkPin.FileName} is not at the repository root.");
        Assert.NotNull(SdkPin.Read(File.ReadAllText(PinPath))?.Version);

        // And that the root's copy is the one a build here reaches. The SDK resolver walks up from
        // the current directory and stops at the FIRST global.json it meets, exactly as MSBuild does
        // for Directory.Build.props — so one added nearer a project takes over for anything built
        // from inside it while the rule above goes on reading the root's copy and reporting the
        // repository pinned. Read along the project walk-up paths rather than across the whole tree,
        // for the reason BuildPlatformTests gives: a file off every project's path to the root
        // cannot affect a build here, and forbidding one would be a rule about the wrong thing.
        Assert.Equal(
            [SdkPin.FileName],
            RepositoryLayout.FilesOnProjectWalkUpPaths(SdkPin.FileName));
    }

    [Fact]
    public void Every_workflow_installs_the_SDK_the_pin_names()
    {
        List<string> violations = [];

        foreach (string workflow in WorkflowFiles())
        {
            violations.AddRange(
                SdkPin.SetupStepViolations(
                    Path.GetRelativePath(RepositoryLayout.Root, workflow).Replace('\\', '/'),
                    File.ReadAllText(workflow)));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void Every_job_that_runs_dotnet_installs_an_SDK_first()
    {
        // The sweep above folds over the steps a file HAS, so a job holding none contributes
        // nothing for it to fault and reads as compliant — and the guard below asks each file for
        // at least one step, which two jobs and one step satisfies. A runner is a fresh machine per
        // job, though, so an install step reaches only the job that declares it: a job without one
        // builds under whatever SDK the image ships. That is this repository's split moved into CI,
        // where there is no second verdict to notice it — just a package built under analyzers
        // nobody pinned.
        List<string> violations = [];
        List<string> covered = [];

        foreach (string workflow in WorkflowFiles())
        {
            string text = File.ReadAllText(workflow);
            string file = Path.GetFileName(workflow);

            violations.AddRange(SdkPin.MissingSetupStepViolations(file, text));
            covered.AddRange(
                SdkPin.ReadJobs(text)
                    .Where(job => SdkPin.RunsDotnet(job.Text))
                    .Select(job => $"{file}: {job.Name}"));
        }

        // Guards the guard, and names each job rather than counting them, because counting in
        // aggregate is the weakness being fixed here. A splitter that stopped finding jobs or a
        // detector that stopped recognising a dotnet command would empty the sweep and report
        // green. These are every job in this repository that touches the SDK: the two that build
        // and test on both compiler hosts, and the one that packs and publishes. A job added later
        // fails this line, which is the moment to ask whether it installs the pin.
        Assert.Equal(
            ["ci.yml: build-test", "release.yml: publish", "release.yml: test"],
            covered.Order(StringComparer.Ordinal));

        Assert.Empty(violations);
    }

    [Fact]
    public void The_steps_that_install_the_SDK_are_the_ones_that_get_checked()
    {
        // Guards the guard. A rule enforced by searching for a step reports green the moment the
        // search stops finding one — a renamed action, a step rewritten to call the install script
        // directly, a workflow moved to .yaml. Both workflows install an SDK today, and they are
        // named individually rather than swept, because "every workflow has a setup step" is not a
        // rule this repository wants: a workflow added later for something else would break it for
        // no reason, while these two going quiet is precisely the failure worth catching.
        Assert.NotEmpty(SdkPin.ReadSetupSteps(WorkflowText(IntegrationWorkflow)));
        Assert.NotEmpty(SdkPin.ReadSetupSteps(WorkflowText(PublishingWorkflow)));

        // And that the reader got the input out of the steps rather than merely finding them: a
        // reader that located every step and read none of their inputs would report each one clean.
        Assert.All(
            SdkPin.ReadSetupSteps(WorkflowText(PublishingWorkflow)),
            step => Assert.Equal(SdkPin.FileName, step.PinFile));
    }

    [Fact]
    public void The_page_a_contributor_reads_states_the_band_the_pin_names()
    {
        // The pin fails a machine that lacks the band fast and by design, so the page has to say
        // which band that is — and has to keep saying it after the pin moves. Derived from the file
        // rather than written out here, so moving the pin without touching the page goes red instead
        // of leaving a stranger reading last year's answer.
        string version = Assert.IsType<string>(SdkPin.Read(File.ReadAllText(PinPath))?.Version);
        string band = SdkPin.FeatureBand(version);

        Assert.Contains(
            band,
            File.ReadAllText(Path.Combine(RepositoryLayout.Root, ContributorPage)),
            StringComparison.Ordinal);
    }

    [Theory]
    // The shape this file had, and the one this whole check exists for. setup-dotnet installs
    // 10.0.400 exactly; a machine holding a newer band resolves that instead, and the two compile
    // the same commit under different analyzers.
    [InlineData("10.0.400", "latestFeature", false)]
    // The same reach without the "latest", which is no better: it still leaves the band.
    [InlineData("10.0.400", "feature", false)]
    // Further still. A policy that can cross a major version can cross everything below it.
    [InlineData("10.0.400", "latestMajor", false)]
    // Cased the way somebody writes a policy from memory. The resolver is not case-sensitive about
    // this, so a rule that matched the documented spelling alone would read the most reachable band
    // in the list as a value it had never heard of — right verdict, wrong sentence — or, worse,
    // let it through. Pinned here because nothing else would notice which way that went.
    [InlineData("10.0.400", "LATESTFEATURE", false)]
    public void A_pin_that_can_leave_its_feature_band_is_reported(
        string version, string rollForward, bool allowPrerelease)
    {
        string violation = Assert.Single(SdkPin.Inspect(SdkPin.FileName, Pin(version, rollForward, allowPrerelease)));

        Assert.Contains("outside the feature band", violation, StringComparison.Ordinal);
    }

    [Theory]
    // A wildcard is not a pin: setup-dotnet installs the newest match, whenever it happens to run.
    [InlineData("10.0.x")]
    // Two parts name a runtime, not a feature band, and there is no band for latestPatch to hold.
    [InlineData("10.0")]
    // One digit where the band lives. Reads as a patch level and is not a version that exists.
    [InlineData("10.0.4")]
    // A prerelease, which the same file forbids two lines further down — and which carries its own
    // analyzers, so pinning to one would reintroduce the split from the other direction.
    [InlineData("10.0.100-rc.1.25451.107")]
    public void A_version_that_names_no_feature_band_is_reported(string version)
    {
        string violation = Assert.Single(SdkPin.Inspect(SdkPin.FileName, Pin(version, "latestPatch", false)));

        Assert.Contains("not an exact SDK version", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pin_that_leaves_the_roll_forward_policy_unstated_is_reported()
    {
        // Unstated is not the same as safe. The default is applied by the muxer and by nothing on
        // the CI side at all, so leaving it out means the two ends of this pin are following rules
        // that were never written down together.
        string violation = Assert.Single(
            SdkPin.Inspect(
                SdkPin.FileName,
                """
                {
                  "sdk": {
                    "version": "10.0.400",
                    "allowPrerelease": false
                  }
                }
                """));

        Assert.Contains("\"rollForward\" unstated", violation, StringComparison.Ordinal);
    }

    [Theory]
    // Prereleases allowed outright. A preview of the pinned band is installed on more machines than
    // anyone expects — this one included — and it carries analyzers of its own.
    [InlineData("""{ "sdk": { "version": "10.0.400", "rollForward": "latestPatch", "allowPrerelease": true } }""")]
    // Left unstated, which is the same exposure written more quietly: the default is not this
    // repository's to rely on, and the field costs one line to say out loud.
    [InlineData("""{ "sdk": { "version": "10.0.400", "rollForward": "latestPatch" } }""")]
    public void A_pin_that_lets_a_preview_win_is_reported(string pinText)
    {
        string violation = Assert.Single(SdkPin.Inspect(SdkPin.FileName, pinText));

        Assert.Contains("allowPrerelease", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_with_no_sdk_section_is_reported()
    {
        // global.json carries other sections — msbuild-sdks among them — so a file that exists and
        // parses is not a file that pins an SDK, and a reader that only looked for defects inside
        // the section would find none and call this clean.
        string violation = Assert.Single(
            SdkPin.Inspect(SdkPin.FileName, """{ "msbuild-sdks": { "Some.Sdk": "1.0.0" } }"""));

        Assert.Contains("no \"sdk\" section", violation, StringComparison.Ordinal);
    }

    [Theory]
    // The shape this repository uses.
    [InlineData("latestPatch")]
    // Prefers the exact version and rolls forward within the band only if it is missing. A narrower
    // policy than latestPatch, inside the same band, and equally fine.
    [InlineData("patch")]
    // No roll-forward at all: strictest of the three, and it still satisfies the rule, because the
    // rule is about leaving the band rather than about how tightly a machine is held inside it.
    [InlineData("disable")]
    public void A_pin_that_stays_inside_its_feature_band_is_clean(string rollForward)
    {
        // Asserted before the emptiness: a fixture the reader gets no pin out of also produces no
        // violations, and only one of those two means what this case claims.
        Assert.NotNull(SdkPin.Read(Pin("10.0.400", rollForward, false))?.Version);
        Assert.Empty(SdkPin.Inspect(SdkPin.FileName, Pin("10.0.400", rollForward, false)));
    }

    [Theory]
    [InlineData("10.0.400", "10.0.4xx")]
    [InlineData("10.0.100", "10.0.1xx")]
    [InlineData("9.0.203", "9.0.2xx")]
    public void A_version_names_the_band_the_way_the_SDK_documents_one(string version, string band) =>
        Assert.Equal(band, SdkPin.FeatureBand(version));

    [Fact]
    public void A_version_that_names_no_band_cannot_be_reduced_to_one() =>
        // The page rule above derives its expected text from this, so a version that named no band
        // has to stop it rather than let it assert against a string built out of nothing.
        Assert.Throws<FormatException>(() => SdkPin.FeatureBand("10.0.x"));

    [Theory]
    // A step naming a version beside the pin. Both are read — setup-dotnet prefers the explicit one
    // — so this is the shape where the file goes on saying one thing while CI installs another.
    [InlineData(
        "      - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1\n"
        + "        with:\n"
        + "          global-json-file: global.json\n"
        + "          dotnet-version: 10.0.x",
        1)]
    // A step naming a version instead of the pin, which is how this starts: someone adds a workflow,
    // copies the first example they find, and the pin quietly stops reaching CI. Two messages rather
    // than one, and deliberately: the step names a version of its own AND reads no pin, which are
    // separate repairs — deleting the version alone would leave the step installing a default.
    [InlineData(
        "      - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1\n"
        + "        with:\n"
        + "          dotnet-version: 10.0.x",
        2)]
    // A step with no inputs at all, which installs whatever the action defaults to.
    [InlineData(
        "      - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1",
        1)]
    // Reading a pin, but not this repository's. A path that resolves to some other file is a pin
    // nobody here maintains.
    [InlineData(
        "      - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1\n"
        + "        with:\n"
        + "          global-json-file: eng/global.json",
        1)]
    public void A_step_that_decides_its_SDK_somewhere_else_is_reported(string step, int expected) =>
        // Counted rather than merely non-empty, because each of these breaks a named number of the
        // rules and a reader that collapsed them into one message would still pass an emptiness
        // check while telling somebody about half of what they have to fix.
        Assert.Equal(expected, SdkPin.SetupStepViolations(IntegrationWorkflow, step).Count);

    [Theory]
    // The form both workflows use: `uses:` on the list dash, inputs indented under `with:`.
    [InlineData(
        "      - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1\n"
        + "        with:\n"
        + "          global-json-file: global.json\n"
        + "\n"
        + "      - name: Restore\n"
        + "        run: dotnet restore")]
    // The same step written with a name, which puts `uses:` on its own line at the same indent as
    // the `with:` below it. A reader that ended the step at the next line indented no deeper than
    // the `uses:` would find no inputs here and report a correctly configured step.
    [InlineData(
        "      - name: Install the pinned SDK\n"
        + "        uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1\n"
        + "        with:\n"
        + "          global-json-file: global.json")]
    // A commented-out input beside the live one. The step carries prose about this very rule, so a
    // reader folding comments in with the inputs would report the file for its own documentation.
    [InlineData(
        "      - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1\n"
        + "        with:\n"
        + "          # dotnet-version: 10.0.x — no: the version comes from the pin.\n"
        + "          global-json-file: global.json")]
    public void A_step_that_takes_its_SDK_from_the_pin_is_clean(string step)
    {
        // Asserted first, for the reason the clean pin fixtures assert it: a reader that found no
        // step reports no violations, which is not the same thing as a step that complies.
        Assert.NotEmpty(SdkPin.ReadSetupSteps(step));
        Assert.Empty(SdkPin.SetupStepViolations(IntegrationWorkflow, step));
    }

    [Fact]
    public void The_reader_finds_every_setup_step_in_a_file_rather_than_the_first()
    {
        // release.yml installs an SDK in two separate jobs, and the sweep is a fold over whatever
        // the reader returns — so a reader that stopped at the first would report green on a file
        // whose second job takes its SDK from somewhere else entirely.
        const string workflow = """
            jobs:
              build:
                steps:
                  - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1
                    with:
                      global-json-file: global.json
              publish:
                steps:
                  - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1
                    with:
                      global-json-file: eng/global.json
            """;

        Assert.Equal(2, SdkPin.ReadSetupSteps(workflow).Count);
        Assert.Single(SdkPin.SetupStepViolations(PublishingWorkflow, workflow));
    }

    [Fact]
    public void A_job_that_runs_dotnet_without_installing_one_is_reported()
    {
        // How this arrives: a second job is added to do one small thing with the toolchain — pack
        // an artifact, read a property back out of MSBuild — and the install step is not copied
        // across, because the job above already has one and the file looks configured.
        const string workflow = """
            jobs:
              test:
                steps:
                  - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1
                    with:
                      global-json-file: global.json

                  - name: Test
                    run: dotnet test Rendlio.Analyzers.slnx

              publish:
                needs: test
                steps:
                  - name: Pack
                    run: dotnet pack src/Rendlio.Analyzers/Rendlio.Analyzers.csproj
            """;

        // Asserted first, and it is the point of the case: the step-wise sweep finds nothing wrong
        // with this file, because the job at fault contains no step to be wrong.
        Assert.Empty(SdkPin.SetupStepViolations(PublishingWorkflow, workflow));

        Assert.Contains(
            "job \"publish\"",
            Assert.Single(SdkPin.MissingSetupStepViolations(PublishingWorkflow, workflow)),
            StringComparison.Ordinal);
    }

    [Theory]
    // A job that installs the SDK and then uses it, which is the shape of every job here.
    [InlineData("""
        jobs:
          build:
            steps:
              - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1
                with:
                  global-json-file: global.json

              - run: dotnet build Rendlio.Analyzers.slnx
        """)]
    // A job that runs no dotnet command needs no SDK. "Every job installs one" is not the rule —
    // it would fire on a labeler, and a rule that fires on correct files gets switched off.
    [InlineData("""
        jobs:
          label:
            steps:
              - uses: actions/labeler@8558fd74291d67161a8a78ce36a881fa63b766a9 # v5.0.0
        """)]
    public void A_job_that_needs_no_SDK_or_installs_the_one_it_uses_is_clean(string workflow)
    {
        // Asserted first, for the reason the other clean fixtures assert it: a reader that found no
        // jobs reports no violations, and that is not what this case claims.
        Assert.NotEmpty(SdkPin.ReadJobs(workflow));
        Assert.Empty(SdkPin.MissingSetupStepViolations(IntegrationWorkflow, workflow));
    }

    [Theory]
    // Both invocation shapes these workflows use, copied from them: a one-line `run:`, a command
    // inside a block, and one whose output is captured into a shell variable.
    [InlineData("        run: dotnet restore Rendlio.Analyzers.slnx --locked-mode")]
    [InlineData("          dotnet pack src/Rendlio.Analyzers/Rendlio.Analyzers.csproj --configuration Release")]
    [InlineData("          declared=$(dotnet msbuild src/Rendlio.Analyzers/Rendlio.Analyzers.csproj \\")]
    public void A_line_that_runs_the_muxer_is_a_job_that_needs_an_SDK(string line) =>
        Assert.True(SdkPin.RunsDotnet(line));

    [Theory]
    // The action that installs an SDK is not a build that uses one. A reader matching the bare word
    // would find one in every setup step, and then every job with a step would also "need" one —
    // true here by luck, and false the moment a job installs an SDK it never uses.
    [InlineData("      - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1")]
    // A path inside the package, which both workflows grep for while proving the layout.
    [InlineData("          grep -q 'analyzers/dotnet/cs/Rendlio.Analyzers.dll' <<< \"${listing}\"")]
    // Prose. These files explain their own steps at length, and talking about a build is not one.
    [InlineData("        # under `dotnet build` anywhere. Shipping on one leg's word ships a pack")]
    public void A_line_that_only_names_the_muxer_is_not_a_job_that_needs_an_SDK(string line) =>
        Assert.False(SdkPin.RunsDotnet(line));

    [Fact]
    public void The_reader_separates_jobs_rather_than_reading_the_file_as_one()
    {
        // Guards the guard from the other direction. A splitter returning the whole file as one job
        // would find the gate job's install step and count the publishing job as covered by it —
        // the same false green, reached by reading two machines as one. It also has to stop where
        // the jobs do: `defaults:` below puts a bare two-space key inside a top-level section, and
        // a reader running past the left margin would report `run` as a third job.
        const string workflow = """
            jobs:
              test:
                steps:
                  - run: dotnet test Rendlio.Analyzers.slnx

              publish:
                needs: test
                steps:
                  - run: dotnet nuget push ./artifacts/Rendlio.Analyzers.0.1.0.nupkg

            defaults:
              run:
                shell: bash
            """;

        Assert.Equal(["test", "publish"], SdkPin.ReadJobs(workflow).Select(job => job.Name));
        Assert.Equal(2, SdkPin.MissingSetupStepViolations(PublishingWorkflow, workflow).Count);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void The_pin_is_read_the_same_under_any_current_culture(string culture)
    {
        // The reject side of the roll-forward check matches OrdinalIgnoreCase, and that is the one
        // comparison here a rewrite could make culture-sensitive. The hazard is concrete rather
        // than theoretical: under tr-TR, "LATESTFEATURE".ToLower() is "latestfeature" with a
        // dotless ı, so a case-folding rewrite would stop recognising the band-crossing policy and
        // the guard would go green on the exact file shape it exists to reject. The flag alone does
        // not say which way that goes; this does.
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            var target = new CultureInfo(culture);
            CultureInfo.CurrentCulture = target;
            CultureInfo.CurrentUICulture = target;

            // Both cultures write 1.5 with a comma and the invariant one writes a dot, so a build
            // with InvariantGlobalization on would leave every assertion below vacuous.
            Assert.Equal("1,5", 1.5.ToString(target));

            Assert.Contains(
                "outside the feature band",
                Assert.Single(SdkPin.Inspect(SdkPin.FileName, Pin("10.0.400", "LATESTFEATURE", false))),
                StringComparison.Ordinal);

            Assert.Equal("10.0.4xx", SdkPin.FeatureBand("10.0.400"));
            Assert.Empty(SdkPin.Inspect(SdkPin.FileName, File.ReadAllText(PinPath)));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void A_workflow_with_CRLF_endings_is_read_the_same_as_one_without()
    {
        // .gitattributes holds this repository to LF, so nothing exercises the other ending today —
        // which is the reason to pin it rather than the reason not to. The reader strips the
        // carriage return per line; one left on the end of an input value would make
        // "global.json\r" a file this repository does not pin against, inverting the rule into
        // reporting the one workflow shape that is correct.
        string workflow = """
            steps:
              - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9 # v4.3.1
                with:
                  global-json-file: global.json
            """.ReplaceLineEndings("\r\n");

        Assert.Equal(SdkPin.FileName, Assert.Single(SdkPin.ReadSetupSteps(workflow)).PinFile);
        Assert.Empty(SdkPin.SetupStepViolations(IntegrationWorkflow, workflow));
    }

    [Theory]
    // Prose in a workflow header that names the action while explaining this rule. These files carry
    // paragraphs of it.
    [InlineData("# The SDK comes from the pin: uses: actions/setup-dotnet@<sha> with global-json-file.")]
    // A commented-out step is not a step.
    [InlineData("      # - uses: actions/setup-dotnet@67a3573c9a986a3f9c594539f4ab511d57bb3ce9")]
    // A different action entirely, whose inputs this rule has no opinion about.
    [InlineData(
        "      - uses: actions/checkout@11d5960a326750d5838078e36cf38b85af677262 # v4.4.0\n"
        + "        with:\n"
        + "          fetch-depth: 0")]
    public void A_line_that_installs_no_SDK_is_left_alone(string line)
    {
        Assert.Empty(SdkPin.ReadSetupSteps(line));
        Assert.Empty(SdkPin.SetupStepViolations(IntegrationWorkflow, line));
    }

    /// <summary>The pin this repository is held to, at the root every build resolves up to.</summary>
    private static string PinPath => Path.Combine(RepositoryLayout.Root, SdkPin.FileName);

    /// <summary>A minimal <c>global.json</c> naming the three fields the rules are about.</summary>
    private static string Pin(string version, string rollForward, bool allowPrerelease) =>
        $$"""
        {
          "sdk": {
            "version": "{{version}}",
            "rollForward": "{{rollForward}}",
            "allowPrerelease": {{(allowPrerelease ? "true" : "false")}}
          }
        }
        """;

    /// <summary>The text of the workflow called <paramref name="fileName"/>.</summary>
    private static string WorkflowText(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryLayout.Root, ".github", "workflows", fileName));

    /// <summary>Every workflow file in this repository, in a stable order.</summary>
    /// <remarks>
    /// Both extensions, because GitHub accepts both and a rule that only knew about one would be
    /// silently skippable by naming a file the other way.
    /// </remarks>
    private static List<string> WorkflowFiles()
    {
        string directory = Path.Combine(RepositoryLayout.Root, ".github", "workflows");

        return
            [.. Directory.EnumerateFiles(directory, "*.yml", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly))
                .OrderBy(path => path, StringComparer.Ordinal)];
    }
}
