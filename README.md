# Rendlio.Analyzers

Two Roslyn analyzers for .NET codebases that must not talk to the network, and must produce
the same bytes every time they run:

- **A network-API ban.** Opening a socket, resolving a host or issuing an HTTP request is a
  compile error, not a code-review comment — and so are the neighbouring ways out of the same
  box: spawning a process, loading code at run time, resolving a type from a name, declaring a
  P/Invoke.
- **A non-deterministic-API ban.** Reading the wall clock, the random number generator, or
  ambient machine state is a compile error, so output cannot silently start depending on
  *when* or *where* a build ran.

Apache-2.0. Free to use, fork and vendor.

## Rules

| Rule | What it bans |
| --- | --- |
| [RENDLIO001](https://github.com/Rendlio/analyzers/blob/main/docs/rules/RENDLIO001.md) | Network I/O, process spawning, dynamic code loading, type-name reflection, native interop declarations. |
| [RENDLIO002](https://github.com/Rendlio/analyzers/blob/main/docs/rules/RENDLIO002.md) | `DateTime.Now`, `System.Random`, `Guid.NewGuid`. |

Both report at severity error by default: installing the package is how you ask for that. Each
page says what the rule deliberately does *not* report, and how to scope or suppress it.

The [rules index](https://github.com/Rendlio/analyzers/blob/main/docs/rules/README.md) is the
full list, and carries what is true of every rule rather than of one: how ids are allocated, what
the two categories are for, every way to turn a rule off, and what each of them costs.

## Installing

```sh
dotnet add package Rendlio.Analyzers
```

or, as a `PackageReference`:

```xml
<PackageReference Include="Rendlio.Analyzers" Version="0.1.0" PrivateAssets="all" />
```

`PrivateAssets="all"` keeps the analyzers out of your own package's dependencies: they check
*your* build, and nobody who installs *you* should inherit them. `dotnet add package` writes it
for you, because the package declares itself a development dependency.

Expect the first build against a codebase that has never been held to these rules to fail. That
is the package working rather than a misconfiguration.

The analyzer assembly loads into the compiler host rather than into your program, so nothing
here reaches your output and your own target framework does not matter. What does matter is the
host: this package builds against Roslyn 4.8, so it needs Visual Studio 2022 17.8 or .NET SDK
8.0.100 and upwards.

## Why this exists

Rendlio Sheets makes two promises about its rendering engine: it never phones home, and the
same input renders to the same bytes. Both are promises a reader has no practical way to
check. An engine could acquire a telemetry call or a `DateTime.Now` in any commit, and no
amount of documentation would notice.

These analyzers are how those two promises are kept honest in the engine's own build. They
run on every commit against the engine's source, and a violation fails the build. That is the
entire point: the properties are compiler-enforced rather than asserted in prose, so they hold
because they *cannot* be broken quietly, not because someone remembered to check.

They are published here, free, because neither invariant is specific to a spreadsheet
renderer. Air-gapped deployments, reproducible builds, offline licence verification, and any
codebase audited for data egress all need exactly these two bans — and there is no reason to
make anyone write them a second time.

## Where the rules are developed

The rules are developed against the Rendlio Sheets engine, in the engine's own repository,
because that is the codebase they are load-bearing for — a rule is only as good as the real
source it is run against, and that source is where the false positives show up. **That
repository is authoritative for what a rule detects. This one is the published home of the
pack, and is synced from it.**

What that means in practice:

- **A change to a rule's behaviour is made there first** and arrives here as a release sync:
  one commit bringing the rule and its own tests across together. Nothing about detection is
  edited only here, or the two would drift and the pack would stop matching the codebase it is
  proved against.
- **The published shape is decided here.** Packaging, the diagnostic text a stranger reads,
  the help link every rule carries, the per-rule pages under `docs/rules/`, and the conventions
  in `tests/Rendlio.Analyzers.Tests`. A rule that arrives citing something a reader cannot look
  up fails the build here — which is why the sync is a real step and not a copy.
- **Rule ids are allocated once across both repositories**, so an id never means two different
  things. The next rule published here may therefore not be the next number.
- **If the development home ever moves to this repository**, the direction of the sync reverses
  and this section changes to say so. Nothing else does: same ids, same package, same licence.

## Rule ids

Rule ids are family-scoped: `RENDLIO` followed by three digits (`RENDLIO001`, `RENDLIO002`,
…). Ids are never reused for a different rule, so a suppression a consumer writes today keeps
meaning what they meant by it. Every rule carries a help link to its own documentation page;
the test suite fails the build if one does not, or if the page it points at is missing.

## Building

Requires a .NET SDK from the `10.0.4xx` feature band. `global.json` pins `10.0.400` and rolls
forward only inside that band, so a machine without it stops with a message naming what to install
rather than building.

The strictness is the point. The analyzers that decide whether this repository compiles ship inside
the SDK, and a different feature band bundles a different rule set — so with warnings as errors, a
band that drifts is a build that is green in CI and red on somebody's desk on the same commit, with
nothing in the diff to explain it. CI installs the version pinned here and nothing else; holding
local resolution to the same band is what keeps the two verdicts one verdict.

```sh
dotnet build Rendlio.Analyzers.slnx
dotnet test Rendlio.Analyzers.slnx
```

The committed `packages.lock.json` files record the dependency graph the pinned versions resolve
to, so a build here restores what CI restores. Changing a version in `Directory.Packages.props`
rewrites them on the next restore; commit that with the change, because CI restores in locked mode
and stops on a graph the lock files do not describe.

The analyzer assembly targets `netstandard2.0`. That is a Roslyn requirement rather than a
preference: a compiler host may be .NET Framework (Visual Studio) or .NET (`dotnet build`),
and `netstandard2.0` is the only target both can load.

## Support and contributing

Issues and pull requests are welcome. What happens to one — response expectations, what is in
and out of scope, and how priorities are decided — is written down in the
[triage policy](https://github.com/Rendlio/analyzers/blob/main/TRIAGE.md).

A suspected security problem goes through the private route in the
[security policy](https://github.com/Rendlio/analyzers/blob/main/SECURITY.md) instead — please
keep the detail off any public thread until there is a release to upgrade to.

Two things worth knowing before you open either:

- **The bans track a real codebase.** These rules exist to hold the Rendlio Sheets engine to
  its promises, so the engine's needs decide what a rule does by default. A ban that is right
  in general but wrong for that codebase will be turned into something configurable rather
  than adopted as-is.
- **A rule is public text.** A diagnostic message is read by strangers in their own build
  logs, so it has to stand on its own — no references to specifications, trackers or
  repositories the reader cannot open. The test suite enforces this.

## Licence

Apache-2.0 — see [LICENSE](https://github.com/Rendlio/analyzers/blob/main/LICENSE).

This is a separate licence from the Rendlio Sheets engine itself, which is source-available
under its own terms in its own repository. The two are licensed independently: taking these
analyzers puts you under Apache-2.0 and nothing else.

## About

Rendlio is built by a Swiss association in formation, with profits pledged to charities.
