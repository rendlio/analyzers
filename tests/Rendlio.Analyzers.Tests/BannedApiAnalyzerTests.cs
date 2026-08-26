using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Rendlio.Analyzers.Tests;

/// <summary>
/// RENDLIO001 against its banned-API table, row by row. Every positive case is paired with the
/// nearest legal call it must not swallow, because a ban that also fires on
/// <c>Activator.CreateInstance(typeof(T))</c>, on <c>object.GetType()</c>, or on a method the
/// project itself calls <c>Process</c> would get suppressed rather than obeyed.
/// </summary>
public sealed class BannedApiAnalyzerTests
{
    private const string Rule = "RENDLIO001";

    /// <summary>
    /// A stand-in for the project that installed the package. The name carries no meaning to the
    /// rule — see <see cref="The_verdict_does_not_depend_on_what_the_assembly_is_called"/>.
    /// </summary>
    private const string Consumer = "Consumer";

    private const string NativeInterop = """
        using System.Runtime.InteropServices;

        namespace Example;

        internal static class Sut
        {
            [DllImport("native")]
            internal static extern int Version();
        }
        """;

    /// <summary>
    /// A <c>LibraryImport</c> declaration with its implementing part written out by hand. In a real
    /// build the interop source generator supplies that part; these compilations run no generators,
    /// and the analyzer reads the attribute either way.
    /// </summary>
    private const string SourceGeneratedNativeInterop = """
        using System.Runtime.InteropServices;

        namespace Example;

        internal static partial class Sut
        {
            [LibraryImport("native")]
            internal static partial int Version();

            internal static partial int Version() => 0;
        }
        """;

    /// <summary>
    /// The same P/Invoke as <see cref="NativeInterop"/>, spelled as a local <c>static extern</c>
    /// function. Legal C# since 9.0, and a real P/Invoke — the compiler emits the interop stub.
    /// <c>LibraryImport</c> has no local-function spelling: it requires a partial method.
    /// </summary>
    private const string LocalFunctionNativeInterop = """
        using System.Runtime.InteropServices;

        namespace Example;

        internal static class Sut
        {
            internal static int Version()
            {
                [DllImport("native")]
                static extern int Inner();

                return Inner();
            }
        }
        """;

    /// <summary>Two local-function P/Invokes: one inside another local function, one inside a lambda.</summary>
    private const string NestedLocalFunctionNativeInterop = """
        using System;
        using System.Runtime.InteropServices;

        namespace Example;

        internal static class Sut
        {
            internal static int Nested()
            {
                return Outer();

                static int Outer()
                {
                    [DllImport("native")]
                    static extern int Deep();

                    return Deep();
                }
            }

            internal static Func<int> InsideALambda() => () =>
            {
                [DllImport("native")]
                static extern int FromLambda();

                return FromLambda();
            };
        }
        """;

    private static Task<ImmutableArray<Diagnostic>> RunAsync(string source, string assemblyName = Consumer) =>
        AnalyzerHarness.RunAsync(new BannedApiAnalyzer(), assemblyName, source);

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

    // ---- No process spawning — System.Diagnostics.Process, all members ----

