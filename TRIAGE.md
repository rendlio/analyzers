# Triage policy

`Rendlio.Analyzers` is free to use, and a free package with no stated support posture is a
broken promise. This page is the posture: what happens to an issue you open, what is in
and out of scope, and how priorities get decided when they conflict.

It applies from the first published release onward, and that release exists: the pack is on
nuget.org and installable today. So a report about a rule can be recorded, answered, and
fixed in a version you can install — always a new version, because a published one can be
unlisted but never replaced.

## Where to report

- **A false positive, a missed violation, a crash, a packaging or documentation defect:**
  [GitHub issues](https://github.com/Rendlio/analyzers/issues) on this repository.
- **A suspected security problem:** use the private route in the
  [security policy](SECURITY.md) rather than an issue, and please keep the detail off any
  public thread until there is a release to upgrade to. That is the page GitHub links
  from this repository's Security tab, and it is where the route is specified — including
  what to do if the private route is not available to you, and what we do in return. The
  acknowledgment window below applies to it.
- **A rule you would like to see, or a ban you need to bend:** GitHub issues, after
  reading "How priorities get decided" below.

Issues are public on purpose — the answer to "why does `RENDLIO001` fire here?" is almost
always useful to more than one reader. Apart from the security route above there is no
private channel for this package and no paid support tier. If you need a guaranteed
response time, this is not a package that can give you one, and we would rather say so
than let you find out during an incident.

## Response expectations

These are the numbers we hold ourselves to. They are deliberately modest, and they are
real — which is worth more to you than an ambitious number nobody meets.

- **A new issue is read and labelled within ten working days.** Labelled means triaged by
  a human: in scope or not, reproduced or waiting on something specific from you, and a
  note saying which.
- **A security report is acknowledged within five working days.**
- **No fix deadline.** A confirmed bug enters the queue in the priority order below and
  ships in the next release. There is no hotfix channel and no committed date.
- **An issue we close, we close with a reason.** "Cannot reproduce" and "out of scope" are
  both legitimate answers, and both come with the reason attached. Reopen it if you have
  what was missing.
- **Silence is a mistake, not an answer.** If two weeks pass with nothing on your issue,
  say so in a comment — we missed it.

## What makes a report actionable

Analyzer bugs are cheap to fix and expensive to reproduce, so the repro carries the
report:

- The **rule id** and the diagnostic message, verbatim from the build log.
- The **smallest code that shows it** — for a false positive, the fewest lines that still
  fire it; for a missed violation, the call you expected to be flagged.
- The **compiler host**: `dotnet --version` or the Visual Studio version, and whether it
  also reproduces on a clean command-line build rather than only in the IDE.
- What you **expected instead**, and why.

We never need your source tree, your build server or your credentials. If the only way to
answer a question is to look at code you cannot share, say so and we will work from a
description.

## In scope

- **False positives.** A rule that fires on correct code is the worst defect this pack can
  have: it breaks a build that should be green. These are triaged ahead of everything
  else.
- **Missed violations.** An API that falls inside one of the two bans and is not flagged.
- **Crashes.** An analyzer that throws inside a build, however it surfaces in the log.
- **Load and packaging defects.** The analyzer not reaching the compiler at all, anything
  from the package leaking into a consumer's output, the package flowing transitively to a
  consumer's consumers, or wrong licence and metadata on the feed.
- **Build-time cost.** A slowdown you can measure and attribute to these rules.
- **Diagnostic text and help links.** A message a stranger cannot act on, or a help link
  that goes nowhere, is a bug — a diagnostic is read in someone else's build log, with
  none of our context around it.
- **Suppression that does not work.** Severity configuration, `#pragma warning disable`,
  `[SuppressMessage]` and `NoWarn` are part of the contract; if a documented route to quiet
  a rule fails, that is a bug.
- **Documentation that contradicts behaviour**, including this page.

## Out of scope

- **Relaxing a ban because a codebase wants the API.** Suppression and severity
  configuration exist for that, and they are yours to set. See the priority rules below
  for when a default moves instead.
- **Compiler hosts older than the floor.** The pack builds against a Roslyn API version
  chosen to keep it loadable from Visual Studio 2022 17.8 and .NET SDK 8.0.100 onward.
  Older hosts are not supported, and raising that floor would itself be a breaking change.
- **Languages other than C#.** The package installs a C# analyzer and nothing else.
- **The engine these rules are developed against.** A question about Rendlio Sheets itself
  belongs with that product, not with this rule pack.
- **General .NET, Roslyn or build help**, review of your codebase, and rule authoring on
  request.
- **Forks.** Apache-2.0 means you can fork, vendor and modify this freely, and we would
  rather you did that than wait on us — but we cannot triage what a fork does.

## How priorities get decided

**The engine's needs govern.** These two bans are load-bearing in one specific codebase:
they are how Rendlio Sheets keeps its promises that the renderer never phones home and
that the same input renders to the same bytes. That is not a disclaimer — it is why the
rules are maintained at all, and it settles the hard cases:

- A change that improves a rule in general but weakens it in that codebase is not taken as
  written. Where the general case really is different, the answer is to make the behaviour
  configurable, not to move the default.
- Default severity and default ban lists are set by what that codebase needs. Both are
  yours to change in your own build, and doing so is expected rather than a workaround.
- External feature requests are genuinely welcome, and some will be better than what we
  would have arrived at alone. But a request that only serves a codebase we cannot see
  waits behind one that serves the codebase we can.

Priority order, highest first: build-breaking false positives → crashes → load and
packaging defects → missed violations → misleading diagnostic text or documentation →
configurability → new rules.

## Contributing a fix

Pull requests are welcome, with one thing worth knowing before you spend an evening on
one: the rules are developed against the engine's own source, in the engine's repository,
and arrive here as releases. The README says why. In practice:

- A change to **anything that lives in this repository** — tests, documentation,
  packaging, build — is reviewed and merged here as usual.
- A change to **a rule's behaviour** cannot be. It has to land where the rule is
  developed, so it can be run against the codebase it is load-bearing for. We will treat
  your diff as the specification, implement it there, credit you, and it reaches you in
  the next release. Tell us in the issue if you would rather we did not use your patch
  that way.

Two standing rules constrain what we can accept, in either place:

- **A rule id is never reused** for a different rule, so a suppression you write today
  keeps meaning what you meant by it.
- **A diagnostic message stands on its own** — no references to specifications, trackers
  or repositories the reader cannot open. The test suite enforces this, and it applies to
  the pages in this repository too.

## Licence

Apache-2.0 — see [LICENSE](LICENSE). Nothing on this page changes it: the licence,
warranty disclaimer included, is what you are entitled to. This policy is what we intend
to do beyond it.
