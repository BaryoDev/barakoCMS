#!/usr/bin/env bash
# Tests how restore-check.sh, upgrade-check.sh and suite-db-commands-check.sh get their ports.
#
# Those scripts bound fixed ports inside the kernel's ephemeral range and lost them now and then to
# whatever else on the runner had been handed the same number (#1047). The cases here hold a
# listener on each old default, the way that other process did, and require the scripts to get past
# their port steps anyway.
#
# Three groups:
#
#   1. scripts/lib-ports.sh on its own: reading `docker port` output, reading the port a process
#      logged, and refusing a port whose listener is not the process this run started. A small
#      python server stands in for the API, since the real one takes a build to start.
#   2. Docker choosing a host port and the lib reading it back, on loopback only.
#   3. Each script run for real up to the moment it starts the API, with `dotnet` replaced by a
#      stub that records the address and connection string it was handed and then exits. The
#      script fails there, as it must with no host, and the record is what gets checked.
#
# Groups 2 and 3 need Docker and the postgres:16-alpine image, which the scripts under test pull
# anyway. Nothing else touches the network. Without Docker those cases are reported as SKIPPED and
# the run still fails when PORTS_TEST_REQUIRE_DOCKER=1, which CI sets, so a runner that lost Docker
# cannot turn this into a pass.
#
#   bash scripts/test-check-ports.sh

set -uo pipefail

cd "$(dirname "$0")/.."

pass=0
fail=0
skipped=0

ok() { echo "  ok    $1"; pass=$((pass + 1)); }
bad() { echo "  FAIL  $1"; fail=$((fail + 1)); }
expect() { # $1 = description, then a command that must succeed
    local what="$1"
    shift
    if "$@"; then ok "$what"; else bad "$what"; fi
}

d=$(mktemp -d)
pids=()
containers=()
cleanup() {
    for p in "${pids[@]:-}"; do [ -n "$p" ] && kill "$p" 2>/dev/null; done
    for c in "${containers[@]:-}"; do [ -n "$c" ] && docker rm -f "$c" >/dev/null 2>&1; done
    rm -rf "$d"
}
trap cleanup EXIT

if [ -f scripts/lib-ports.sh ]; then
    . scripts/lib-ports.sh
else
    echo "scripts/lib-ports.sh is missing, so every case that calls it fails below"
fi

# One program for both sides of the story. With "kestrel" it is the process a run starts: it binds,
# says so in Kestrel's words, and answers. Without, it is somebody else's listener that happens to
# answer /health with 200, which is the thing a readiness check must not be fooled by.
cat > "$d/listener.py" <<'PY'
import http.server, sys

port = int(sys.argv[1])
kestrel = len(sys.argv) > 2 and sys.argv[2] == "kestrel"

class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Length", "2")
        self.end_headers()
        self.wfile.write(b"ok")
    def log_message(self, *args):
        pass

try:
    server = http.server.HTTPServer(("127.0.0.1", port), Handler)
except OSError:
    if kestrel:
        print(f"[00:00:00 FTL] Failed to bind to address http://127.0.0.1:{port}: address already in use.", flush=True)
        sys.exit(1)
    # Already held by something else, which serves this test's purpose just as well.
    print("held", flush=True)
    sys.exit(0)
if kestrel:
    print(f"[00:00:00 INF] Now listening on: http://127.0.0.1:{server.server_address[1]}", flush=True)
else:
    print("held", flush=True)
server.serve_forever()
PY

# hold <port>: keeps a listener this test's "run" did not start on that port until the test ends.
hold() {
    python3 "$d/listener.py" "$1" > "$d/hold-$1.log" 2>&1 &
    pids+=("$!")
    for _ in $(seq 1 50); do grep -q held "$d/hold-$1.log" 2>/dev/null && return 0; sleep 0.1; done
    echo "could not hold port $1" >&2
    return 1
}

# A port for the explicit cases, taken from below the ephemeral range so nothing is handed it by
# chance between this check and its use. $1 is where to start looking, so two calls do not agree.
free_port() {
    python3 - "$1" <<'PY'
import socket, sys
for port in range(int(sys.argv[1]), int(sys.argv[1]) + 100):
    s = socket.socket()
    try:
        s.bind(("0.0.0.0", port))
    except OSError:
        continue
    finally:
        s.close()
    print(port)
    break
PY
}

