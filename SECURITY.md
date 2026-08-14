# Security policy

`Rendlio.Analyzers` is a build-time package. The analyzer assembly loads into the
compiler host rather than into your program, so nothing in it runs when your
application runs. That bounds what a vulnerability here can be, and it does not remove
the question: a package that loads into your build reads your source and runs on your
build machine.

This page is the private route for telling us about one. Everything else an issue can
be — a false positive, a crash, a packaging or documentation defect — goes through the
ordinary flow the [triage policy](TRIAGE.md) describes.

## Reporting a vulnerability

Use **[Report a vulnerability](https://github.com/Rendlio/analyzers/security/advisories/new)**
on this repository's Security tab. It opens a thread only you and the maintainers can
read, and it asks you to trust no mailbox of ours — GitHub carries it.

If that route is not available to you, open an ordinary
[issue](https://github.com/Rendlio/analyzers/issues) saying only *that* you have a
security report and how to reach you; we will take the detail off the public thread
rather than leave you without a route. Either way, **please do not put the detail
itself in a public issue, pull request or discussion** — that publishes it while there
is still nothing to upgrade to.

What we ask in return is a chance to publish a fixed release before you write it up.
There is no paid bounty programme. We will credit you in the release notes unless you
would rather we did not.

## What happens next

- **Acknowledged within five working days.** That is the window the
  [triage policy](TRIAGE.md#response-expectations) states, and acknowledged means a
  human has read the report and told you whether we can reproduce it or what is still
  missing.
- **No fix deadline.** A confirmed report enters the queue and ships in the next
  release. There is no hotfix channel and no committed date — this is a free package,
  and you are better served by knowing that now than during an incident.
- **A fix arrives as a new version.** A published version can be unlisted but never
  replaced, so the remedy for any defect, this kind included, is a release you upgrade
  to.
- **Silence is a mistake, not an answer.** If the window passes with nothing from us,
  say so on the thread — we missed it.

## What is worth reporting

The surface is narrow enough to be worth naming:

- **These analyzers reaching outside the compilation.** The rules exist to prove that a
  codebase does no network I/O and nothing non-deterministic, so a pack that did either
  itself would be the sharpest defect it could have. It reads the code being compiled
  and nothing besides — no files, no sockets, no processes.
- **Anything from this package reaching your build output**, or flowing on to the
  consumers of your own package. It is declared a development dependency and packs no
  dependencies of its own for that reason, and a case where that fails belongs here
  rather than in the ordinary queue.
- **Package integrity.** A release is published only by this repository's own release
  workflow, authenticated per run with a short-lived credential; no long-lived
  publishing key exists to be leaked. A package claiming to be this one from any other
  origin is worth telling us about.
- **A crash or hang that hostile input triggers**, as opposed to merely unusual input.
  An analyzer that throws on odd-but-legitimate code is an ordinary bug: it is in scope
  on the [triage policy](TRIAGE.md#in-scope), and reporting it in public helps the next
  reader.

If you are unsure which of those you have, use the private route. Misrouting a report
costs us a moment; publishing one early costs every consumer.

## Supported versions

Fixes go to the latest published version. There are no long-term support branches and
no backports to older versions — upgrading is the supported path, and the pack is small
enough, with a narrow enough dependency surface, that upgrading is meant to be cheap.

## Licence

Apache-2.0 — see [LICENSE](LICENSE). Nothing on this page changes it: the licence,
warranty disclaimer included, is what you are entitled to. This page is what we intend
to do beyond it.
