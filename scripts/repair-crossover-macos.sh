#!/usr/bin/env bash
set -u

APPID="3678970"
EXE_NAME="TaskBarHero.exe"
GAME_SUBDIR="steamapps/common/TaskbarHero"
REG_KEY='HKCU\Software\Wine\AppDefaults\TaskBarHero.exe\DllOverrides'
REG_VALUE="winhttp"
REG_DATA="native,builtin"

CX_APP_BUNDLE_PATH="${CX_APP_BUNDLE_PATH:-/Applications/CrossOver.app}"
CX_ROOT="${CX_ROOT:-$CX_APP_BUNDLE_PATH/Contents/SharedSupport/CrossOver}"
CX_BOTTLE_PATH="${CX_BOTTLE_PATH:-$HOME/Library/Application Support/CrossOver/Bottles}"
CX_MANAGED_BOTTLE_PATH="${CX_MANAGED_BOTTLE_PATH:-/Library/Application Support/CrossOver/Bottles}"

MODE_CHECK=1
DO_REPAIR=0
DO_LAUNCH=0
BOTTLE_ARG="${CX_BOTTLE:-}"
GAME_DIR_ARG=""

status=0

usage() {
  cat <<'EOF'
Usage:
  bash scripts/repair-crossover-macos.sh [--check] [--repair] [--launch] [--bottle NAME] [--game-dir PATH]

Modes:
  --check       Default. Inspect CrossOver, the bottle, BepInEx files, winhttp override, quarantine, and log status.
  --repair      Clear macOS quarantine from BepInEx/Doorstop files and set the TaskBarHero.exe winhttp DLL override.
  --launch      With --repair only. Stop TaskBarHero.exe, launch Steam appid 3678970, and poll BepInEx log signals.

Options:
  --bottle NAME     CrossOver bottle name. If omitted, the script scans for a bottle containing TaskBarHero.exe.
  --game-dir PATH   macOS path to the TaskbarHero game directory. Useful for non-standard Steam libraries.
  --help            Show this help.

Environment overrides:
  CX_APP_BUNDLE_PATH, CX_ROOT, CX_BOTTLE_PATH, CX_MANAGED_BOTTLE_PATH, CX_BOTTLE

Examples:
  bash scripts/repair-crossover-macos.sh --check
  bash scripts/repair-crossover-macos.sh --repair --launch
  bash scripts/repair-crossover-macos.sh --check --bottle "<your-bottle-name>"
EOF
}

info() { printf '[INFO] %s\n' "$*"; }
ok() { printf '[OK] %s\n' "$*"; }
warn() { printf '[WARN] %s\n' "$*"; status=1; }
fail() { printf '[ERROR] %s\n' "$*" >&2; exit 1; }

while [ "$#" -gt 0 ]; do
  case "$1" in
    --check)
      MODE_CHECK=1
      ;;
    --repair)
      DO_REPAIR=1
      ;;
    --launch)
      DO_LAUNCH=1
      ;;
    --bottle)
      [ "$#" -ge 2 ] || fail "--bottle requires a value"
      BOTTLE_ARG="$2"
      shift
      ;;
    --game-dir)
      [ "$#" -ge 2 ] || fail "--game-dir requires a value"
      GAME_DIR_ARG="${2%/}"
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      fail "Unknown argument: $1"
      ;;
  esac
  shift
done

if [ "$(uname -s)" != "Darwin" ]; then
  fail "This repair script supports macOS CrossOver only."
fi

if [ "$DO_LAUNCH" -eq 1 ] && [ "$DO_REPAIR" -ne 1 ]; then
  fail "--launch must be used with --repair so launch verification is tied to an explicit repair action."
fi

WINE="$CX_ROOT/bin/wine"

check_crossover() {
  [ -d "$CX_APP_BUNDLE_PATH" ] || fail "CrossOver app bundle not found: $CX_APP_BUNDLE_PATH"
  [ -d "$CX_ROOT" ] || fail "CrossOver root not found: $CX_ROOT"
  [ -x "$WINE" ] || fail "CrossOver wine wrapper not executable: $WINE"
  ok "CrossOver wrapper found: $WINE"
}

bottle_root_for_name() {
  local name="$1"
  if [ -d "$CX_BOTTLE_PATH/$name" ]; then
    printf '%s\n' "$CX_BOTTLE_PATH/$name"
    return 0
  fi
  if [ -d "$CX_MANAGED_BOTTLE_PATH/$name" ]; then
    printf '%s\n' "$CX_MANAGED_BOTTLE_PATH/$name"
    return 0
  fi
  return 1
}

