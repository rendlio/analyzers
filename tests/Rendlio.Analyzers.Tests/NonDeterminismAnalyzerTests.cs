using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// RENDLIO002 against the three ambient APIs it names: <c>DateTime.Now</c>, <c>Random</c> and
/// <c>Guid.NewGuid</c>. Each is pinned as an error, and — with more care than the bans get — the
/// APIs that sit next to them and must stay legal are pinned as clean.
/// </summary>
/// <remarks>
/// The negatives are the load-bearing half. <c>Stopwatch</c> and <c>TimeProvider</c> are how a
/// timeout or a budget is written, and a rule that flagged either would turn <c>dotnet build</c> red
/// on code that is doing nothing wrong. <c>DateTime.UtcNow</c> and
/// <c>DateTimeOffset.Now</c>/<c>UtcNow</c> are off the list too — widening the ban is a
/// consumer-visible change, so they are pinned clean here to make any such widening an intentional,
/// visible test edit.
/// </remarks>
public sealed class NonDeterminismAnalyzerTests
{
    private const string Rule = "RENDLIO002";

    /// <summary>
    /// A stand-in for the project that installed the package. The name carries no meaning to the
    /// rule — see <see cref="The_verdict_does_not_depend_on_what_the_assembly_is_called"/>.
    /// </summary>
    private const string Consumer = "Consumer";

    private static Task<ImmutableArray<Diagnostic>> RunAsync(string source, string assemblyName = Consumer) =>
        AnalyzerHarness.RunAsync(new NonDeterminismAnalyzer(), assemblyName, source);

    [Fact]
    public async Task Clean_code_reports_nothing()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System.Text;

            namespace Example;

            internal static class Sut
            {
                internal static string Join(string left, string right) =>
                    new StringBuilder(left).Append(right).ToString();
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    // ---- DateTime.Now ----

    [Fact]
    public async Task DateTime_Now_is_an_error()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static int Stamp() => DateTime.Now.Year;
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.DateTime.Now'");
    }

    [Fact]
    public async Task The_fully_qualified_spelling_of_DateTime_Now_is_an_error()
    {
        // Semantic, not textual: the namespace qualification changes the syntax and not the symbol.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static System.DateTime Stamp() => System.DateTime.Now;
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.DateTime.Now'");
    }

