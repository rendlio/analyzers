#!/usr/bin/env bash
#
# verify-package-layout.sh — assert that a packed Rendlio.Analyzers.nupkg contains EXACTLY the
# entries it is supposed to, and nothing else.
#
#   usage: eng/verify-package-layout.sh <path-to-nupkg>
#
# Why an exact set and not a search for the path that matters. The check this replaces asked
# whether analyzers/dotnet/cs/Rendlio.Analyzers.dll was PRESENT, which catches the failure that
# ships a package installing as a no-op — the important one, and not the only one. Measured on
# this repository: `dotnet pack -p:IncludeBuildOutput=true` adds lib/netstandard2.0/*.dll and
# lib/netstandard2.0/*.xml, and a presence check exits 0 on it. That package installs the analyzer
# AND offers a reference assembly nobody should compile against, from a project whose whole point
# is that it ships no library — and it flows differently through a consumer's graph. A gate that
# reads the whole listing has no such blind side: anything gained or lost is a diff.
#
# The expected set is written here rather than derived, because deriving it from the project file
# would be the same statement twice in the same voice and would agree with itself forever.
# `PackGateTests` in tests/Rendlio.Analyzers.Tests reads this list back and fails the build if it
# stops naming the assembly the project actually produces, stops naming the packed README, or starts
# naming a lib/ folder — so the list cannot drift away from the project, and the project cannot drift
# away from the list, without one of the two going red.
#
# Runnable by hand, and meant to be: this is the same command both workflows run, so a package
# inspected locally is inspected by the gate rather than by eye.
set -euo pipefail

nupkg="${1:-}"

if [ -z "${nupkg}" ]; then
  echo "usage: eng/verify-package-layout.sh <path-to-nupkg>" >&2
  exit 2
fi

if [ ! -f "${nupkg}" ]; then
  echo "error: no package at '${nupkg}'." >&2
  exit 2
fi

# Every entry this package may contain, sorted the way the comparison below sorts.
#
# The four OPC entries — [Content_Types].xml, _rels/.rels and the core-properties part — are
# NuGet's own container bookkeeping rather than anything this project states, so they are listed
# as what they are: required, and not ours to change.
#
# The core-properties part is matched by shape. NuGet names it after the package identity when
# packing deterministically and after a fresh GUID otherwise, and which of those a given SDK does
# is not a promise; pinning the name observed today would turn an SDK upgrade into a failure of
# this gate rather than of anything real.
expected=$(
  cat <<'ENTRIES'
Rendlio.Analyzers.nuspec
README.md
analyzers/dotnet/cs/Rendlio.Analyzers.dll
[Content_Types].xml
_rels/.rels
package/services/metadata/core-properties/<name>.psmdcp
ENTRIES
)

# LC_ALL=C on both sides: a locale-sensitive sort orders '[', '_' and letters differently, and the
# two sides of this comparison must be ordered by the same rule or every run is a diff.
expected=$(LC_ALL=C sort <<< "${expected}")

# -Z1 lists entry names and nothing else. Captured rather than piped into the normaliser because
# under `set -o pipefail` a consumer that exits early — as `grep -q` does at its first match — can
# SIGPIPE unzip and fail this script for a reason that has nothing to do with the package.
#
# Tested rather than left to `set -e`, because an archive that yields no listing would otherwise
# arrive at the comparison as an empty set and be reported as a layout diff naming every expected
# entry as missing — a true diff describing the wrong problem, which is the kind a reader acts on
# before noticing it is not the problem.
#
# One branch for both shapes of that, deliberately. Measured with this unzip: a zero-entry archive
# that is otherwise a valid zip is refused outright rather than listed as nothing, so the
# non-zero exit already covers the case a separate emptiness test would exist for. The `-z` is kept
# beside it as the belt to that brace — an unzip that chose to print nothing instead would take the
# same exit — but not as its own branch with its own sentence, which would claim a distinction
# nothing here can produce.
if ! listing=$(unzip -Z1 "${nupkg}") || [ -z "${listing}" ]; then
  echo "::error::${nupkg} did not read back as an archive with entries in it. Pack produced something that is not a package; this is not a layout problem." >&2
  exit 1
fi

echo "inspecting ${nupkg}"
echo "${listing}"
echo

actual=$(
  LC_ALL=C sort <<< "$(
    sed -E 's#^package/services/metadata/core-properties/.+\.psmdcp$#package/services/metadata/core-properties/<name>.psmdcp#' \
      <<< "${listing}"
  )"
)

if [ "${expected}" != "${actual}" ]; then
  echo "::error::${nupkg} does not contain the entries this package ships. '<' is expected and missing; '>' is present and unexpected. A lib/ entry means IncludeBuildOutput was turned on; a missing analyzers/dotnet/cs entry means the package would install cleanly and analyse nothing. Reconcile Rendlio.Analyzers.csproj and eng/verify-package-layout.sh." >&2
  diff <(echo "${expected}") <(echo "${actual}") >&2 || true
  exit 1
fi

echo "layout ok: ${nupkg} contains exactly the entries this package ships."
