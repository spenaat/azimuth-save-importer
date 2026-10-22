#!/usr/bin/env bash
# Builds AzimuthSaveImporter.exe with Mono's C# compiler (used by CI on Linux).
# The result is a plain .NET Framework 4.8 Windows app: no install needed on Windows 10/11.
#   ./build.sh            -> dist/AzimuthSaveImporter.exe
#   ./build.sh --cli-only -> dist/AzimuthSaveImporter.Cli.exe (no WinForms, for tests on Linux)
set -euo pipefail
cd "$(dirname "$0")"
mkdir -p dist
API=/usr/lib/mono/4.5-api   # 4.5 reference assemblies: runs on every .NET Framework 4.5-4.8
REFS=(-nostdlib -r:$API/mscorlib.dll -r:$API/System.dll -r:$API/System.Core.dll
      -r:$API/System.IO.Compression.dll -r:$API/System.IO.Compression.FileSystem.dll)
COMMON=(-nologo -langversion:7 -optimize+ -warnaserror+ -codepage:utf8 -nowarn:1702)

if [[ "${1:-}" == "--cli-only" ]]; then
  mcs "${COMMON[@]}" "${REFS[@]}" -define:NO_GUI -target:exe -out:dist/AzimuthSaveImporter.Cli.exe \
      src/Program.cs src/Cli.cs src/Core/*.cs
  echo "Built dist/AzimuthSaveImporter.Cli.exe"
  exit 0
fi

mcs "${COMMON[@]}" "${REFS[@]}" -r:$API/System.Drawing.dll -r:$API/System.Windows.Forms.dll \
    -target:winexe -win32icon:assets/icon.ico \
    -out:dist/AzimuthSaveImporter.exe src/Program.cs src/Cli.cs src/Core/*.cs src/Gui/*.cs
echo "Built dist/AzimuthSaveImporter.exe"