health() { [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$1/health" || true)" = "200" ]; }

echo "reading docker port output:"
out=$(printf '127.0.0.1:32768\n' | parse_docker_port 2>/dev/null)
expect "one loopback line gives its port" [ "$out" = "32768" ]
out=$(printf '0.0.0.0:32768\n[::]:32769\n127.0.0.1:40001\n' | parse_docker_port 2>/dev/null)
expect "the loopback line is chosen over wildcard and IPv6 lines" [ "$out" = "40001" ]
out=$(printf '127.0.0.1:40001' | parse_docker_port 2>/dev/null)
expect "a last line with no newline is still read" [ "$out" = "40001" ]
printf '0.0.0.0:32768\n[::]:32769\n' | parse_docker_port >/dev/null 2>&1
expect "no loopback line is refused, not guessed from the IPv6 one" [ $? -ne 0 ]
printf '127.0.0.1:40001\n127.0.0.1:40002\n' | parse_docker_port >/dev/null 2>&1
expect "two loopback lines that disagree are refused" [ $? -ne 0 ]
printf '127.0.0.1:\n' | parse_docker_port >/dev/null 2>&1
expect "a line with no number is refused" [ $? -ne 0 ]
printf '127.0.0.1:0\n' | parse_docker_port >/dev/null 2>&1
expect "port 0 is refused" [ $? -ne 0 ]
printf '' | parse_docker_port >/dev/null 2>&1
expect "empty output is refused" [ $? -ne 0 ]
expect "no host port asks Docker to choose, on loopback" [ "$(publish_spec "" 5432 2>/dev/null)" = "127.0.0.1::5432" ]
expect "a host port set by the caller is kept, on loopback" [ "$(publish_spec 21000 5432 2>/dev/null)" = "127.0.0.1:21000:5432" ]

echo
echo "reading the port a process bound:"
python3 "$d/listener.py" 0 kestrel > "$d/zero.log" 2>&1 &
zero_pid=$!
pids+=("$zero_pid")
port=$(listen_port "$d/zero.log" "$zero_pid" 10 2>/dev/null)
expect "port 0 is read back as the port the kernel assigned" [ "${port:-0}" -gt 0 ]
expect "and that port answers" health "${port:-0}"

wanted=$(free_port 21000)
python3 "$d/listener.py" "$wanted" kestrel > "$d/explicit.log" 2>&1 &
explicit_pid=$!
pids+=("$explicit_pid")
port=$(listen_port "$d/explicit.log" "$explicit_pid" 10 2>/dev/null)
expect "a port set by the caller is the port that is bound" [ "$port" = "$wanted" ]

echo
echo "refusing a listener this run did not start:"
taken=$(free_port 21100)
hold "$taken"
expect "somebody else's listener answers /health with 200" health "$taken"
python3 "$d/listener.py" "$taken" kestrel > "$d/taken.log" 2>&1 &
taken_pid=$!
wait "$taken_pid" 2>/dev/null
message=$(listen_port "$d/taken.log" "$taken_pid" 10 2>&1 >/dev/null)
expect "a process that lost its port to that listener is refused" [ $? -ne 0 ]
case "$message" in
    *"listener this run did not start"*) ok "and the message says whose listener it is" ;;
    *) bad "and the message says whose listener it is (got: $message)" ;;
esac

# The log names a port and something answers on it, but the process that wrote the line is gone.
echo "[00:00:00 INF] Now listening on: http://127.0.0.1:$taken" > "$d/stale.log"
true &
gone_pid=$!
wait "$gone_pid"
listen_port "$d/stale.log" "$gone_pid" 10 >/dev/null 2>&1
expect "a listening line from a process that has exited is refused" [ $? -ne 0 ]

sleep 30 &
silent_pid=$!
pids+=("$silent_pid")
: > "$d/silent.log"
listen_port "$d/silent.log" "$silent_pid" 2 >/dev/null 2>&1
expect "a process that never says it is listening runs into the deadline" [ $? -ne 0 ]

# What each stubbed script run leaves behind: the lines the dotnet stub wrote, and the script's output.
record="$d/record"
output="$d/output"

recorded() { sed -n "s/^$1=//p" "$record" 2>/dev/null | head -n 1; }

