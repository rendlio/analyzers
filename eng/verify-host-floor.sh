#!/usr/bin/env bash
#
# verify-host-floor.sh — install a packed Rendlio.Analyzers into throwaway consumers under a NAMED
# .NET SDK, and prove the analyzer loads in that SDK's compiler host and reports there.
#
#   usage: eng/verify-host-floor.sh <path-to-nupkg> <sdk-version> [target-framework]
#          eng/verify-host-floor.sh ./artifacts/Rendlio.Analyzers.0.1.0.nupkg 8.0.100 net8.0
#
# What this exists to answer. The README tells a consumer this package needs "Visual Studio 2022
# 17.8 or .NET SDK 8.0.100 and upwards". That is the lowest host it claims to support, it is the
# sentence someone reads before deciding whether they can install at all, and until this script
# existed nothing ran on that host: the suite builds and tests under the SDK global.json pins, and
# a floor is a claim about the OTHER end of the supported range.
#
# The static half of the claim is already held: Directory.Packages.props pins
# Microsoft.CodeAnalysis.CSharp deliberately below what the engine builds against, and a README test
# fails the build if the stated floor stops matching that pin. Compiling against Roslyn 4.8 is what
# makes post-4.8 API use impossible, so the floor cannot be broken by calling something too new.
#
# What it CANNOT rule out is AD0001 — an analyzer that compiles fine and then throws inside an older
# host, which the compiler reports as "analyzer threw an exception" and which no amount of reading
# finds. That is a runtime property of a specific compiler host and the only way to learn it is to
# run one. So this script runs the floor host, and it checks BOTH directions, because an analyzer
# that has silently stopped loading is indistinguishable from a codebase with no violations:
#
#   * a consumer that breaks both rules must FAIL, reporting RENDLIO001 and RENDLIO002;
#   * a consumer that breaks neither must build clean;
#   * neither may report AD0001, on which the run fails even where the compiler treats it as a
#     warning — an analyzer that threw did not analyse, whatever severity the host chose.
#
# Isolation is eng/local-consume/{nuget.config,Directory.Build.props}, copied in rather than
# reinvented. Those two files are why a run here grades the package just packed and not a cached copy
# of the same id and version, and each carries its own explanation of the trap it closes — read
# eng/local-consume/nuget.config first if any of the provenance checks below fail. Provenance is read
# back at the end rather than assumed, for the same reason those files exist: configuration states an
# intention, and only project.assets.json says what happened.
set -euo pipefail

nupkg="${1:-}"
sdk="${2:-}"
framework="${3:-net8.0}"

if [ -z "${nupkg}" ] || [ -z "${sdk}" ]; then
  echo "usage: eng/verify-host-floor.sh <path-to-nupkg> <sdk-version> [target-framework]" >&2
  exit 2
fi

if [ ! -f "${nupkg}" ]; then
  echo "error: no package at '${nupkg}'." >&2
  exit 2
fi

# Resolved from the file name rather than passed in, so there is no way to install one version and
# assert against another.
package=$(basename "${nupkg}")
version=$(sed -E 's/^Rendlio\.Analyzers\.(.+)\.nupkg$/\1/' <<< "${package}")

if [ "${version}" = "${package}" ]; then
  echo "error: '${package}' is not a Rendlio.Analyzers package file name, so the version cannot be read from it." >&2
  exit 2
fi

repo=$(cd "$(dirname "$0")/.." && pwd)
nupkg=$(cd "$(dirname "${nupkg}")" && pwd)/$(basename "${nupkg}")

scratch=$(mktemp -d)
# Removed however this exits. The scratch tree holds a packages folder of its own, so a leaked one
# costs real disk, and the next run wants a cold one regardless.
trap 'rm -rf "${scratch}"' EXIT

# The floor SDK's first run in a fresh install prints a page of welcome text, which buries the four
# lines of this that anyone reads. Suppressed for these builds only, as an exported variable in this
# script's own process — nothing here writes to the machine's configuration.
export DOTNET_NOLOGO=1

