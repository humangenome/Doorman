#!/usr/bin/env bash
# Builds Doorman, runs its tests, and packs dist/Doorman-v<version>.zip
# (the DLL and its licence note, both under Mods/, the same layout the mod's own package uses,
# so it can be laid over a game folder as it is). Prints the zip's SHA-256 for anybody who pins
# a download by its hash. Nothing is uploaded from here.
set -euo pipefail
cd "$(dirname "$0")"

version=$(grep -oP 'public const string Version = "\K[^"]+' src/Doorman/DoormanMod.cs)
project_version=$(grep -oP '<Version>\K[^<]+' src/Doorman/Doorman.csproj)
if [ -z "$version" ] || [ "$version" != "$project_version" ]; then
  echo "version mismatch: DoormanMod.cs says '$version', the project file says '$project_version'" >&2
  exit 1
fi

echo "== tests"
dotnet run --project tests/Doorman.Tests -c Release

echo "== build $version"
rm -rf src/Doorman/bin src/Doorman/obj
dotnet build src/Doorman -c Release --nologo -v quiet
dll=src/Doorman/bin/Release/Doorman.dll
[ -s "$dll" ] || { echo "no DLL was built" >&2; exit 1; }

# the version baked into the file must be the one being packed (a stale build has shipped before)
if ! strings -e l "$dll" | grep -qx "$version"; then
  echo "the built DLL does not carry version $version" >&2
  exit 1
fi

# nothing of the build machine inside the file, in either text encoding
for enc in s l; do
  if strings -e "$enc" "$dll" | grep -qF -e "$PWD" -e '/home/' -e '\\wsl' -e 'C:\Users\'; then
    echo "the built DLL carries a build path" >&2
    exit 1
  fi
done

# it must not ask players for anything: no client companion is declared
if strings -e s "$dll" | grep -q 'S1DSClientCompanion'; then
  echo "the built DLL declares a client companion; players would have to install something" >&2
  exit 1
fi

echo "== pack"
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/Mods" dist
cp "$dll" "$stage/Mods/Doorman.dll"
cp NOTICE.txt "$stage/Mods/Doorman-LICENSE.txt"
# fixed times and order, so the same source gives the same zip and the same SHA
find "$stage" -exec touch -d '2026-01-01T00:00:00Z' {} +
zip_path="$PWD/dist/Doorman-v$version.zip"
rm -f "$zip_path"
(cd "$stage" && TZ=UTC zip -X -q "$zip_path" Mods/Doorman.dll Mods/Doorman-LICENSE.txt)

echo "zip:     dist/Doorman-v$version.zip ($(stat -c %s "$zip_path") bytes)"
echo "dll:     $(sha256sum "$dll" | cut -d' ' -f1)"
echo "sha256:  $(sha256sum "$zip_path" | cut -d' ' -f1)"
