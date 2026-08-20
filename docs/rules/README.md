# Rules

Every rule this package ships, with its own page. A rule that is not listed here is not shipped.

| Rule | Title | Category | Default severity | What it bans |
| --- | --- | --- | --- | --- |
| [RENDLIO001](RENDLIO001.md) | Banned API | `Rendlio.Security` | Error | Network I/O, process spawning, dynamic code loading, type-name reflection, native interop declarations |
| [RENDLIO002](RENDLIO002.md) | Non-deterministic API | `Rendlio.Determinism` | Error | `DateTime.Now`, `System.Random`, `Guid.NewGuid` |

Each page says what its rule reports, what it deliberately does *not* report, where the squiggle
lands, and how to scope or suppress it.

## Both default to error

Installing the package is how a project asks for that. A ban that reports as a warning is a ban
the build can be green without, which over a few months means it is not a ban — the whole reason
to compile-enforce these two properties rather than write them down is that prose does not fail a
build and a compiler does.

That default is a starting point, not a lock. Every mechanism below is available to a project that
wants a different one, and lowering the severity is the ordinary way to adopt the pack on a
codebase that does not pass yet.

## Rule ids

Ids are family-scoped: `RENDLIO` followed by three digits. An id belongs to one rule forever — it
is never reused for a different one, and never re-pointed at a different meaning — so a suppression
a consumer writes today keeps saying what its author meant by it for as long as the file lives.

Ids are allocated once across every repository these rules are developed in, so a gap in the
sequence is expected and carries no meaning. The next rule published here may not be the next
number.

Every rule carries a help link to its page in the table above. That is enforced rather than
reviewed: the test suite fails the build if a shipped rule has no help link, or if the page it
points at is not in this repository.

## Categories

Two, and they are deliberately not one:

- `Rendlio.Security` — the sealed box. Nothing reaches the network, the host or the loader.
- `Rendlio.Determinism` — the same input produces the same output, on every run and every machine.

They are separate because they are separate wants. A project may need reproducible output while
being perfectly happy to spawn a process, or need a sealed box while stamping a build time into
its own output. Configuring one category says nothing about the other, and the test suite pins
that: turning off either one leaves the other reporting at error.

## Turning a rule off

Three mechanisms, narrowest first. Each rule page repeats these spelled out with its own id.

**One call site**, with a comment saying why. This is the one to reach for first, because it is the
only suppression that leaves its reason next to the code it applies to:

```csharp
#pragma warning disable RENDLIO001 // <why this call site is legitimate>
// ...
#pragma warning restore RENDLIO001
```

**One rule, for a project or a folder**, in `.editorconfig`. `none` removes it; `warning` keeps the
diagnostic and stops it failing the build, which is what a codebase adopting the pack usually wants
first:

```ini
[*.cs]
dotnet_diagnostic.RENDLIO001.severity = none
dotnet_diagnostic.RENDLIO002.severity = warning
```

**A whole category**, in the same file. Keyed on the category rather than on an id, so it also
covers any later rule published into that category:

```ini
[*.cs]
dotnet_analyzer_diagnostic.category-Rendlio.Security.severity = warning
```

**And the widest one, which is not a suppression at all:** scope is the package reference. A
project that does not reference the package never loads these rules, and nothing in either rule
reads the assembly name or looks at anything else to decide who it applies to. Leaving the
reference off a test project is cleaner than suppressing the rules inside it, because seeding a
`Random` and stamping a temp path are ordinary there and always will be.

## What a suppression costs

A suppression is a local exception to a global promise, so what it costs depends entirely on how
local it stays.

A `#pragma` around one call site costs almost nothing: it is visible in review, it is greppable,
and the comment beside it says why. A rule set to `none` for a folder costs the property for that
folder — which is a real answer when the folder genuinely does not need it, and an invisible one
when it does, because nothing in the build will mention it again.

The honest reading of a broad suppression is that the project does not want the ban. Dropping the
package reference for that project says so more clearly, in a place someone will find, than a
severity line does.

## Related

- [Package overview](../../README.md) — what this is, how to build it, and why it exists.
- [Triage policy](../../TRIAGE.md) — what happens to an issue or a pull request about a rule, and
  the private route for reporting a suspected security problem.