    [Fact]
    public async Task A_using_static_directive_does_not_hide_DateTime_Now()
    {
        // The member row's half of the evasion path A_using_static_directive_does_not_hide_Random
        // covers for the type row, and it reaches the property by a different route: the call site
        // names no type at all, so what carries the diagnostic here is the member reference itself.
        // The directive is not the error — `using static System.DateTime;` imports Today and
        // MinValue too, and neither is banned.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;
            using static System.DateTime;

            namespace Example;

            internal static class Sut
            {
                internal static int Stamp() => Now.Year;
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.DateTime.Now'");
    }

    [Theory]
    [InlineData("UtcNow")]
    [InlineData("Today")]
    [InlineData("MinValue")]
    public async Task Every_other_static_of_DateTime_is_legal(string member)
    {
        // The row names DateTime.Now and stops there. UtcNow in particular is NOT banned: this case
        // failing means someone widened the rule, which is a change consumers have to be told about.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync($$"""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static DateTime Stamp() => DateTime.{{member}};
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Now")]
    [InlineData("UtcNow")]
    public async Task DateTimeOffset_is_a_different_type_and_is_legal(string member)
    {
        // Same reasoning as the case above: the row says DateTime.Now, and DateTimeOffset is not
        // DateTime.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync($$"""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static DateTimeOffset Stamp() => DateTimeOffset.{{member}};
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Modelling_a_date_that_came_out_of_the_input_is_legal()
    {
        // The type is not the hazard; the clock is. A date computed from the input is exactly as
        // deterministic as the input.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static DateTime FromSerial(double serial) =>
                    new DateTime(1899, 12, 30, 0, 0, 0, DateTimeKind.Utc).AddDays(serial);
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_property_of_the_project_own_called_Now_is_legal()
    {
        // The ban is on a symbol, never on the word.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal sealed class Deadline
            {
                internal long Now { get; init; }

                internal bool Expired(long limit) => Now > limit;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    // ---- Random ----

    [Fact]
    public async Task Constructing_a_Random_is_an_error()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static int Pick() => new Random().Next();
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Random'");
    }

    [Fact]
    public async Task Random_Shared_is_an_error_once()
    {
        // The row names the type, so Random.Shared is covered without a member row of its own —
        // and the type reference carries the diagnostic for Shared and Next both, or one statement
        // would report three times.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static int Pick() => Random.Shared.Next(10);
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Random'");
    }

    [Fact]
    public async Task A_field_typed_as_Random_is_an_error_without_being_called()
    {
        // An injected Random is still a random source: the remedy is to derive the value from the
        // input, not to move the non-determinism up one frame.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal sealed class Sut
            {
                private readonly Random? _source;

                internal Sut(Random? source) => _source = source;

                internal bool Seeded => _source is not null;
            }
            """);

        // The field's type and the constructor parameter's type: two references to one banned type.
        ShouldAllReportRandom(diagnostics, expected: 2);
    }

    [Fact]
    public async Task An_alias_does_not_hide_Random()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using Source = System.Random;

            namespace Example;

            internal static class Sut
            {
                internal static int Pick() => new Source().Next();
            }
            """);

        // Two references to one banned type: the alias declaration, and the use that resolves
        // through it. Both name the type the alias was meant to hide.
        ShouldAllReportRandom(diagnostics, expected: 2);
    }

    [Fact]
    public async Task A_using_static_directive_does_not_hide_Random()
    {
        // `using static System.Random;` reaches Shared without naming the type at the call site,
        // and the member early-out above means `Shared.Next()` reports nothing on its own — two
        // members of a banned type and no reference to the type. The directive itself names it,
        // which is what keeps the file from passing entirely.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using static System.Random;

            namespace Example;

            internal static class Sut
            {
                internal static int Pick() => Shared.Next(10);
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Random'");
    }

    [Fact]
    public async Task Random_as_a_generic_argument_is_an_error()
    {
        // The row is the type wherever it is named, not only where it is constructed.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;
            using System.Collections.Generic;

            namespace Example;

            internal static class Sut
            {
                internal static List<Random> Pool() => new();
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Random'");
    }

    [Fact]
    public async Task A_cast_to_Random_is_an_error()
    {
        // Laundering the source through `object` still has to name the type to get a number out.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static int Via(object source) => ((Random)source).Next();
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Random'");
    }

    [Fact]
    public async Task A_type_of_the_project_own_called_Random_is_legal()
    {
        // Matched on the fully-qualified name, so Example.Random is not System.Random.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal sealed class Random
            {
                internal int Next() => 4;
            }

            internal static class Sut
            {
                internal static int Pick() => new Random().Next();
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task RandomNumberGenerator_is_a_different_type_and_is_legal()
    {
        // System.Security.Cryptography.RandomNumberGenerator is not System.Random and is not on the
        // list. Cryptographic randomness is normally wanted precisely where it is unpredictable,
        // and banning it here would put this rule in the way of that.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System.Security.Cryptography;

            namespace Example;

            internal static class Sut
            {
                internal static byte[] Bytes()
                {
                    byte[] buffer = new byte[8];
                    RandomNumberGenerator.Fill(buffer);
                    return buffer;
                }
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    // ---- Guid.NewGuid ----

    [Fact]
    public async Task Guid_NewGuid_is_an_error()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static string Name() => Guid.NewGuid().ToString("N");
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Guid.NewGuid'");
    }

    [Fact]
    public async Task The_fully_qualified_spelling_of_Guid_NewGuid_is_an_error()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static System.Guid Name() => System.Guid.NewGuid();
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Guid.NewGuid'");
    }

    [Fact]
    public async Task Reading_a_Guid_that_came_out_of_the_input_is_legal()
    {
        // Only NewGuid is a row: Guid itself is an ordinary value type, and parsing one from the
        // input is exactly as deterministic as the input is.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static Guid Read(string text) =>
                    Guid.TryParse(text, out Guid parsed) ? parsed : Guid.Empty;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_method_group_of_Guid_NewGuid_is_an_error()
    {
        // The row is the API, not the invocation. Handing NewGuid out as a delegate defers the
        // non-determinism rather than removing it, and whoever invokes the delegate names nothing
        // banned at all — so if this reference were not the error, nothing would be.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static Func<Guid> Factory() => Guid.NewGuid;
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Guid.NewGuid'");
    }

    // ---- Measuring elapsed time, which MUST NOT trip ----

    [Fact]
    public async Task A_TimeProvider_based_budget_is_legal()
    {
        // The shape a timeout is written in: a monotonic timestamp taken at the start and an
        // elapsed time read off it. If this case ever fails, fix the analyzer rather than the code
        // it flagged — a false positive here is a red build with no honest remedy.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;
            using System.Threading;

            namespace Example;

            internal sealed class Governor
            {
                private readonly TimeProvider _time;
                private readonly long _startTimestamp;
                private readonly CancellationTokenSource _watchdog;

                internal Governor(TimeSpan timeout, TimeProvider timeProvider)
                {
                    _time = timeProvider;
                    _startTimestamp = timeProvider.GetTimestamp();
                    _watchdog = new CancellationTokenSource(timeout, timeProvider);
                }

                internal Governor(TimeSpan timeout)
                    : this(timeout, TimeProvider.System)
                {
                }

                internal TimeSpan Elapsed => _time.GetElapsedTime(_startTimestamp);

                internal bool Cancelled => _watchdog.IsCancellationRequested;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stopwatch_is_legal()
    {
        // Stopwatch reads a monotonic counter, not the wall clock: two runs measure different
        // elapsed times and still produce the same output, which is the property this rule is about.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;
            using System.Diagnostics;

            namespace Example;

            internal static class Sut
            {
                internal static TimeSpan Measure(Action work)
                {
                    long start = Stopwatch.GetTimestamp();
                    work();
                    return Stopwatch.GetElapsedTime(start);
                }

                internal static TimeSpan MeasureWithAnInstance(Action work)
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    work();
                    watch.Stop();
                    return watch.Elapsed;
                }
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_injected_clock_is_legal()
    {
        // The rule's own remedy must not itself be a violation.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal interface IClock
            {
                DateTime Now { get; }
            }

            internal sealed class Sut
            {
                private readonly IClock _clock;

                internal Sut(IClock clock) => _clock = clock;

                internal DateTime Stamp() => _clock.Now;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    // ---- Shared contracts ----

    [Fact]
    public async Task A_cref_to_a_banned_api_is_not_a_use()
    {
        // Documentation explaining why an API is banned must not itself be the violation.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            /// <summary>
            /// Replaces <see cref="System.DateTime.Now"/> and <see cref="System.Guid.NewGuid"/>,
            /// which this rule bans; see also <see cref="System.Random"/>.
            /// </summary>
            internal static class Sut
            {
                internal static int Run() => 1;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task All_three_rows_are_reported_from_one_file()
    {
        // The rule does not stop at the first finding, and the three rows are independent.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static DateTime Stamp() => DateTime.Now;

                internal static int Pick() => Random.Shared.Next();

                internal static Guid Name() => Guid.NewGuid();
            }
            """);

        diagnostics.Select(diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture))
            .Order(StringComparer.Ordinal)
            .Select(message => message.Split(' ')[0])
            .ShouldBe(["'System.DateTime.Now'", "'System.Guid.NewGuid'", "'System.Random'"]);
    }

    [Fact]
    public async Task Stamping_a_field_initializer_is_an_error()
    {
        // Field initialisers run outside any method body, so a rule registered only on invocations
        // would miss them — and a boot timestamp or a per-instance id is precisely where ambient
        // state gets captured with no call site left to review.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal sealed class Sut
            {
                private static readonly DateTime Boot = DateTime.Now;

                private readonly Guid _id = Guid.NewGuid();

                internal Guid Id => _id;

                internal static DateTime Started => Boot;
            }
            """);

        diagnostics.Select(diagnostic => diagnostic.GetMessage(CultureInfo.InvariantCulture).Split(' ')[0])
            .Order(StringComparer.Ordinal)
            .ShouldBe(["'System.DateTime.Now'", "'System.Guid.NewGuid'"]);
    }

    [Theory]
    [InlineData("Consumer")]
    [InlineData("Consumer.Tests")]
    [InlineData("Contoso.Billing")]
    [InlineData("Rendlio.Something")]
    public async Task The_verdict_does_not_depend_on_what_the_assembly_is_called(string assemblyName)
    {
        // The same contract RENDLIO001 keeps: scope is the package reference, so a rename cannot
        // switch the rule off and no name is special to it.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System;

            namespace Example;

            internal static class Sut
            {
                internal static Guid Name() => Guid.NewGuid();
            }
            """, assemblyName);

        diagnostics.ShouldBeSingleError(Rule, "'System.Guid.NewGuid'");
    }

    /// <summary>
    /// Asserts every diagnostic is RENDLIO002 naming <c>System.Random</c>, and that there are
    /// exactly <paramref name="expected"/> of them.
    /// </summary>
    private static void ShouldAllReportRandom(ImmutableArray<Diagnostic> diagnostics, int expected)
    {
        diagnostics.Length.ShouldBe(expected);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            diagnostic.Id.ShouldBe(Rule);
            diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
            diagnostic.GetMessage(CultureInfo.InvariantCulture).ShouldContain("'System.Random'");
        }
    }
}
