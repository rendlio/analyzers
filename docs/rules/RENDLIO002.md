# RENDLIO002 — Non-deterministic API

| | |
| --- | --- |
| **Category** | `Rendlio.Determinism` |
| **Severity** | Error |
| **Applies to** | every project that references the package |

Code that must produce the same output from the same input cannot read the wall clock, draw a random
number, or mint an identifier out of thin air. RENDLIO002 makes each of those a compile error, so
output cannot silently start depending on *when* or *where* a build ran.

## What it reports

| Banned | Note |
| --- | --- |
| `System.DateTime.Now` | the wall clock, and the machine's time zone with it |
| `System.Random` | the type, so `new Random()`, `Random.Shared` and a `Random` field are all reported |
| `System.Guid.NewGuid` | a fresh value on every call, including as a method group |

```csharp
// error RENDLIO002: 'System.Guid.NewGuid' makes output vary between runs and is banned
//                   in this project — inject the value or derive it from the input
string name = Guid.NewGuid().ToString("N");
```

The remedy is not prescribed, because there are two good ones and the choice is yours: take the
value as a parameter, or derive it from a hash of the input.

## What it does not report

The list is closed, and it is shorter than "no clock". These stay legal, deliberately:

- **`Stopwatch` and `TimeProvider`.** Measuring elapsed time off a monotonic counter is how a
  timeout or a budget is written. Two runs measure different durations and still produce the same
  bytes, which is the property this rule is about.
- **`DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now` and `DateTimeOffset.UtcNow`.** Not on
  the list. Adding one would turn code that built yesterday red, so it is a change consumers get
  told about rather than a quiet widening.
- **`DateTime` and `Guid` themselves.** A date computed from the input, or a `Guid.Parse` of an
  identifier that was already in the file, is exactly as deterministic as the input is.
- **`System.Security.Cryptography.RandomNumberGenerator`.** A different type, and one that is
  normally wanted precisely because it is unpredictable.
- **Your own `Now` property or `Random` type.** Detection is semantic: names are bound and the
  resolved symbol compared.

## Where a diagnostic lands

On the *type* reference where the ban is a type — `Random.Shared.Next()` is one error on `Random`,
not three — and on the member reference where the ban is a member. A `using static System.Random;`
that never names the type at a call site is reported on the directive, which is the only place the
type is named at all.

Field and property initialisers are in scope: a boot timestamp or a per-instance id is exactly where
ambient state gets captured with no call site left to review.

## Turning it off, in part or in whole

Scope is the package reference: a project that does not reference the package never loads the rule.
Within a project that does, the ordinary mechanisms apply.

```ini
# .editorconfig — the whole rule, for this project or this folder
dotnet_diagnostic.RENDLIO002.severity = none

# or the category, which is deliberately separate from the security one
dotnet_analyzer_diagnostic.category-Rendlio.Determinism.severity = warning
```

```csharp
#pragma warning disable RENDLIO002 // one call site, with a comment saying why
```

Test projects are the common case for a blanket exemption: seeding a `Random` and stamping a temp
path is ordinary there. The cleanest way to say so is to leave the package reference off those
projects.

## Related

- [RENDLIO001](RENDLIO001.md) — the banned-API ban.
- [All rules](README.md) — the index: id allocation, the two categories, and what a suppression
  costs.
- [Triage policy](../../TRIAGE.md) — what happens to an issue or a pull request about this rule.
