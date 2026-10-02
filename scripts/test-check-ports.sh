#!/usr/bin/env bash
# Tests how restore-check.sh, upgrade-check.sh and suite-db-commands-check.sh get their ports.
#
# Those scripts bound fixed ports inside the kernel's ephemeral range and lost them now and then to
# whatever else on the runner had been handed the same number (#1047). The cases here hold a
# listener on each old default, the way that other process did, and require the scripts to get past
# their port steps anyway.
#
# Four groups:
#
#   1. scripts/lib-ports.sh on its own: reading `docker port` output, reading the port a process
#      logged, and refusing a port whose listener is not the process this run started. A small
#      python server stands in for the API, since the real one takes a build to start.
#   2. upgrade-check.sh run up to the first db-assert that has to pass, with both `dotnet` and
#      `docker` replaced. The released image it starts is large and amd64 only, so a stand-in
#      `docker` hands back made-up ids and ports and starts the python server in the image's place.
#      What is checked is what the script asked Docker for and what it handed the host.
#   3. Docker choosing a host port and the lib reading it back, on loopback only.
#   4. restore-check.sh and suite-db-commands-check.sh run against the real Docker up to the moment
#      they start the API, with `dotnet` replaced by a stub that records the address, connection
#      string and log setting it was handed and then exits. The script fails there, as it must with
#      no host, and the record is what gets checked.
#
# Groups 3 and 4 need Docker and the postgres:16-alpine image, which the scripts under test pull
# anyway. Nothing else touches the network. Without Docker those cases are reported as SKIPPED and
# the run still fails when PORTS_TEST_REQUIRE_DOCKER=1, which CI sets, so a runner that lost Docker
# cannot turn this into a pass.
#
# Not covered: the second `old_is_running` call in upgrade-check.sh, after the rollback. Reaching it
# needs a stand-in for the whole upgrade. The data check after the rollback sits in the same place,
# so it is tested on its own, with the other two of lib-upgrade-data.sh, and the two before the
# hosts are tested through the script as well.
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

d=$(mktemp -d) || { echo "could not make a scratch directory"; exit 1; }
pids=()
containers=()
cleanup() {
    for p in "${pids[@]:-}"; do [ -n "$p" ] && kill "$p" 2>/dev/null; done
    for c in "${containers[@]:-}"; do [ -n "$c" ] && docker rm -f "$c" >/dev/null 2>&1; done
    rm -rf "${d:?}"
}
trap cleanup EXIT

if [ -f scripts/lib-ports.sh ]; then
    . scripts/lib-ports.sh
else
    echo "scripts/lib-ports.sh is missing, so every case that calls it fails below"
fi

# One program for every part in the story.
#
#   <port> kestrel                  the process a run starts: it binds, says so in Kestrel's words,
#                                   and answers, including the sign-in and write calls upgrade-check
#                                   makes against the released image
#   <port>                          somebody else's listener that happens to answer /health with
#                                   200, which is the thing a readiness check must not be fooled by
#   <port> after <pidfile> <marker> somebody else's listener that takes over a port after a host
#                                   dies. Asked for anything, it touches the marker, then holds its
#                                   answer until the pid in the file is gone, and only then says
#                                   200. So the order is fixed, not likely: whoever asks cannot
#                                   hear back while the host is alive, however long anything stalls.
cat > "$d/listener.py" <<'PY'
import http.server, os, sys, time

port = int(sys.argv[1])
mode = sys.argv[2] if len(sys.argv) > 2 else "foreign"

def host_alive():
    try:
        os.kill(int(open(sys.argv[3]).read()), 0)
        return True
    except FileNotFoundError:
        return True
    except (OSError, ValueError):
        return False

class Handler(http.server.BaseHTTPRequestHandler):
    def answer(self, code, body):
        self.send_response(code)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)
    def do_GET(self):
        if mode == "after":
            open(sys.argv[4], "w").close()
            deadline = time.time() + 120
            while host_alive() and time.time() < deadline:
                time.sleep(0.05)
            self.answer(503 if host_alive() else 200, b"ok")
        else:
            self.answer(200, b"ok")
    def do_POST(self):
        self.rfile.read(int(self.headers.get("Content-Length") or 0))
        self.answer(200, b'{"token":"stand-in","id":"stand-in-stream"}')
    do_PUT = do_POST
    def log_message(self, *args):
        pass

try:
    server = http.server.HTTPServer(("127.0.0.1", port), Handler)
