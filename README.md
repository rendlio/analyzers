# Rendlio.Analyzers

Two Roslyn analyzers for .NET codebases that must not talk to the network, and must produce
the same bytes every time they run:

- **A network-API ban.** Opening a socket, resolving a host or issuing an HTTP request is a
  compile error, not a code-review comment.
- **A non-deterministic-API ban.** Reading the wall clock, the random number generator, or
  ambient machine state is a compile error, so output cannot silently start depending on
  *when* or *where* a build ran.

Apache-2.0. Free to use, fork and vendor.

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

## Status

This repository currently holds the build, packaging and test scaffolding for the pack. The
rule implementations are not in it yet, and `Rendlio.Analyzers` is not on NuGet yet — so
nothing here is installable today. The convention tests that every rule must satisfy are in
place already (`tests/Rendlio.Analyzers.Tests`), and they are what the rules land against.

## Where the rules are developed

The rules are developed against the Rendlio Sheets engine, in the engine's own repository,
because that is the codebase they are load-bearing for — a rule is only as good as the real
source it is run against, and that source is where the false positives show up. This
repository is the published home of the pack: the rules arrive here as releases rather than
being edited in two places.

## Rule ids

Rule ids are family-scoped: `RENDLIO` followed by three digits (`RENDLIO001`, `RENDLIO002`,
…). Ids are never reused for a different rule, so a suppression a consumer writes today keeps
meaning what they meant by it. Every rule carries a help link to its own documentation page;
the test suite fails the build if one does not.

## Building

Requires the .NET SDK pinned in `global.json`.

```sh
dotnet build Rendlio.Analyzers.slnx
dotnet test Rendlio.Analyzers.slnx
```

The analyzer assembly targets `netstandard2.0`. That is a Roslyn requirement rather than a
preference: a compiler host may be .NET Framework (Visual Studio) or .NET (`dotnet build`),
and `netstandard2.0` is the only target both can load.

## Contributing

Issues and pull requests are welcome. Two things worth knowing before you open one:

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
