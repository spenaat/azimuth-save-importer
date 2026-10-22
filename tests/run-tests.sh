#!/usr/bin/env bash
# End-to-end tests for the command-line importer, using synthetic saves.
# Needs mono (mono-devel) and python3.   ./tests/run-tests.sh
set -uo pipefail
cd "$(dirname "$0")/.."
bash ./build.sh --cli-only >/dev/null || exit 1
CLI=(mono "$PWD/dist/AzimuthSaveImporter.Cli.exe")
REF=(python3 -I "$PWD/tests/reference.py")
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
FAILED=0
pass() { echo "  ok    $1"; }
fail() { echo "  FAIL  $1"; FAILED=1; }
digest() { (cd "$1" && find . -type f | LC_ALL=C sort | xargs -d '\n' sha256sum | sha256sum); }

# --- fixtures ----------------------------------------------------------------
make_xbox() {  # an XBsync "Download all" folder
  local x=$1; mkdir -p "$x"
  local u="$x/User_900000ABCDEF1,savedgame"; mkdir -p "$u"
  head -c 2000 /dev/urandom > "$u/BinarySaveState"; head -c 6000 /dev/urandom > "$u/PlayerDatabase"
  head -c 300 /dev/urandom > "$u/SaveState"; printf '\x06\x00\x00\x00' > "$u/VersionFlags"
  printf '\x01\x00\x00\x00\x00\x00\x00\x00' > "$u/TransactionLogFile"
  local l="$x/Livery_0289_20150618180709,savedgame"; mkdir -p "$l"
  head -c 700 /dev/urandom > "$l/C_livery"; head -c 114 /dev/urandom > "$l/header"; head -c 900 /dev/urandom > "$l/bigThumb.png"
  local p="$x/Photo_0000_20150618195656,savedgame"; mkdir -p "$p"
  head -c 88 /dev/urandom > "$p/header"; head -c 5000 /dev/urandom > "$p/image"; : > "$p/metadata"
}
make_azimuth() {  # an Azimuth install with one save written "by Azimuth"
  local root=$1 player="$1/data/titles/1A96545B/saves/player-1"
  local rev="$player/Tuning_1115_20261022114106/revisions/0000000000000001"
  mkdir -p "$rev/data"
  head -c 362 /dev/urandom > "$rev/data/Data"; head -c 300 /dev/urandom > "$rev/data/Thumb.png"; head -c 112 /dev/urandom > "$rev/data/header"
  "${REF[@]}" commit "$rev/data" > "$rev/commit"
  local prof="$player/User_9000000000000/revisions/0000000000000049"; mkdir -p "$prof/data"
  head -c 500 /dev/urandom > "$prof/data/SaveState"; "${REF[@]}" commit "$prof/data" > "$prof/commit"
  local photo="$player/Photo_0000_20261022114026/revisions/0000000000000001"; mkdir -p "$photo/data"
  head -c 50 /dev/urandom > "$photo/data/image"; "${REF[@]}" commit "$photo/data" > "$photo/commit"
}
P=data/titles/1A96545B/saves/player-1
make_xbox "$WORK/xbox"

echo "commit format"
mkdir -p "$WORK/golden/data"; printf '\x06\x00\x00\x00' > "$WORK/golden/data/VersionFlags"
# This line appears verbatim in commit files written by Azimuth.
if "${REF[@]}" commit "$WORK/golden/data" | grep -qx '7aa8ca4a02506da9133d8f889678b76f716ce45d02e22fdb7b70a15e56a0eff8 4 VersionFlags'
then pass "reference matches a line Azimuth wrote"; else fail "reference matches a line Azimuth wrote"; fi
make_azimuth "$WORK/v"
out=$("${CLI[@]}" verify --target "$WORK/v")
if [[ $out == *"reproduced 3 existing commit files"* ]]; then pass "verify reproduces existing commits"; else fail "verify: $out"; fi

echo "import, replace everything"
make_azimuth "$WORK/a"
"${CLI[@]}" import --source "$WORK/xbox" --target "$WORK/a" --mode replace --yes --backup-dir "$WORK/a-backups" >/dev/null \
  && pass "exit code 0" || fail "exit code"