# stubs <postgres container name>: a `dotnet` that builds nothing and starts nothing. `exec` writes
# down what the script handed the host and where Docker published the script's postgres at that
# moment, then exits 1. The scripts start the host with `env -i`, so the paths are written into the
# stub rather than passed in the environment.
stubs() {
    local real_docker
    real_docker=$(command -v docker)
    rm -rf "$d/bin"
    mkdir "$d/bin"
    : > "$record"
    cat > "$d/bin/dotnet" <<STUB
#!/usr/bin/env bash
case "\${1:-}" in
    publish) exit 0 ;;
    exec)
        {
            echo "URLS=\${ASPNETCORE_URLS:-}"
            echo "CONN=\${ConnectionStrings__DefaultConnection:-}"
            echo "PUBLISHED=\$("$real_docker" port "$1" 5432/tcp 2>&1 | tr '\n' ' ')"
        } >> "$record"
        exit 1 ;;
esac
exit 1
STUB
    chmod +x "$d/bin/dotnet"
}

# The scripts name their own containers and remove them by name on exit. One that is already there
# was not started by this test, so the case is failed rather than run over it.
name_is_free() {
    if docker ps -a --format '{{.Names}}' | grep -qx "$1"; then
        bad "a container named $1 already exists and this test did not start it, so the case was not run"
        return 1
    fi
}

docker_lib_cases() {
    echo
    echo "Docker choosing the host port:"
    hold 55434
    id=$(docker run -d --name "ports-test-$$-chosen" -p "$(publish_spec "" 5432)" --entrypoint sleep postgres:16-alpine 120 2>&1) \
        || { bad "docker run with a chosen port (docker said: $id)"; return; }
    containers+=("$id")
    port=$(published_port "$id" 5432 2>/dev/null)
    expect "the chosen port is read back from the container this test started" [ "${port:-0}" -gt 0 ]
    expect "it is not the old default, which is held" [ "${port:-55434}" != "55434" ]
    bindings=$(docker port "$id" 5432/tcp)
    expect "it is published on loopback and nowhere else" [ "$bindings" = "127.0.0.1:$port" ]

    wanted=$(free_port 21200)
    id=$(docker run -d --name "ports-test-$$-explicit" -p "$(publish_spec "$wanted" 5432)" --entrypoint sleep postgres:16-alpine 120 2>&1) \
        || { bad "docker run with a port set by the caller (docker said: $id)"; return; }
    containers+=("$id")
    expect "a port set by the caller is the port that is published" [ "$(published_port "$id" 5432 2>/dev/null)" = "$wanted" ]
}

