# Sourced by restore-check.sh, upgrade-check.sh and suite-db-commands-check.sh. Not run on its own.
#
# Those scripts used to publish Postgres and start the API on fixed host ports between 55433 and
# 58096. That is inside Linux's ephemeral range (32768 to 60999), which the kernel hands to outbound
# connections, so an image pull or a package restore could be holding the port at the moment a
# script went to bind it (#1047).
#
# Choosing a free port first and binding it later keeps that race, so nothing here looks for a free
# port. Whoever binds the port chooses it, in the same call, and the script reads the answer back:
#
#   - a container is published with an empty host port, Docker binds one, `docker port` reports it
#   - the API is given port 0, the kernel assigns one at bind, Kestrel logs it
#
# A caller that sets a port by hand still gets that port, and a failure to take it is reported as
# what it is rather than retried on another one.
#
# Functions print to stderr and return 1. The caller decides what failing means.
#
# Tested by: bash scripts/test-check-ports.sh

# publish_spec <host port, or empty for Docker's choice> <container port>
#
# Loopback only. Without an address Docker publishes on every interface, which put a Postgres with
# a known password on the runner's network.
publish_spec() {
    printf '127.0.0.1:%s:%s\n' "${1:-}" "$2"
}

# Reads `docker port <container> <port>/tcp` output on stdin and prints the loopback host port.
#
# That output is one line per binding, and a container published without an address prints two, an
# IPv4 line and an IPv6 one ("[::]:32768"), which Docker is free to give different ports. Taking
# the text after the last colon of the first line would read whichever came first. Only a 127.0.0.1
# line counts here, and two of them that disagree is an error rather than a guess.
parse_docker_port() {
    local line port found=""
    while IFS= read -r line || [ -n "$line" ]; do
        case "$line" in
            127.0.0.1:*) port="${line#127.0.0.1:}" ;;
            *) continue ;;
        esac
        case "$port" in
            ''|*[!0-9]*) echo "ports: no port number in '$line'" >&2; return 1 ;;
        esac
        if [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then
            echo "ports: '$line' is not a usable port" >&2; return 1
        fi
        if [ -n "$found" ] && [ "$found" != "$port" ]; then
            echo "ports: two loopback bindings, $found and $port, and no way to tell which is meant" >&2
            return 1
        fi
        found="$port"
    done
    [ -n "$found" ] || { echo "ports: no binding on 127.0.0.1" >&2; return 1; }
    printf '%s\n' "$found"
}

# published_port <container id> <container port>
#
# Takes the id `docker run` printed, not a name, so the answer is about the container this run
# started. Docker holds the host port from the moment it publishes it until the container stops, so
# nothing else can be listening on what this returns.
published_port() {
    local out
    out=$(docker port "$1" "$2/tcp" 2>&1) || {
        echo "ports: docker port failed for container $1: $out" >&2; return 1
    }
    printf '%s\n' "$out" | parse_docker_port || {
        echo "ports: container $1 port $2/tcp, docker port printed: ${out:-nothing}" >&2; return 1
    }
}

# publish_failure <file holding docker run's stderr> <host port the caller asked for, or empty> <what was being started>
#
# Shows what Docker said and prints the reason to fail with. Only a port the caller fixed can be
# lost to another listener, since Docker binds a port it chooses before it reports one, so that is
# the only case named as such.
publish_failure() {
    cat "$1" >&2
    if [ -n "${2:-}" ] && grep -qiE 'address already in use|port is already allocated' "$1"; then
        echo "port $2 was asked for and is held by a listener this run did not start, so docker could not start $3"
    else
        echo "docker could not start $3"
    fi
}

# listen_port <log file> <pid> [seconds]
#
# Prints the port the process with that pid is listening on, read from its own log. The log is the
# one this run redirected that process into, so the line cannot have come from anything else, and
# Kestrel writes it only after the bind succeeded. While the process lives it holds that port, so
# an answer on it is an answer from this process.
#
# The host must log Microsoft.Hosting.Lifetime at Information for the line to exist. appsettings.json
# sets Microsoft to Warning, so the scripts raise that one source back with LISTEN_LOG_ENV.
#
# A process that exits is reported at once instead of waited out, and "address already in use" in
# its log is named as what it is: a listener this run did not start.
LISTEN_LOG_ENV='Serilog__MinimumLevel__Override__Microsoft.Hosting.Lifetime=Information'

listen_port() {
    local log="$1" pid="$2" seconds="${3:-120}" port
    for _ in $(seq 1 "$seconds"); do
        port=$(sed -n '/Now listening on: http:\/\/127\.0\.0\.1:[0-9]/{s/.*Now listening on: http:\/\/127\.0\.0\.1:\([0-9]*\).*/\1/p;q;}' "$log" 2>/dev/null || true)
        if ! kill -0 "$pid" 2>/dev/null; then
            if grep -qi 'address already in use' "$log" 2>/dev/null; then
                echo "ports: the process this run started (pid $pid) could not bind: the address is held by a listener this run did not start" >&2
            else
                echo "ports: the process this run started (pid $pid) is gone, so nothing it logged says who is listening now" >&2
            fi
            return 1
        fi
        case "$port" in
            ''|0) ;;
            *) printf '%s\n' "$port"; return 0 ;;
        esac
        sleep 1
    done
    echo "ports: pid $pid wrote no 'Now listening on: http://127.0.0.1:PORT' line to $log in ${seconds}s" >&2
    return 1
}
