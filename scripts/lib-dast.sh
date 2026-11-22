# shellcheck shell=bash
# Sourced by scripts/dast-scan.sh and tested directly by scripts/test-dast.sh. Not run on its own.
#
# The decisions the scan makes, as functions, so a test can reach each one. Nothing here prints a
# secret or anything a scan found: a function prints a count, a status class or a fixed word.

# dast_zap_finished <exit code>: 0 when ZAP's exit code means the scan finished. ZAP exits 0 when it
# found nothing to report, 1 or 2 when it has FAIL or WARN alerts (alerts are judged from the JSON
# report, not from this code), and 3 or anything else when it could not run.
dast_zap_finished() {
  case "${1:-}" in
    0 | 1 | 2) return 0 ;;
    *) return 1 ;;
  esac
}

# dast_url_count <zap log file>: prints the number from the line ZAP's zap-api-scan.py prints after
# the active scan ("Total of 253 URLs", the count of URLs it holds for the target). Prints nothing
# and returns 1 when the line is not there, because ZAP prints a warning instead when it has none.
dast_url_count() {
  local n
  n="$(sed -n 's/^Total of \([0-9][0-9]*\) URLs$/\1/p' "${1:-/dev/null}" 2> /dev/null | tail -n 1)"
  [ -n "$n" ] || return 1
  printf '%s' "$n"
}

# dast_url_floor_ok <count> <floor>: 0 when both are numbers and the count is at least the floor.
dast_url_floor_ok() {
  case "${1:-}${2:-}" in '' | *[!0-9]*) return 1 ;; esac
  [ -n "${1:-}" ] && [ -n "${2:-}" ] && [ "$1" -ge "$2" ]
}

# dast_status_class <http status>: prints 2xx, 4xx and so on, or "none" when it is not a status.
dast_status_class() {
  case "${1:-}" in
    [1-5][0-9][0-9]) printf '%sxx' "${1%??}" ;;
    *) printf 'none' ;;
  esac
}

# dast_token_state <http status>: prints ok for a 2xx, expired for 401, limited for 429, failed for
# anything else. It returns 0 only for ok. The caller decides what each means before and after a pass.
dast_token_state() {
  case "${1:-}" in
    2[0-9][0-9]) printf 'ok' ;;
    401) printf 'expired'; return 1 ;;
    429) printf 'limited'; return 1 ;;
    *) printf 'failed'; return 1 ;;
  esac
}

# dast_retry_after: reads response headers on stdin and prints the seconds to wait before one retry
# of a request the API answered with 429. It is the Retry-After the response gives, at least 1 and
# at most 60. A response without one (or with a date instead of seconds) gets 5.
dast_retry_after() {
  local n
  n="$(tr -d '\r' | sed -n 's/^[Rr]etry-[Aa]fter:[[:space:]]*\([0-9][0-9]*\)[[:space:]]*$/\1/p' | head -n 1)"
  case "$n" in '' | *[!0-9]*) printf '5'; return 0 ;; esac
  n=$((10#$n))
  [ "$n" -ge 1 ] || n=1
  [ "$n" -le 60 ] || n=60
  printf '%s' "$n"
}

# shellcheck disable=SC2034 # REPORT_DIR and REPORT_CREATED are read by the caller
# dast_choose_report_dir <DAST_REPORT_DIR or empty> <work dir>: sets REPORT_DIR and REPORT_CREATED.
# With a folder named, that folder is used, and REPORT_CREATED is 1 only when this call made it.
# Without one, a new mktemp folder is made outside the work folder, because the work folder holds
# the secrets and is deleted on every exit while the reports are kept on a developer's machine.
dast_choose_report_dir() {
  local named="${1:-}" work="${2:-}"
  REPORT_CREATED=0
  if [ -n "$named" ]; then
    if [ ! -e "$named" ]; then
      mkdir -p "$named" || return 1
      REPORT_CREATED=1
    fi
    REPORT_DIR="$named"
  else
    REPORT_DIR="$(mktemp -d)" || return 1
    REPORT_CREATED=1
  fi
  case "$REPORT_DIR/" in "${work%/}/"*) return 1 ;; esac
  return 0
}

# dast_delete_reports <in CI, 1 or 0> <report folder was created here, 1 or 0>: 0 when the report
# folder is to be deleted. CI deletes what this script made and never a folder the caller brought.
dast_delete_reports() {
  [ "${1:-0}" = 1 ] && [ "${2:-0}" = 1 ]
}

# dast_smoke_failures <security-smoke.sh output file>: prints how many checks failed.
dast_smoke_failures() {
  grep -c -- '-> FAIL' "${1:-/dev/null}" 2> /dev/null || true
}