script_cases() {
    echo
    echo "restore-check.sh, with its old defaults 55434 and 58095 held:"
    hold 55434
    hold 58095
    if name_is_free restore-check-pg; then
        stubs restore-check-pg
        PATH="$d/bin:$PATH" bash scripts/restore-check.sh > "$output" 2>&1
        expect "the script stops where the stub host exits, not before" grep -q "the host this run started is not listening" "$output"
        port=$(recorded PUBLISHED | sed -n 's/^127\.0\.0\.1:\([0-9]*\) $/\1/p')
        expect "postgres is published on loopback only, on a port Docker chose" [ -n "$port" ]
        expect "that port is not the old default" [ "${port:-55434}" != "55434" ]
        case "$(recorded CONN)" in
            *"Host=127.0.0.1;Port=$port;"*) ok "the host is given the port Docker chose" ;;
            *) bad "the host is given the port Docker chose (connection string: $(recorded CONN))" ;;
        esac
        expect "the host is told to bind port 0" [ "$(recorded URLS)" = "http://127.0.0.1:0" ]

        echo
        echo "restore-check.sh, with PG_PORT and APP_PORT set by the caller:"
        pg_wanted=$(free_port 21300)
        app_wanted=$(free_port 21400)
        stubs restore-check-pg
        PATH="$d/bin:$PATH" PG_PORT="$pg_wanted" APP_PORT="$app_wanted" bash scripts/restore-check.sh > "$output" 2>&1
        expect "postgres is published on the port asked for, loopback only" [ "$(recorded PUBLISHED)" = "127.0.0.1:$pg_wanted " ]
        case "$(recorded CONN)" in
            *"Host=127.0.0.1;Port=$pg_wanted;"*) ok "the host is given the port asked for" ;;
            *) bad "the host is given the port asked for (connection string: $(recorded CONN))" ;;
        esac
        expect "the host is told to bind the port asked for" [ "$(recorded URLS)" = "http://127.0.0.1:$app_wanted" ]
    fi

    echo
    echo "suite-db-commands-check.sh, with its old defaults 55436 and 58096 held:"
    hold 55436
    hold 58096
    if name_is_free suite-db-commands-pg; then
        stubs suite-db-commands-pg
        PATH="$d/bin:$PATH" bash scripts/suite-db-commands-check.sh > "$output" 2>&1
        port=$(recorded PUBLISHED | sed -n 's/^127\.0\.0\.1:\([0-9]*\) $/\1/p')
        expect "postgres is published on loopback only, on a port Docker chose" [ -n "$port" ]
        expect "that port is not the old default" [ "${port:-55436}" != "55436" ]
        case "$(recorded CONN)" in
            *"Host=127.0.0.1;Port=$port;"*) ok "the host is given the port Docker chose" ;;
            *) bad "the host is given the port Docker chose (connection string: $(recorded CONN))" ;;
        esac
        expect "the host is told to bind port 0" [ "$(recorded URLS)" = "http://127.0.0.1:0" ]

        echo
        echo "suite-db-commands-check.sh, with PG_PORT set to a port somebody else holds:"
        stubs suite-db-commands-pg
        PATH="$d/bin:$PATH" PG_PORT="$taken" bash scripts/suite-db-commands-check.sh > "$output" 2>&1
        expect "the run fails" [ $? -ne 0 ]
        expect "the host was never started against it" [ ! -s "$record" ]
        expect "the message names the port and says whose listener it is" \
            grep -q "port $taken was asked for and is held by a listener this run did not start" "$output"
    fi

    echo
    echo "upgrade-check.sh, with its old defaults 55433, 58090 and 58091 held:"
    hold 55433
    hold 58090
    hold 58091
    # This script takes its container names from the environment, so the test uses its own. The
    # FROM_VERSION image is large and built for amd64 only, so `docker run` of a stand-in image name
    # is recorded and refused here; everything else goes to the real docker.
    real_docker=$(command -v docker)
    stubs "ports-test-$$-pg"
    cat > "$d/bin/docker" <<STUB
#!/usr/bin/env bash
if [ "\${1:-}" = run ]; then
    printf '%s\n' "\$*" >> "$d/docker-runs"
    for last in "\$@"; do :; done
    if [ "\$last" = "ports-test-stub:none" ]; then echo "stub: not starting the old image" >&2; exit 1; fi
fi
exec "$real_docker" "\$@"
STUB
    chmod +x "$d/bin/docker"
    : > "$d/docker-runs"
    PATH="$d/bin:$PATH" PG="ports-test-$$-pg" OLD="ports-test-$$-old" NETWORK="ports-test-$$-net" \
        IMAGE="ports-test-stub:none" bash scripts/upgrade-check.sh > "$output" 2>&1
    expect "the script stops where the stand-in image is refused, not before" \
        grep -q "docker could not start ports-test-stub:none" "$output"
    expect "postgres is published on loopback with Docker choosing the port" \
        grep -q -- "-p 127.0.0.1::5432 postgres:16-alpine" "$d/docker-runs"
    expect "the FROM_VERSION container is published the same way" \
        grep -q -- "-p 127.0.0.1::8080 ports-test-stub:none" "$d/docker-runs"
    rm -f "$d/bin/docker"
}

if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    docker_lib_cases
    script_cases
else
    echo
    echo "SKIPPED: Docker is not available, so the cases that publish a container and the cases that"
    echo "SKIPPED: run restore-check.sh, suite-db-commands-check.sh and upgrade-check.sh did not run."
    skipped=1
fi

echo
if [ "$skipped" -ne 0 ]; then
    echo "$pass passed, $fail failed, and the Docker cases were SKIPPED"
    if [ "${PORTS_TEST_REQUIRE_DOCKER:-}" = "1" ]; then
        echo "PORTS_TEST_REQUIRE_DOCKER=1, so a skip is a failure"
        exit 1
    fi
else
    echo "$pass passed, $fail failed"
fi
[ "$fail" -eq 0 ]