detect_bottle_from_game_dir() {
  local game_dir="$1"
  local root bottle_rel bottle_name
  for root in "$CX_BOTTLE_PATH" "$CX_MANAGED_BOTTLE_PATH"; do
    [ -d "$root" ] || continue
    case "$game_dir/" in
      "$root"/*)
        bottle_rel="${game_dir#"$root"/}"
        bottle_name="${bottle_rel%%/*}"
        if [ -n "$bottle_name" ] && [ -d "$root/$bottle_name" ]; then
          printf '%s|%s\n' "$bottle_name" "$root/$bottle_name"
          return 0
        fi
        ;;
    esac
  done
  return 1
}

scan_candidates() {
  local root exe bottle_rel bottle_name game_dir
  for root in "$CX_BOTTLE_PATH" "$CX_MANAGED_BOTTLE_PATH"; do
    [ -d "$root" ] || continue
    while IFS= read -r exe; do
      game_dir="$(dirname "$exe")"
      bottle_rel="${game_dir#"$root"/}"
      bottle_name="${bottle_rel%%/*}"
      [ -n "$bottle_name" ] || continue
      printf '%s|%s|%s\n' "$bottle_name" "$root/$bottle_name" "$game_dir"
    done < <(find "$root" -path "*/$GAME_SUBDIR/$EXE_NAME" -print 2>/dev/null)
  done
}

pick_bottle_and_game() {
  local candidate_count=0
  local candidate bottle_name bottle_root game_dir
  local first_bottle="" first_root="" first_game=""

  if [ -n "$BOTTLE_ARG" ]; then
    bottle_name="$BOTTLE_ARG"
    bottle_root="$(bottle_root_for_name "$bottle_name")" || fail "Bottle not found: $bottle_name"
    if [ -n "$GAME_DIR_ARG" ]; then
      game_dir="$GAME_DIR_ARG"
    else
      game_dir="$bottle_root/drive_c/Program Files (x86)/Steam/$GAME_SUBDIR"
      if [ ! -f "$game_dir/$EXE_NAME" ]; then
        local found_count=0 found_game=""
        while IFS= read -r exe; do
          found_count=$((found_count + 1))
          found_game="$(dirname "$exe")"
        done < <(find "$bottle_root/drive_c" -path "*/$GAME_SUBDIR/$EXE_NAME" -print 2>/dev/null)
        if [ "$found_count" -eq 1 ]; then
          game_dir="$found_game"
        elif [ "$found_count" -gt 1 ]; then
          fail "Multiple TaskBarHero installs found in bottle $bottle_name; pass --game-dir."
        fi
      fi
    fi
  elif [ -n "$GAME_DIR_ARG" ]; then
    candidate="$(detect_bottle_from_game_dir "$GAME_DIR_ARG")" || fail "--game-dir is not under a known CrossOver bottle; pass --bottle too."
    bottle_name="${candidate%%|*}"
    bottle_root="${candidate#*|}"
    game_dir="$GAME_DIR_ARG"
  else
    while IFS= read -r candidate; do
      candidate_count=$((candidate_count + 1))
      if [ "$candidate_count" -eq 1 ]; then
        first_bottle="$(printf '%s' "$candidate" | awk -F'|' '{print $1}')"
        first_root="$(printf '%s' "$candidate" | awk -F'|' '{print $2}')"
        first_game="$(printf '%s' "$candidate" | awk -F'|' '{print $3}')"
      fi
      printf '[INFO] candidate: bottle=%s game=%s\n' \
        "$(printf '%s' "$candidate" | awk -F'|' '{print $1}')" \
        "$(printf '%s' "$candidate" | awk -F'|' '{print $3}')"
    done < <(scan_candidates)

    if [ "$candidate_count" -eq 0 ]; then
      fail "No CrossOver bottle containing $EXE_NAME was found. Pass --bottle and/or --game-dir."
    elif [ "$candidate_count" -gt 1 ]; then
      fail "Multiple TaskBarHero installs found. Re-run with --bottle NAME, and use --game-dir if needed."
    fi
    bottle_name="$first_bottle"
    bottle_root="$first_root"
    game_dir="$first_game"
  fi

  CX_BOTTLE="$bottle_name"
  WINEPREFIX="$bottle_root"
  GAME_DIR="$game_dir"
  export CX_APP_BUNDLE_PATH CX_ROOT CX_BOTTLE_PATH CX_MANAGED_BOTTLE_PATH CX_BOTTLE WINEPREFIX
  export PYTHONPATH="$CX_ROOT/lib/python"
  export PATH="$CX_ROOT/bin:$PATH"

  ok "Bottle: $CX_BOTTLE"
  ok "Game directory: $GAME_DIR"
}

check_required_file() {
  local path="$1"
  local label="$2"
  if [ -f "$path" ]; then
    ok "$label found"
  else
    warn "$label missing: $path"
  fi
}

has_quarantine() {
  xattr -p com.apple.quarantine "$1" >/dev/null 2>&1
}

check_quarantine() {
  local path
  local found=0
  for path in "$GAME_DIR/winhttp.dll" "$GAME_DIR/doorstop_config.ini" "$GAME_DIR/BepInEx" "$GAME_DIR/dotnet"; do
    [ -e "$path" ] || continue
    if has_quarantine "$path"; then
      warn "macOS quarantine present: $path"
      found=1
    fi
  done
  [ "$found" -eq 0 ] && ok "No macOS quarantine marker found on BepInEx/Doorstop paths"
}

query_override() {
  "$WINE" --bottle "$CX_BOTTLE" reg query "$REG_KEY" /v "$REG_VALUE" 2>&1
}