"${REF[@]}" check "$WORK/xbox" "$WORK/a/$P" && pass "every container matches the reference" || fail "containers"
[[ $(ls "$WORK/a/$P" | wc -l) -eq 3 ]] && pass "old saves removed" || fail "old saves removed: $(ls "$WORK/a/$P")"
[[ ! -e "$WORK/a/$P/User_9000000000000/revisions/0000000000000001/data/TransactionLogFile" ]] && pass "Xbox-only file left out" || fail "TransactionLogFile"
[[ $(ls "$WORK/a-backups"/*.zip | wc -l) -eq 1 ]] && pass "backup written" || fail "backup written"
[[ -z $(ls -a "$WORK/a/data/titles/1A96545B/saves" | grep azimuth-importer) ]] && pass "no staging left behind" || fail "staging left behind"

echo "restore"
make_azimuth "$WORK/r"; before=$(digest "$WORK/r/$P")
"${CLI[@]}" import --source "$WORK/xbox" --target "$WORK/r" --mode replace --yes --backup-dir "$WORK/r-backups" >/dev/null
"${CLI[@]}" restore --backup "$(ls "$WORK/r-backups"/*.zip)" --target "$WORK/r" --yes --backup-dir "$WORK/r-backups" >/dev/null
[[ $before == $(digest "$WORK/r/$P") ]] && pass "restore gives back the exact previous saves" || fail "restore"
[[ $(ls "$WORK/r-backups"/*.zip | wc -l) -eq 2 ]] && pass "restore backs up first" || fail "restore backs up first"

echo "import from ZIP, merge"
(cd "$WORK/xbox" && python3 -I -m zipfile -c "$WORK/xbox.zip" *)
make_azimuth "$WORK/m"
"${CLI[@]}" import --source "$WORK/xbox.zip" --target "$WORK/m" --mode merge --yes --backup-dir "$WORK/m-backups" >/dev/null \
  && pass "exit code 0" || fail "exit code"
"${REF[@]}" check "$WORK/xbox" "$WORK/m/$P" && pass "every container matches the reference" || fail "containers"
[[ -d "$WORK/m/$P/Tuning_1115_20261022114106" && $(ls "$WORK/m/$P" | wc -l) -eq 5 ]] && pass "other saves kept" || fail "merge kept: $(ls "$WORK/m/$P")"
out=$("${CLI[@]}" import --source "$WORK/xbox.zip" --target "$WORK/m" --dry-run)
[[ $(grep -c "already imported, identical" <<<"$out") -eq 3 ]] && pass "re-import detected as identical" || fail "identical detection"

echo "safety"
make_azimuth "$WORK/s"
"${CLI[@]}" import --source "$WORK/xbox" --target "$WORK/s" --yes >/dev/null 2>&1
[[ $? -eq 2 ]] && pass "existing saves need --mode" || fail "existing saves need --mode"
c=$(ls "$WORK/s/$P/Tuning_1115_20261022114106/revisions/0000000000000001/commit"); sed -i 's/blobs 3/blobs 4/' "$c"
before=$(digest "$WORK/s")
"${CLI[@]}" import --source "$WORK/xbox" --target "$WORK/s" --mode replace --yes --backup-dir "$WORK/s-backups" >/dev/null 2>&1
[[ $? -eq 1 && $before == $(digest "$WORK/s") && ! -e "$WORK/s-backups" ]] && pass "unknown commit format: refused, nothing changed" || fail "format mismatch"

mkdir -p "$WORK/e/data/titles/1A96545B/saves"
"${CLI[@]}" import --source "$WORK/xbox" --target "$WORK/e" --yes >/dev/null 2>&1
[[ $? -eq 1 && ! -e "$WORK/e/data/titles/1A96545B/saves/player-1" ]] && pass "no Azimuth save to verify: refused" || fail "unverified refused"
"${CLI[@]}" import --source "$WORK/xbox" --target "$WORK/e" --yes --allow-unverified >/dev/null 2>&1 \
  && "${REF[@]}" check "$WORK/xbox" "$WORK/e/$P" && pass "--allow-unverified creates player-1" || fail "--allow-unverified"

"${CLI[@]}" import --source "$WORK/a/$P" --target "$WORK/e" --yes >/dev/null 2>&1
[[ $? -eq 1 ]] && pass "an Azimuth folder is refused as source" || fail "Azimuth folder as source"

make_azimuth "$WORK/f"; touch "$WORK/f/$P/Livery_0289_20150618180709"; before=$(digest "$WORK/f")
"${CLI[@]}" import --source "$WORK/xbox" --target "$WORK/f" --mode merge --yes --backup-dir "$WORK/f-backups" >/dev/null 2>&1
[[ $? -eq 1 && $before == $(digest "$WORK/f") ]] && pass "failure while applying: backup restored automatically" || fail "rollback"

echo
if [[ $FAILED -eq 0 ]]; then echo "All tests passed."; else echo "Some tests FAILED."; fi
exit $FAILED
