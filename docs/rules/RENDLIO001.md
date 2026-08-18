# RENDLIO001 — Banned API

| | |
| --- | --- |
| **Category** | `Rendlio.Security` |
| **Severity** | Error |
| **Applies to** | every project that references the package |

Reaching the network, spawning a process, loading code that the build never saw, or resolving a
type from a name are all ways out of the box a project is supposed to stay inside. RENDLIO001 makes
each of them a compile error rather than a code-review comment.

## What it reports

| Banned | Reason given in the message |
| --- | --- |
| `System.Diagnostics.Process`, all members | no process spawning |
| `System.Reflection.Emit.*`, the whole namespace tree | no dynamic code |
| `System.Reflection.Assembly.Load` / `LoadFrom` / `LoadFile`, every overload | no dynamic code |
| `System.Runtime.Loader.AssemblyLoadContext.LoadFromStream` | no dynamic code |
| `System.Activator.CreateInstance(string, …)` | no reflection over input-derived type names |
| `System.Type.GetType(string)` | no reflection over input-derived type names |
| `System.Net.*`, the whole namespace tree | zero network I/O; zero phone-home |
| `[DllImport]` and `[LibraryImport]` declarations | native interop can reach all of the above |

```csharp
// error RENDLIO001: 'System.Net.Http.HttpClient' is banned in this project
//                   — zero network I/O; zero phone-home
using var client = new HttpClient();
```

## What it does not report

- **The word, as opposed to the symbol.** A method of your own called `Process`, a parameter called
  `process`, a type of your own called `Random` — the rule binds every name it sees and compares the
  resolved symbol, so an alias (`using P = System.Diagnostics.Process;`) or a fully-qualified call is
  caught, and a coincidence of naming is not.
- **`Activator.CreateInstance(typeof(T))` and `object.GetType()`.** The hazard is the type *name*,
  because a name can come out of untrusted input. The overloads that take an already-resolved `Type`
  are a different symbol and are left alone.
- **`System.Reflection` outside `Emit`.** Reading metadata is not emitting code.
- **A `<see cref="…"/>` in a documentation comment.** Explaining why an API is banned must not
  itself be the violation.

## Where a diagnostic lands

On the *type* reference, not on the member reached through it: `Process.Start(…)` is one error on
`Process`, not two. A `[DllImport]` is reported on the attribute, so the squiggle sits on the
declaration rather than on the whole method — including the local-function spelling, which is a real
P/Invoke and is reached through a second code path for exactly that reason.

## Native interop has no allowlist

A P/Invoke can reach the network, the host and the loader, and no analyzer can see past the call, so
a project that declares one has opted out of every row above whether it meant to or not. The rule
therefore reports every `[DllImport]` and `[LibraryImport]` declaration and grants no exemptions of
its own. A project that legitimately needs native interop scopes the rule the way any analyzer is
scoped — see below.

## Turning it off, in part or in whole

Scope is the package reference: a project that does not reference the package never loads the rule.
Within a project that does, the ordinary mechanisms apply.

```ini
# .editorconfig — the whole rule, for this project or this folder
dotnet_diagnostic.RENDLIO001.severity = none

# or the category, which also covers any later rule in it
dotnet_analyzer_diagnostic.category-Rendlio.Security.severity = warning
```

```xml
<!-- .csproj — the whole rule, for this project; silences it despite the name -->
<NoWarn>$(NoWarn);RENDLIO001</NoWarn>
```

```csharp
#pragma warning disable RENDLIO001 // one call site, with a comment saying why
```

If you find yourself suppressing this rule broadly, the honest reading is that the project does not
want the ban — dropping the package reference for it says so more clearly than a suppression does.

## Related

- [RENDLIO002](RENDLIO002.md) — the non-deterministic-API ban.
- [All rules](README.md) — the index: id allocation, the two categories, and what a suppression
  costs.
- [Triage policy](../../TRIAGE.md) — what happens to an issue or a pull request about this rule.