# And the build output is read below by matching strings in it, so it has to be the language those
# strings were written against. Every assertion here except the diagnostic-id ones is on English
# prose: measured on this box, `DOTNET_CLI_UI_LANGUAGE=de dotnet build` on a failing project prints
# `Fehler beim Buildvorgang.` while the diagnostic token itself stays English (`error CS0029`). So a
# check for "Build succeeded" is satisfied by nothing on a localized host — and this script is
# documented as one a release manager runs by hand, which is exactly where a non-English host turns
# up. Same scope as the line above: this process only, overriding whatever the caller exported.
export DOTNET_CLI_UI_LANGUAGE=en

echo "verifying ${package} against SDK ${sdk} (${framework}) in ${scratch}"

mkdir -p "${scratch}/feed"
cp "${repo}/eng/local-consume/nuget.config" "${repo}/eng/local-consume/Directory.Build.props" "${scratch}/"
cp "${nupkg}" "${scratch}/feed/"

# rollForward: disable, so this is the named SDK or nothing. The default would roll forward to the
# newest feature band installed, which on any developer box and on a runner that also installed the
# repository's pinned SDK means the floor silently stops being the thing under test — the failure
# where a gate reports on a host nobody asked about and reads green.
cat > "${scratch}/global.json" <<JSON
{
  "sdk": {
    "version": "${sdk}",
    "rollForward": "disable"
  }
}
JSON

# `|| true` so a muxer that refuses outright is reported by the sentence below rather than by
# `set -e` — which is what an absent floor SDK looks like, and the most likely way this runs wrong.
resolved=$(cd "${scratch}" && dotnet --version 2>/dev/null) || true