except OSError:
    if mode == "kestrel":
        print(f"[00:00:00 FTL] Failed to bind to address http://127.0.0.1:{port}: address already in use.", flush=True)
        sys.exit(1)
    # Already held by something else, which serves this test's purpose just as well.
    print("held", flush=True)
    sys.exit(0)
if mode == "kestrel":
    print(f"[00:00:00 INF] Now listening on: http://127.0.0.1:{server.server_address[1]}", flush=True)
else:
    print("held", flush=True)
server.serve_forever()
PY

# hold <port> [after <pidfile> <marker>]: keeps a listener this test's "run" did not start on that
# port until the test ends.
hold() {
    python3 "$d/listener.py" "$@" > "$d/hold-$1.log" 2>&1 &
    pids+=("$!")
    for _ in $(seq 1 300); do grep -q held "$d/hold-$1.log" 2>/dev/null && return 0; sleep 0.1; done
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
out=$(printf '127.0.0.1:99999999999999999999\n' | parse_docker_port 2>"$d/big.err")
rc=$?
expect "a number too long for the shell to compare is refused" [ "$rc" -ne 0 ]
expect "and nothing is printed for it" [ -z "$out" ]
if grep -q "integer expression expected" "$d/big.err"; then
    bad "and the shell is never asked to compare it"
else
    ok "and the shell is never asked to compare it"
fi
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

sleep 120 &
silent_pid=$!
pids+=("$silent_pid")
: > "$d/silent.log"
listen_port "$d/silent.log" "$silent_pid" 2 >/dev/null 2>&1
expect "a process that never says it is listening runs into the deadline" [ $? -ne 0 ]

# The line as it looks while the process is still writing it: the number has started, the newline
# has not arrived. Port 41234 read at that moment is port 4.
printf '[00:00:00 INF] Now listening on: http://127.0.0.1:4' > "$d/partial.log"
out=$(listen_port "$d/partial.log" "$silent_pid" 2 2>/dev/null)
rc=$?
expect "a listening line with no newline yet is not read" [ "$rc" -ne 0 ]
expect "and no part of its number is returned" [ -z "$out" ]
printf '1234\n' >> "$d/partial.log"
expect "the same line once finished gives the whole number" [ "$(listen_port "$d/partial.log" "$silent_pid" 2 2>/dev/null)" = "41234" ]

# What each stubbed script run leaves behind: the lines the dotnet stub wrote, and the script's output.
record="$d/record"
output="$d/output"
bin="$d/bin"
state="$d/fake-docker"
mkdir "$bin" "$state"

recorded() { sed -n "s/^$1=//p" "$record" 2>/dev/null | head -n 1; }

# stubs <postgres container name> [dies <port> <pidfile> <marker>]
#
# A `dotnet` that builds nothing and starts nothing. `exec` writes down what the script handed the
# host and where Docker published the script's postgres at that moment, then exits 1. The scripts
# start the host with `env -i`, so the paths are written into the stub rather than passed in the
# environment. The log setting has a dot in its name, which the shell cannot read as a variable, so
# it is taken from `env`.
#
# With "dies" the stub is a host that says it is listening on <port> and lives until the listener
# holding that port has been asked, which the script does only after it has read the port from a
# live host. Then it exits, and that listener, which was waiting for exactly that, answers. Nothing
# here is timed: the script's first answer on the port can only come from a listener it did not
# start, after its host is gone.
stubs() {
    local real_docker ending="exit 1"
    real_docker=$(command -v docker)
    if [ "${2:-}" = dies ]; then
        ending="echo \$\$ > \"$4\"
        echo \"[00:00:00 INF] Now listening on: http://127.0.0.1:$3\"
        for _ in \$(seq 1 1200); do [ -e \"$5\" ] && break; sleep 0.1; done
        exit 1"
    fi
    : > "$record"
    cat > "$bin/dotnet" <<STUB
#!/usr/bin/env bash
case "\${1:-}" in
    publish) exit 0 ;;
    exec)
        {
            echo "URLS=\${ASPNETCORE_URLS:-}"
            echo "CONN=\${ConnectionStrings__DefaultConnection:-}"
            echo "LISTEN=\$(env | sed -n 's/^Serilog__MinimumLevel__Override__Microsoft\\.Hosting\\.Lifetime=//p')"
            echo "PUBLISHED=\$("$real_docker" port "$1" 5432/tcp 2>&1 | tr '\n' ' ')"
        } >> "$record"
        $ending ;;
esac
exit 1
STUB
    chmod +x "$bin/dotnet"
}

