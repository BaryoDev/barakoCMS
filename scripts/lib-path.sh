# Sourced by scripts that need `realpath -m`, which macOS realpath does not have.
# Resolves the deepest existing parent with pwd -P and appends the rest, so the path need not exist.
resolve_path() {
    local p="$1" rest="" d
    case "$p" in /*) ;; *) p="$PWD/$p" ;; esac
    while [ ! -d "$p" ]; do rest="/$(basename "$p")$rest"; p="$(dirname "$p")"; done
    d="$(cd "$p" && pwd -P)" || return 1
    d="${d%/}$rest"
    printf '%s\n' "${d:-/}"
}