# The muxer answers a version it cannot satisfy with a page of advice on stdout, which would go into
# the message below as though it were the version that resolved.
#
# Judged by SHAPE, not by first character. "Anything not starting with a digit is not an answer" was
# the first attempt and it does not hold: measured, the muxer's refusal LEADS with the installed-SDK
# listing — `3.1.426 [C:\Program Files\dotnet\sdk]` — so it starts with a digit, survived the filter,
# and the error message carried eight lines of listing where its own sentence promises one version.
# A version is one line of three dot-separated numbers and nothing else; the listing fails that on
# every count.
if ! [[ "${resolved}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  resolved=""
fi

if [ "${resolved}" != "${sdk}" ]; then
  echo "::error::asked for SDK ${sdk} and the scratch tree resolved '${resolved:-nothing}'. Install ${sdk} and put it on PATH: rollForward is disabled there, so this is that SDK or no SDK, and anything else means the floor is not what got checked." >&2
  exit 1
fi

echo "host: SDK ${resolved}"

# ------------------------------------------------------------------- the two consumers

# A consumer that breaks both rules, one line per documented shape. Written before the clean one so
# that a package which reports nothing fails here rather than passing halfway.
mkdir -p "${scratch}/violating"
cat > "${scratch}/violating/Program.cs" <<'CSHARP'
using System;
using System.Net.Http;

internal static class Program
{
    private static void Main()
    {
        using var client = new HttpClient();          // RENDLIO001 — network I/O
        Console.WriteLine(DateTime.Now);              // RENDLIO002 — depends on when the build runs
        Console.WriteLine(Guid.NewGuid());            // RENDLIO002
        Console.WriteLine(new Random().Next());       // RENDLIO002
    }
}
CSHARP

# A consumer that breaks neither, in the shapes the rules must NOT report: the deterministic clock,
# and a stopwatch. An analyzer that fires here is worse than one that fires nowhere.
mkdir -p "${scratch}/clean"
cat > "${scratch}/clean/Program.cs" <<'CSHARP'
using System;
using System.Diagnostics;

internal static class Program
{
    private static void Main()
    {
        Console.WriteLine(DateTimeOffset.UtcNow.Year);
        Console.WriteLine(Stopwatch.StartNew().ElapsedTicks);
    }
}
CSHARP

for consumer in violating clean; do
  cd "${scratch}/${consumer}"
  # A project file written here rather than `dotnet new console`, which would overwrite the
  # Program.cs above and pull a template whose contents are not this repository's to pin.
  cat > Consumer.csproj <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>${framework}</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Rendlio.Analyzers" Version="${version}" PrivateAssets="all" />
  </ItemGroup>
</Project>
XML
done

# ------------------------------------------------------------------- the violating consumer

cd "${scratch}/violating"

# `|| true` because this build is SUPPOSED to fail; what is asserted is the reason, below. Captured
# rather than streamed so the assertions read the same text a reader does.
violating_log=$(dotnet build --configuration Release 2>&1) || true
echo "${violating_log}"

if grep -q 'AD0001' <<< "${violating_log}"; then
  echo "::error::the analyzer threw inside the SDK ${sdk} compiler host (AD0001). It compiles against Roslyn 4.8, so this is a runtime incompatibility with the lowest host the README claims — the exact failure this check exists to find." >&2
  exit 1
fi

for rule in RENDLIO001 RENDLIO002; do
  if ! grep -q "error ${rule}" <<< "${violating_log}"; then
    echo "::error::a consumer breaking every documented rule did not report ${rule} as an error under SDK ${sdk}. Either the analyzer was not loaded at all — which reads exactly like a codebase with no violations — or that rule no longer fires on the floor host." >&2
    exit 1
  fi
done

# The build must actually have failed, not merely mentioned the ids. RENDLIO001/002 ship at severity
# error, and a host that downgraded them to warnings would satisfy the greps above while leaving a
# consumer's build green on code this package exists to stop.
if grep -q 'Build succeeded' <<< "${violating_log}"; then
  echo "::error::a consumer breaking every documented rule BUILT under SDK ${sdk}. Both rules ship at severity error; a build that succeeded means the floor host is not enforcing them." >&2
  exit 1
fi

echo "violating consumer: failed with RENDLIO001 and RENDLIO002, no AD0001."

# ------------------------------------------------------------------- the clean consumer

cd "${scratch}/clean"

clean_log=$(dotnet build --configuration Release 2>&1) || {
  echo "${clean_log}"
  echo "::error::a consumer breaking no rule failed to build under SDK ${sdk}. A false positive on the floor host is a package that cannot be installed there at all." >&2
  exit 1
}
echo "${clean_log}"

if grep -q 'AD0001' <<< "${clean_log}"; then
  echo "::error::the analyzer threw inside the SDK ${sdk} compiler host (AD0001) on code that breaks no rule." >&2
  exit 1
fi

if grep -qE 'RENDLIO[0-9]{3}' <<< "${clean_log}"; then
  echo "::error::a consumer breaking no rule was reported on under SDK ${sdk}. A rule that fires on the deterministic clock or a stopwatch is worse than one that fires nowhere." >&2
  exit 1
fi

echo "clean consumer: built with nothing reported."

# ------------------------------------------------------------- provenance, not configuration

# NuGet keys its packages folder by id and version alone, so an extraction of
# rendlio.analyzers/<version> that happened in some earlier run answers every later restore of it —
# on any branch, from any feed. A verdict above is about the package just packed only if this section
# holds, and the environment can override every setting that puts it there: NUGET_PACKAGES outranks
# the config silently, which is what the copied Directory.Build.props is for. Configuration states an
# intention; this reads back what happened.
assets="${scratch}/clean/obj/project.assets.json"

# Separators normalised on the way out, because NuGet writes a Windows path into JSON with its
# backslashes escaped.
# `|| true` on both, and not as a shrug. Each is a reader over a JSON shape NuGet owns, not this
# repository: `grep -o` finding nothing exits 1, `grep -c` counting nothing exits 1, and under
# `set -euo pipefail` either would kill the script at the assignment — BEFORE the branch below can
# say what went wrong. A gate that exits non-zero with no annotation is the failure the sibling
# script's own comment calls the kind a reader acts on before noticing it is not the problem. Nothing
# is tolerated by this: an empty result is caught immediately below and reported as what it is.
folders=$(
  sed -n '/"packageFolders"/,/^  }/p' "${assets}" \
    | grep -oE '"[^"]+": \{\}' \
    | sed -E -e 's/^"(.*)": \{\}$/\1/' -e 's#\\\\#/#g'
) || true
folder_count=$(grep -c . <<< "${folders}") || true

# NONE means the reader above stopped reading, which is a different problem from a failure of
# isolation and gets said differently: an assets file always carries packageFolders, so zero is this
# script failing to parse a shape that moved rather than a verdict about the run.
if [ "${folder_count:-0}" -eq 0 ]; then
  echo "::error::could not read any packageFolders entry out of ${assets}. That section is always written, so this is the reader in this script failing on a shape NuGet changed — not a verdict about the isolation." >&2
  exit 1
fi

# Exactly one. More than one means a fallback folder is still in play — Visual Studio installs one
# machine-wide and NUGET_FALLBACK_PACKAGES names another — so restore could have satisfied this id
# and version out of it without downloading, and the verdict above would be about whatever was
# extracted there first.
if [ "${folder_count}" -ne 1 ]; then
  echo "::error::the consumer restored through ${folder_count} packages folders rather than one, so this run was not isolated and its verdict is about some other build." >&2
  echo "${folders}" >&2
  exit 1
fi

# And it is the scratch one. Matched on the scratch directory's own leaf name rather than on the
# path as a whole, because the two sides are the same directory written two ways: `mktemp -d`
# reports a POSIX path and NuGet records the Windows one for the same folder, so on a developer's
# box a full-path comparison fails on a run that was perfectly isolated. The leaf is `mktemp`'s
# random component, which both spellings carry, and the assertion that actually settles it is the
# one below — a fact about the filesystem rather than about how a path was spelled.
leaf=$(basename "${scratch}")

case "${folders}" in
  *"/${leaf}/packages") ;;
  *)
    echo "::error::the consumer restored into '${folders}', which is not the packages folder under ${scratch}. NUGET_PACKAGES overrides the isolation silently, so a path here outside the scratch tree means this run graded a cached copy of the same id and version." >&2
    exit 1
    ;;