# A `docker` for upgrade-check.sh that starts no container. It writes every call down, hands back
# made-up ids, and reports as published either the port the script asked for or, when the script
# left the choice to Docker, one of its own: 40123 for postgres, and for the released image whatever
# port the kernel gave the python server started in its place. A query answers 5, which is enough
# events and enough progression for the script to carry on to the hosts, except the two the script
# asks about the seeded HR role (lib-upgrade-data.sh). Those answer what a database upgraded from a
# release that seeded the role would: one such role, and one holding view_sensitive once the
# forward file has run.
#
# $1 is what `docker inspect` says about the released image's container: true while it runs.
# $2 is how many roles are the seeded HR role, and $3 how many of them hold view_sensitive after
# the forward file. Both default to 1.
fake_docker() {
    : > "$state/calls"
    : > "$state/old.pid"
    echo "$1" > "$state/running"
    echo "${2:-1}" > "$state/seeded-hr"
    echo "${3:-1}" > "$state/seeded-hr-granted"
    cat > "$bin/docker" <<STUB
#!/usr/bin/env bash
printf '%s\n' "\$*" >> "$state/calls"
case "\${1:-}" in
    run)
        cidfile=""; spec=""; prev=""
        for arg in "\$@"; do
            [ "\$prev" = "--cidfile" ] && cidfile="\$arg"
            [ "\$prev" = "-p" ] && spec="\$arg"
            prev="\$arg"
        done
        host="\${spec#127.0.0.1:}"
        host="\${host%%:*}"
        case "\$spec" in
            *:5432)
                id=fake-pg
                echo "\${host:-40123}" > "$state/fake-pg.port" ;;
            *)
                id=fake-old
                python3 "$d/listener.py" "\${host:-0}" kestrel > "$state/old.log" 2>&1 < /dev/null &
                echo \$! > "$state/old.pid"
                for _ in \$(seq 1 300); do grep -q "Now listening" "$state/old.log" && break; sleep 0.1; done
                sed -n 's/.*127\\.0\\.0\\.1:\\([0-9]*\\)\$/\\1/p' "$state/old.log" > "$state/fake-old.port" ;;
        esac
        [ -n "\$cidfile" ] && printf '%s' "\$id" > "\$cidfile"
        echo "\$id" ;;
    port) echo "127.0.0.1:\$(cat "$state/\$2.port")" ;;
    inspect) cat "$state/running" ;;
    exec)
        case " \$* " in
            *" -tAc "*mt_doc_roles*view_sensitive*) cat "$state/seeded-hr-granted" ;;
            *" -tAc "*mt_doc_roles*) cat "$state/seeded-hr" ;;
            *" -tAc "*) echo 5 ;;
        esac ;;
esac
exit 0
STUB
    chmod +x "$bin/docker"
}

# hr_check <function> <seeded count> <granted count>: one check of lib-upgrade-data.sh, given a
# query function that answers the two counts and refuses anything else. Prints what the check said.
hr_check() {
    (
        check="$1" seeded="$2" granted="$3"
        FROM_VERSION=9.9.9
        fail() { printf 'FAILED: %s\n' "$1"; exit 1; }
        psql_q() {
            case "$1" in
                *"00000000-0000-0000-0000-000000000003"*"'Name' = 'HR'"*view_sensitive*) echo "$granted" ;;
                *"00000000-0000-0000-0000-000000000003"*"'Name' = 'HR'"*) echo "$seeded" ;;
                *) echo "FAILED: a query the stub does not know: $1"; exit 2 ;;
            esac
        }
        . scripts/lib-upgrade-data.sh
        "$check"
    )
}

not() { ! "$@"; }

upgrade_data_cases() {
    echo
    echo "lib-upgrade-data.sh, the checks around migrations/4.6.0:"
    expect "a database holding the seeded HR role passes the check before the forward file" \
        hr_check require_seeded_hr 1 0
    hr_check require_seeded_hr 0 0 > "$output" 2>&1
    expect "a database with no such role fails it" [ $? -eq 1 ]
    expect "and the message says the file would prove nothing" \
        grep -q "no role named HR under the seeded id" "$output"

    expect "the role holding view_sensitive after the forward file passes" \
        hr_check require_seeded_hr_granted 1 1
    hr_check require_seeded_hr_granted 1 0 > "$output" 2>&1
    expect "the role not holding it after the forward file fails" [ $? -eq 1 ]
    expect "and the message names the forward file" \
        grep -q "sensitivity-by-capability.sql did not give the HR role view_sensitive" "$output"

    expect "the role not holding view_sensitive after the rollback passes" \
        hr_check require_seeded_hr_not_granted 1 0
    hr_check require_seeded_hr_not_granted 1 1 > "$output" 2>&1
    expect "the role still holding it after the rollback fails" [ $? -eq 1 ]
    expect "and the message names the rollback file" \
        grep -q "rollback-sensitivity-by-capability.sql left view_sensitive on the seeded HR role" "$output"
}

