# shellcheck shell=bash
#
# Starting the production compose file's caddy service in a check, and reaching the app through it
# over HTTPS. Sourced by scripts/prod-bundle-check.sh and scripts/compose-upgrade-check.sh.
#
# Caddy runs with DOMAIN_API=api.localhost. A *.localhost name gets a certificate from Caddy's own
# local CA, so no ACME request leaves the machine. What this does not prove is a real ACME issue
# from Let's Encrypt for a public name. Only the host ports differ from the shipped compose file:
# the override below lets Docker pick free ones, because 80 and 443 may be taken on the machine
# running the check.

CADDY_HOST="api.localhost"

# write_caddy_ports_override <file>
# ports is a list that compose appends to, so the override has to replace it (!override, compose
# 2.24 or later). Only the host side changes: both ports stay bound to loopback, chosen by Docker.
write_caddy_ports_override() {
  cat > "$1" <<'YML'
services:
  caddy:
    ports: !override
      - "127.0.0.1::80"
      - "127.0.0.1::443"
YML
}

# caddy_root_cert <container id> <out file>: copies Caddy's local root certificate, waiting up to a
# minute for Caddy to write it.
caddy_root_cert() {
  local _
  for _ in $(seq 1 30); do
    if docker cp "$1:/data/caddy/pki/authorities/local/root.crt" "$2" >/dev/null 2>&1 && [ -s "$2" ]; then
      return 0
    fi
    sleep 2
  done
  return 1
}

# caddy_tls_curl <root cert> <https port> <curl args>...: verifies the server certificate against
# Caddy's root and never skips verification. --resolve keeps the name api.localhost whatever the
# machine's resolver does, so the name on the certificate is the one curl checks.
caddy_tls_curl() {
  local cacert="$1" port="$2"
  shift 2
  curl -sS --max-time 10 --cacert "$cacert" --resolve "$CADDY_HOST:$port:127.0.0.1" "$@"
}

# image_build_sha <image>: the commit the image was built from, or "unknown" when it carries none.
image_build_sha() {
  local sha
  sha="$(docker image inspect "$1" --format '{{range .Config.Env}}{{println .}}{{end}}' | sed -n 's/^BARAKO_BUILD_SHA=//p')"
  printf '%s' "${sha:-unknown}"
}

# body_is_build_sha <json file> <sha>: the body is exactly {"sha": <sha>}.
body_is_build_sha() {
  python3 -I -c '
import json, sys
body = json.load(open(sys.argv[1]))
sys.exit(0 if body == {"sha": sys.argv[2]} else 1)
' "$1" "$2"
}