check_override() {
  local out
  out="$(query_override || true)"
  if printf '%s\n' "$out" | grep -q "$REG_DATA"; then
    ok "Wine DLL override is set: $REG_VALUE=$REG_DATA"
  else
    warn "Wine DLL override missing or different. Expected $REG_VALUE=$REG_DATA under $REG_KEY"
    printf '%s\n' "$out" | sed 's/^/[INFO] reg: /'
  fi
}

check_log() {
  local log="$GAME_DIR/BepInEx/LogOutput.log"
  if [ ! -f "$log" ]; then
    warn "BepInEx log missing: $log"
    return
  fi
  ok "BepInEx log exists: $log"
  local pattern
  for pattern in "Chainloader startup complete" "TBH DPS Meter" "Overlays created" "Patched:" "selfcheck"; do
    if grep -q "$pattern" "$log"; then
      ok "Log contains: $pattern"
    else
      warn "Log does not contain: $pattern"
    fi
  done
}

run_checks() {
  check_required_file "$GAME_DIR/$EXE_NAME" "$EXE_NAME"
  check_required_file "$GAME_DIR/winhttp.dll" "BepInEx winhttp.dll"
  check_required_file "$GAME_DIR/doorstop_config.ini" "doorstop_config.ini"
  check_required_file "$GAME_DIR/BepInEx/plugins/TBH.DpsMeter.dll" "TBH.DpsMeter.dll"
  check_override
  check_quarantine
  check_log
}

require_repair_inputs() {
  [ -f "$GAME_DIR/$EXE_NAME" ] || fail "$EXE_NAME missing: $GAME_DIR/$EXE_NAME"
  [ -f "$GAME_DIR/winhttp.dll" ] || fail "BepInEx winhttp.dll missing: $GAME_DIR/winhttp.dll"
  [ -f "$GAME_DIR/doorstop_config.ini" ] || fail "doorstop_config.ini missing: $GAME_DIR/doorstop_config.ini"
  [ -f "$GAME_DIR/BepInEx/plugins/TBH.DpsMeter.dll" ] || fail "TBH.DpsMeter.dll missing: $GAME_DIR/BepInEx/plugins/TBH.DpsMeter.dll"
}

do_repair() {
  require_repair_inputs

  info "Clearing macOS quarantine from BepInEx/Doorstop paths"
  xattr -dr com.apple.quarantine \
    "$GAME_DIR/winhttp.dll" \
    "$GAME_DIR/doorstop_config.ini" \
    "$GAME_DIR/BepInEx" \
    "$GAME_DIR/dotnet" 2>/dev/null || true

  info "Setting scoped Wine DLL override for $EXE_NAME"
  "$WINE" --bottle "$CX_BOTTLE" reg add "$REG_KEY" \
    /v "$REG_VALUE" /t REG_SZ /d "$REG_DATA" /f >/dev/null
  ok "Set $REG_KEY $REG_VALUE=$REG_DATA"
}

stat_mtime() {
  if [ -f "$1" ]; then
    stat -f '%m' "$1" 2>/dev/null || printf '0\n'
  else
    printf '0\n'
  fi
}

do_launch_verify() {
  local log="$GAME_DIR/BepInEx/LogOutput.log"
  local before_mtime
  before_mtime="$(stat_mtime "$log")"

  info "Stopping $EXE_NAME if it is running"
  "$WINE" --bottle "$CX_BOTTLE" taskkill /IM "$EXE_NAME" /F >/dev/null 2>&1 || true
  sleep 2

  info "Launching through Steam appid $APPID"
  "$WINE" --bottle "$CX_BOTTLE" --start "steam://rungameid/$APPID" >/dev/null 2>&1 || true

  info "Polling BepInEx log for startup signals"
  local i now_mtime ok_count pattern
  for i in $(seq 1 36); do
    sleep 5
    now_mtime="$(stat_mtime "$log")"
    if [ -f "$log" ] && [ "$now_mtime" -gt "$before_mtime" ]; then
      ok_count=0
      for pattern in "Chainloader startup complete" "TBH DPS Meter" "Overlays created" "Patched:" "selfcheck"; do
        grep -q "$pattern" "$log" && ok_count=$((ok_count + 1))
      done
      if [ "$ok_count" -eq 5 ]; then
        ok "BepInEx and TBH DPS Meter startup signals found"
        status=0
        return 0
      fi
    fi
    printf '[INFO] waiting for BepInEx log... (%s/36)\n' "$i"
  done

  warn "Timed out waiting for complete BepInEx/TBH DPS Meter startup signals"
  if [ -f "$log" ]; then
    tail -80 "$log" | sed 's/^/[LOG] /'
  fi
}

check_crossover
pick_bottle_and_game

if [ "$MODE_CHECK" -eq 1 ] && [ "$DO_REPAIR" -ne 1 ]; then
  run_checks
fi

if [ "$DO_REPAIR" -eq 1 ]; then
  do_repair
  status=0
  run_checks
fi

if [ "$DO_LAUNCH" -eq 1 ]; then
  do_launch_verify
fi

if [ "$status" -eq 0 ]; then
  ok "Done"
else
  warn "Done with warnings"
fi

exit "$status"