# run_upgrade [NAME=value ...]: the assignments are the script's environment.
run_upgrade() {
    local rc
    env PATH="$bin:$PATH" IMAGE="ports-test-stub:none" "$@" bash scripts/upgrade-check.sh > "$output" 2>&1
    rc=$?
    [ -s "$state/old.pid" ] && pids+=("$(cat "$state/old.pid")")
    return "$rc"
}

upgrade_cases() {
    echo
    echo "upgrade-check.sh, with its old defaults 55433, 58090 and 58091 held:"
    hold 55433
    hold 58090
    hold 58091
    stubs fake-pg
    fake_docker true
    run_upgrade
    expect "the script stops at the first db-assert that has to pass, not before" \
        grep -q "did not bring core's schema up to date" "$output"
    expect "postgres is published on loopback with Docker choosing the port" \
        grep -q -- "-p 127.0.0.1::5432 postgres:16-alpine" "$state/calls"
    expect "the FROM_VERSION container is published the same way" \
        grep -q -- "-p 127.0.0.1::8080 ports-test-stub:none" "$state/calls"
    case "$(recorded CONN)" in
        *"Host=127.0.0.1;Port=40123;"*) ok "the host is given the port docker port reported" ;;
        *) bad "the host is given the port docker port reported (connection string: $(recorded CONN))" ;;
    esac
    expect "the host is told to bind port 0" [ "$(recorded URLS)" = "http://127.0.0.1:0" ]
    expect "the host is told to log the line the port is read from" [ "$(recorded LISTEN)" = "Information" ]
    expect "both containers are removed by the ids this run was given" \
        [ "$(grep -cE '^rm -f fake-(pg|old)$' "$state/calls")" = 2 ]
    expect "and nothing is removed by name" [ "$(grep -c '^rm ' "$state/calls")" = 2 ]

    echo
    echo "upgrade-check.sh, with PG_PORT, NEW_PORT and OLD_PORT set by the caller:"
    pg_wanted=$(free_port 21500)
    new_wanted=$(free_port 21600)
    old_wanted=$(free_port 21700)
    stubs fake-pg
    fake_docker true
    run_upgrade PG_PORT="$pg_wanted" NEW_PORT="$new_wanted" OLD_PORT="$old_wanted"
    expect "the script gets as far as the hosts" grep -q "did not bring core's schema up to date" "$output"
    expect "postgres is published on the port asked for" \
        grep -q -- "-p 127.0.0.1:$pg_wanted:5432 postgres:16-alpine" "$state/calls"
    expect "the FROM_VERSION container is published on the port asked for" \
        grep -q -- "-p 127.0.0.1:$old_wanted:8080 ports-test-stub:none" "$state/calls"
    case "$(recorded CONN)" in
        *"Host=127.0.0.1;Port=$pg_wanted;"*) ok "the host is given the postgres port asked for" ;;
        *) bad "the host is given the postgres port asked for (connection string: $(recorded CONN))" ;;
    esac
    expect "the host is told to bind the port asked for" [ "$(recorded URLS)" = "http://127.0.0.1:$new_wanted" ]

    echo
    echo "upgrade-check.sh, when the database has no seeded HR role:"
    stubs fake-pg
    fake_docker true 0
    run_upgrade
    expect "the run fails" [ $? -ne 0 ]
    expect "the script stops before the forward file, saying it would prove nothing" \
        grep -q "no role named HR under the seeded id" "$output"
    expect "the forward file was not applied" \
        [ "$(grep -c "cp migrations/4.6.0/sensitivity-by-capability.sql" "$state/calls")" = 0 ]
    expect "it does not go on to the db-assert that has to pass" not grep -q "core db-assert must now pass" "$output"

    echo
    echo "upgrade-check.sh, when the forward file leaves the seeded HR role without view_sensitive:"
    stubs fake-pg
    fake_docker true 1 0
    run_upgrade
    expect "the run fails" [ $? -ne 0 ]
    expect "the forward file was applied" \
        [ "$(grep -c "cp migrations/4.6.0/sensitivity-by-capability.sql" "$state/calls")" = 1 ]
    expect "the script stops there, naming the file and what its holders would lose" \
        grep -q "sensitivity-by-capability.sql did not give the HR role view_sensitive" "$output"
    expect "it does not go on to the hosts, whose seeder would grant it in the file's place" \
        not grep -q "core db-assert must now pass" "$output"

    echo
    echo "upgrade-check.sh, when the FROM_VERSION container has stopped and something still answers:"
    stubs fake-pg
    fake_docker false
    run_upgrade
    expect "the run fails" [ $? -ne 0 ]
    expect "the message says the container this run started is not the one answering" \
        grep -q "container this run started is not running" "$output"
    expect "no host was started against it" [ ! -s "$record" ]
    rm -f "$bin/docker"
}