    [Fact]
    public async Task Process_start_is_an_error()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static void Run() => System.Diagnostics.Process.Start("cmd");
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Diagnostics.Process'");
    }

    [Fact]
    public async Task A_field_typed_as_Process_is_an_error_without_being_called()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal sealed class Sut
            {
                private System.Diagnostics.Process? _held;

                internal bool Idle => _held is null;
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Diagnostics.Process'");
    }

    [Fact]
    public async Task A_method_of_the_project_own_called_Process_is_legal()
    {
        // The ban is on a symbol, never on the word. "Process" is ordinary vocabulary, and a
        // textual rule would make it unusable.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static int Process(int value) => value + 1;

                internal static int Run() => Process(1);
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    // ---- No dynamic code ----

    [Theory]
    [InlineData("Load")]
    [InlineData("LoadFrom")]
    [InlineData("LoadFile")]
    public async Task Assembly_loading_is_an_error(string member)
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync($$"""
            namespace Example;

            internal static class Sut
            {
                internal static object Run() => System.Reflection.Assembly.{{member}}("Foo");
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, $"'System.Reflection.Assembly.{member}'");
    }

    [Fact]
    public async Task AssemblyLoadContext_LoadFromStream_is_an_error()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static object Run(System.IO.Stream part) =>
                    System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromStream(part);
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Runtime.Loader.AssemblyLoadContext.LoadFromStream'");
    }

    [Fact]
    public async Task The_rest_of_AssemblyLoadContext_is_legal()
    {
        // Only LoadFromStream is a row — the type itself is not banned.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static string? Run() => System.Runtime.Loader.AssemblyLoadContext.Default.Name;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reflection_Emit_is_an_error()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static object Run() => typeof(System.Reflection.Emit.DynamicMethod);
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Reflection.Emit.DynamicMethod'");
    }

    [Fact]
    public async Task Reflection_outside_Emit_is_legal()
    {
        // The row is System.Reflection.Emit.*, not System.Reflection.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static object Run() => typeof(System.Reflection.MethodInfo);
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    // ---- No reflection over type names ----

    [Fact]
    public async Task Activator_CreateInstance_from_a_name_is_an_error()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static object? Run() => System.Activator.CreateInstance("Asm", "Type");
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Activator.CreateInstance'");
    }

    [Fact]
    public async Task Activator_CreateInstance_from_a_resolved_Type_is_legal()
    {
        // The row is CreateInstance(string, ...): the hazard is the type *name*, not the API.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static object? Run() => System.Activator.CreateInstance(typeof(Sut));
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task Type_GetType_from_a_name_is_an_error()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static System.Type? Run(string name) => System.Type.GetType(name);
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Type.GetType'");
    }

    [Fact]
    public async Task Object_GetType_is_legal()
    {
        // object.GetType() is a different symbol from Type.GetType(string), and ordinary code uses it.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static string Run(object value) => value.GetType().Name;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    // ---- No network — System.Net.* ----

    [Theory]
    [InlineData("System.Net.Http.HttpClient")]
    [InlineData("System.Net.Sockets.Socket")]
    [InlineData("System.Net.WebRequest")]
    [InlineData("System.Net.Dns")]
    public async Task Every_System_Net_api_the_row_names_is_an_error(string type)
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync($$"""
            namespace Example;

            internal static class Sut
            {
                internal static object Run() => typeof({{type}});
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, $"'{type}'");
    }

    [Fact]
    public async Task An_unused_System_Net_import_is_not_a_use()
    {
        // Nothing is reached, so nothing is used. An import that reaches nothing is a redundant
        // using, which the compiler and the IDE already report on their own terms.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System.Net.Http;

            namespace Example;

            internal static class Sut
            {
                internal static int Run() => 1;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_alias_does_not_hide_a_banned_type()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using Client = System.Net.Http.HttpClient;

            namespace Example;

            internal static class Sut
            {
                internal static object Run() => typeof(Client);
            }
            """);

        // Two references to one banned type: the alias declaration, and the use that resolves
        // through it. Both name the type the alias was meant to hide.
        diagnostics.Length.ShouldBe(2);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            diagnostic.Id.ShouldBe(Rule);
            diagnostic.GetMessage(CultureInfo.InvariantCulture).ShouldContain("'System.Net.Http.HttpClient'");
        }
    }

    [Fact]
    public async Task A_cref_to_a_banned_api_is_not_a_use()
    {
        // Documentation explaining why an API is banned must not itself be the violation.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            /// <summary>
            /// Replaces <see cref="System.Diagnostics.Process"/>, which this rule bans.
            /// </summary>
            internal static class Sut
            {
                internal static int Run() => 1;
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_using_static_does_not_hide_a_banned_member()
    {
        // The one bypass the type-anchored design has to answer for. BannedApiAnalyzer skips
        // members of a banned type on the grounds that "the type is named somewhere in the project,
        // and that name is where the error belongs" — `using static` is the construct that moves
        // that name out of the call site, so the import had better still be the diagnostic.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using static System.Diagnostics.Process;

            namespace Example;

            internal static class Sut
            {
                internal static void Run() => Start("cmd");
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Diagnostics.Process'");
    }

    [Fact]
    public async Task The_System_Net_row_is_the_whole_namespace_tree_not_only_the_four_names_it_lists()
    {
        // The row is the tree; Sockets, Http, WebRequest and Dns are a gloss on it rather than the
        // extent of it. IPAddress is in none of the four and is banned — narrowing the table to the
        // listed names is what this case would go red for.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace Example;

            internal static class Sut
            {
                internal static object Run() => typeof(System.Net.IPAddress);
            }
            """);

        diagnostics.ShouldBeSingleError(Rule, "'System.Net.IPAddress'");
    }

    [Fact]
    public async Task A_namespace_that_merely_starts_with_a_banned_one_is_legal()
    {
        // SymbolFacts.IsWithinNamespace requires a '.' after the banned root, so the ban on
        // System.Net does not reach a "System.Nettle". Nothing in the BCL is shaped like that, so
        // the snippet declares the namespace itself — the separator check has no other test.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            namespace System.Nettle
            {
                internal static class Knitting
                {
                    internal static int Stitches => 1;
                }
            }

            namespace Example
            {
                internal static class Sut
                {
                    internal static int Run() => System.Nettle.Knitting.Stitches;
                }
            }
            """);

        diagnostics.ShouldBeEmpty();
    }

    // ---- Native interop ----

    [Fact]
    public async Task DllImport_is_an_error()
    {
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(NativeInterop);

        diagnostics.ShouldBeSingleError(Rule, "A [DllImport] declaration");
    }

    [Fact]
    public async Task LibraryImport_is_an_error_once_rather_than_once_per_partial_half()
    {
        // Roslyn merges the attributes of a partial method onto both halves, so the naive symbol
        // action reports the same declaration twice at the same span.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(SourceGeneratedNativeInterop);

        diagnostics.ShouldBeSingleError(Rule, "A [LibraryImport] declaration");
    }

    [Fact]
    public async Task DllImport_on_a_local_function_is_an_error()
    {
        // Roslyn drives symbol actions from a named type's declared members and never raises them
        // for a local function, so a rule registered only on symbols sees half the ways C# spells a
        // P/Invoke — and this half is a real one: drop the attribute and the compiler demands it
        // back with CS0626.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(LocalFunctionNativeInterop);

        diagnostics.ShouldBeSingleError(Rule, "A [DllImport] declaration");
    }

    [Fact]
    public async Task A_local_function_is_reached_however_deeply_it_is_nested()
    {
        // One diagnostic per declaration, and the syntax action sees a local function wherever it
        // sits — inside another local function, or inside a lambda body.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync(NestedLocalFunctionNativeInterop);

        diagnostics.Length.ShouldBe(2);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            diagnostic.Id.ShouldBe(Rule);
            diagnostic.GetMessage(CultureInfo.InvariantCulture).ShouldContain("A [DllImport] declaration");
        }
    }

    [Fact]
    public async Task Both_spellings_of_the_same_P_Invoke_report_identically()
    {
        // The two dispatch paths share one diagnostic factory precisely so they cannot drift, and
        // this is what drift would look like: a different message, or a squiggle somewhere else.
        // The span is the attribute the developer wrote — NativeInteropLocation's fallback to the
        // method's own location is there for a P/Invoke inherited from a referenced assembly, which
        // has no syntax in this compilation to point at, and must not be what a source declaration
        // hits.
        Diagnostic onAMember = (await RunAsync(NativeInterop)).ShouldHaveSingleItem();
        Diagnostic onALocalFunction = (await RunAsync(LocalFunctionNativeInterop)).ShouldHaveSingleItem();

        Span(onAMember, NativeInterop).ShouldBe(Span(onALocalFunction, LocalFunctionNativeInterop));
        Span(onAMember, NativeInterop).ShouldBe("""DllImport("native")""");
        onALocalFunction.GetMessage(CultureInfo.InvariantCulture)
            .ShouldBe(onAMember.GetMessage(CultureInfo.InvariantCulture));

        static string Span(Diagnostic diagnostic, string source) =>
            source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);
    }

    // ---- Scope ----

    [Theory]
    [InlineData("Consumer")]
    [InlineData("Consumer.Tests")]
    [InlineData("Contoso.Billing")]
    [InlineData("Rendlio.Something")]
    public async Task The_verdict_does_not_depend_on_what_the_assembly_is_called(string assemblyName)
    {
        // What this rule applies to is decided by the package reference: a project that installed
        // the pack asked for the ban, whatever it is named, and one that did not never loads the
        // analyzer at all. A rule that read the assembly name would enforce differently on a
        // renamed project and quietly enforce on nobody in a consumer's solution.
        ImmutableArray<Diagnostic> diagnostics = await RunAsync("""
            using System.Runtime.InteropServices;

            namespace Example;

            internal static class Sut
            {
                internal static void Spawn() => System.Diagnostics.Process.Start("cmd");

                internal static object Net() => typeof(System.Net.Http.HttpClient);

                internal static object Load() => System.Reflection.Assembly.Load("Foo");

                [DllImport("native")]
                internal static extern int Version();

                internal static int Local()
                {
                    [DllImport("native")]
                    static extern int Inner();

                    return Inner();
                }
            }
            """, assemblyName);

        diagnostics.Length.ShouldBe(5);
        diagnostics.ShouldAllBe(diagnostic => diagnostic.Id == Rule);
    }
}