esac

# The decisive one: the package is extracted in the scratch tree, so whatever path spelling the
# assets file used, restore put it here.
#
# Lower-cased, because NuGet normalises both halves of the folder it extracts into and a version is
# allowed an upper-case prerelease label — `0.1.0-RC.1` is a legal tag by the grammar release.yml
# accepts, and it lands in `0.1.0-rc.1`. Compared as written, a release cut with one of those would
# fail here claiming the package was never extracted.
metadata="${scratch}/packages/rendlio.analyzers/$(tr '[:upper:]' '[:lower:]' <<< "${version}")/.nupkg.metadata"

if [ ! -f "${metadata}" ]; then
  echo "::error::${metadata} does not exist, so rendlio.analyzers/${version} was not extracted into the scratch tree and this run installed some other copy of it." >&2
  exit 1
fi

# And that copy came from the scratch feed rather than from nuget.org. Once a version of this
# package exists publicly, "restored into an isolated folder" and "restored the package just
# packed" stop being the same statement.
if ! grep -qF "/${leaf}/feed" <<< "$(sed 's#\\\\#/#g' "${metadata}")"; then
  echo "::error::${metadata} does not name the scratch feed as its source, so the package installed is not the one just packed." >&2
  cat "${metadata}" >&2
  exit 1
fi

echo "provenance ok: restored from the scratch feed into ${folders}"
echo
echo "host floor ok: ${package} loads and reports in the SDK ${sdk} compiler host."