# The scripts name their own containers. One that is already there was not started by this test, so
# the case is failed rather than run beside it.
name_is_free() {
    if docker ps -a --format '{{.Names}}' | grep -qx "$1"; then
        bad "a container named $1 exists and this test did not start it"
        return 1
    fi
}

# name_conflict <script> <container name>: another run's container already has the name. The script
# must fail, and must not remove what it did not start on its way out.
name_conflict() {
    local other
    other=$(docker run -d --name "$2" --entrypoint sleep postgres:16-alpine 120 2>"$d/run.err") \
        || { bad "starting a stand-in for another run's $2 (docker said: $(cat "$d/run.err"))"; return; }
    containers+=("$other")
    stubs "$2"
    PATH="$bin:$PATH" bash "scripts/$1" > "$output" 2>&1
    expect "the run fails" [ $? -ne 0 ]
    expect "the other run's container is still there afterwards" \
        [ "$(docker inspect --format '{{.State.Running}}' "$other" 2>/dev/null)" = "true" ]
    docker rm -f "$other" >/dev/null 2>&1
}

docker_lib_cases() {
    echo
    echo "Docker choosing the host port:"
    hold 55434
    # stderr goes to a file, not into $id: when the image has to be pulled, Docker writes the pull's
    # progress there, and the id would come back with that text in front of it.
    id=$(docker run -d --name "ports-test-$$-chosen" -p "$(publish_spec "" 5432)" --entrypoint sleep postgres:16-alpine 120 2>"$d/run.err") \
        || { bad "docker run with a chosen port (docker said: $(cat "$d/run.err"))"; return; }
    containers+=("$id")
    port=$(published_port "$id" 5432 2>/dev/null)
    expect "the chosen port is read back from the container this test started" [ "${port:-0}" -gt 0 ]
    expect "it is not the old default, which is held" [ "${port:-55434}" != "55434" ]
    bindings=$(docker port "$id" 5432/tcp)
    expect "it is published on loopback and nowhere else" [ "$bindings" = "127.0.0.1:$port" ]

    wanted=$(free_port 21200)
    id=$(docker run -d --name "ports-test-$$-explicit" -p "$(publish_spec "$wanted" 5432)" --entrypoint sleep postgres:16-alpine 120 2>"$d/run.err") \
        || { bad "docker run with a port set by the caller (docker said: $(cat "$d/run.err"))"; return; }
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
        PATH="$bin:$PATH" bash scripts/restore-check.sh > "$output" 2>&1
        expect "the script stops where the stub host exits, not before" grep -q "the host this run started is not listening" "$output"
        port=$(recorded PUBLISHED | sed -n 's/^127\.0\.0\.1:\([0-9]*\) $/\1/p')
        expect "postgres is published on loopback only, on a port Docker chose" [ -n "$port" ]
        expect "that port is not the old default" [ "${port:-55434}" != "55434" ]
        case "$(recorded CONN)" in
            *"Host=127.0.0.1;Port=$port;"*) ok "the host is given the port Docker chose" ;;
            *) bad "the host is given the port Docker chose (connection string: $(recorded CONN))" ;;
        esac
        expect "the host is told to bind port 0" [ "$(recorded URLS)" = "http://127.0.0.1:0" ]
        expect "the host is told to log the line the port is read from" [ "$(recorded LISTEN)" = "Information" ]
        name_is_free restore-check-pg && ok "the script's own postgres is gone afterwards"

        echo
        echo "restore-check.sh, with PG_PORT and APP_PORT set by the caller:"
        pg_wanted=$(free_port 21300)
        app_wanted=$(free_port 21400)
        stubs restore-check-pg
        PATH="$bin:$PATH" PG_PORT="$pg_wanted" APP_PORT="$app_wanted" bash scripts/restore-check.sh > "$output" 2>&1
        expect "postgres is published on the port asked for, loopback only" [ "$(recorded PUBLISHED)" = "127.0.0.1:$pg_wanted " ]
        case "$(recorded CONN)" in
            *"Host=127.0.0.1;Port=$pg_wanted;"*) ok "the host is given the port asked for" ;;
            *) bad "the host is given the port asked for (connection string: $(recorded CONN))" ;;
        esac
        expect "the host is told to bind the port asked for" [ "$(recorded URLS)" = "http://127.0.0.1:$app_wanted" ]

        echo
        echo "restore-check.sh, when the host dies after it listened and something else takes the port:"
        after=$(free_port 21800)
        hold "$after" after "$d/host.pid" "$d/asked"
        stubs restore-check-pg dies "$after" "$d/host.pid" "$d/asked"
        PATH="$bin:$PATH" bash scripts/restore-check.sh > "$output" 2>&1
        expect "the run fails" [ $? -ne 0 ]
        expect "the message says the answer did not come from the host this run started" \
            grep -q "something answered /health on port $after but the host this run started is gone" "$output"

        echo
        echo "restore-check.sh, when another run's container already has its name:"
        name_conflict restore-check.sh restore-check-pg
    fi

    echo
    echo "suite-db-commands-check.sh, with its old defaults 55436 and 58096 held:"
    hold 55436
    hold 58096
    if name_is_free suite-db-commands-pg; then
        stubs suite-db-commands-pg
        PATH="$bin:$PATH" bash scripts/suite-db-commands-check.sh > "$output" 2>&1
        port=$(recorded PUBLISHED | sed -n 's/^127\.0\.0\.1:\([0-9]*\) $/\1/p')
        expect "postgres is published on loopback only, on a port Docker chose" [ -n "$port" ]
        expect "that port is not the old default" [ "${port:-55436}" != "55436" ]
        case "$(recorded CONN)" in
            *"Host=127.0.0.1;Port=$port;"*) ok "the host is given the port Docker chose" ;;
            *) bad "the host is given the port Docker chose (connection string: $(recorded CONN))" ;;
        esac
        expect "the host is told to bind port 0" [ "$(recorded URLS)" = "http://127.0.0.1:0" ]
        expect "the host is told to log the line the did-not-serve check looks for" [ "$(recorded LISTEN)" = "Information" ]
        name_is_free suite-db-commands-pg && ok "the script's own postgres is gone afterwards"

        echo
        echo "suite-db-commands-check.sh, with PG_PORT and APP_PORT set by the caller:"
        pg_wanted=$(free_port 21300)
        app_wanted=$(free_port 21400)
        stubs suite-db-commands-pg
        PATH="$bin:$PATH" PG_PORT="$pg_wanted" APP_PORT="$app_wanted" bash scripts/suite-db-commands-check.sh > "$output" 2>&1
        expect "postgres is published on the port asked for, loopback only" [ "$(recorded PUBLISHED)" = "127.0.0.1:$pg_wanted " ]
        case "$(recorded CONN)" in
            *"Host=127.0.0.1;Port=$pg_wanted;"*) ok "the host is given the port asked for" ;;
            *) bad "the host is given the port asked for (connection string: $(recorded CONN))" ;;
        esac
        expect "the host is told to bind the port asked for" [ "$(recorded URLS)" = "http://127.0.0.1:$app_wanted" ]

        echo
        echo "suite-db-commands-check.sh, with PG_PORT set to a port somebody else holds:"
        stubs suite-db-commands-pg
        PATH="$bin:$PATH" PG_PORT="$taken" bash scripts/suite-db-commands-check.sh > "$output" 2>&1
        expect "the run fails" [ $? -ne 0 ]
        expect "the host was never started against it" [ ! -s "$record" ]
        expect "the message names the port and says whose listener it is" \
            grep -q "port $taken was asked for and is held by a listener this run did not start" "$output"
        name_is_free suite-db-commands-pg && ok "the container Docker created before the start failed is removed"

        echo
        echo "suite-db-commands-check.sh, when another run's container already has its name:"
        name_conflict suite-db-commands-check.sh suite-db-commands-pg
    fi
}

upgrade_data_cases
upgrade_cases

if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    docker_lib_cases
    script_cases
else
    echo
    echo "SKIPPED: Docker is not available, so the cases that publish a container and the cases that"
    echo "SKIPPED: run restore-check.sh and suite-db-commands-check.sh did not run."
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
