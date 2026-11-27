#!/usr/bin/env bash
#
# Builds the production bundle a release attaches: the files a host needs to run
# docker-compose.prod.yml with Docker and nothing else, so a deploy needs no checkout (#1436).
#
#   scripts/build-prod-bundle.sh <version> <output directory>
#
# Writes two files to the output directory:
#
#   barako-cms-prod-<version>.tar.gz   one top-level directory, barako-cms-prod-<version>/
#   SHA256SUMS                         the archive's sum, in the format `sha256sum -c` reads
#
# The compose file inside defaults BARAKO_TAG to <version>, and so does .env.prod.example, so the
# files and the image they start cannot drift. The comments beside both are rewritten to say so.
# The repository's own compose file keeps requiring the tag, because there a default would turn the
# next pull into an unplanned upgrade.
#
# .env.prod.example also gets COMPOSE_PROJECT_NAME=barako-cms. Compose names the data volumes after
# the project, which defaults to the directory name, and every release unpacks into a directory
# named for its version. Without a fixed name each release would start on an empty database.
# The bundle's compose file also gets a top-level `name:` that requires the variable, so an .env
# copied from an older install that lacks the line stops compose before anything starts, and
# does not quietly start on empty volumes.
#
# The archive is reproducible: sorted members, no owner, one modification time. Building the same
# commit twice gives the same sum.
#
# Every bind mount in docker-compose.prod.yml has to be in the bundle. The list below is checked
# against the compose file here, and scripts/prod-bundle-check.sh starts the stack from an empty
# directory to prove it.
set -euo pipefail

cd "$(dirname "$0")/.."

VERSION="${1:?usage: scripts/build-prod-bundle.sh <version> <output directory>}"
OUT="${2:?usage: scripts/build-prod-bundle.sh <version> <output directory>}"

case "$VERSION" in
  [0-9]*.[0-9]*.[0-9]*) ;;
  *) echo "build-prod-bundle: '$VERSION' is not a version such as 6.0.0 or 6.0.0-beta.1" >&2; exit 1 ;;
esac
case "$VERSION" in
  *[!0-9A-Za-z.-]*) echo "build-prod-bundle: '$VERSION' has a character a version does not" >&2; exit 1 ;;
esac

# The compose file, the proxy config it mounts, the two scripts it mounts from ./scripts (the
# whole directory is mounted, and only these two are read), the second compose file the secrets
# section of docs/deploy-in-production.md tells people to add, and the environment template.
FILES=(
  docker-compose.prod.yml
  docker-compose.secrets.yml
  Caddyfile
  .env.prod.example
  scripts/backup-cron.sh
  scripts/db-app-role.sql
)

for f in "${FILES[@]}"; do
  [ -f "$f" ] || { echo "build-prod-bundle: $f is missing from the repository" >&2; exit 1; }
done

mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
NAME="barako-cms-prod-$VERSION"
ARCHIVE="$OUT/$NAME.tar.gz"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/$NAME/scripts"

for f in "${FILES[@]}"; do
  cp "$f" "$STAGE/$NAME/$f"
done

# Pin the tag in the two places that carry it and rewrite the two comments that say it is required
# or must be pinned. Each replacement must hit exactly once, so a reworded line fails the build
# instead of shipping a bundle that still asks for a tag or says one thing above another.
python3 - "$STAGE/$NAME" "$VERSION" <<'PY'
import pathlib, sys

root, version = pathlib.Path(sys.argv[1]), sys.argv[2]

def replace_once(path, old, new):
    text = path.read_text(encoding="utf-8")
    if text.count(old) != 1:
        sys.exit(f"build-prod-bundle: expected exactly one '{old}' in {path.name}, found {text.count(old)}")
    path.write_text(text.replace(old, new), encoding="utf-8")

compose = root / "docker-compose.prod.yml"
replace_once(compose,
             "${BARAKO_TAG:?pin a version in .env, for example 3.21.0}",
             "${BARAKO_TAG:-" + version + "}")
replace_once(compose,
             "  # BARAKO_TAG is required rather than defaulting to latest, because with a default the\n"
             "  # next `docker compose pull` is an unplanned major upgrade.\n",
             "  # BARAKO_TAG defaults to the release this bundle came from, so the image and these files\n"
             "  # agree. Change it only to deploy another release. It never defaults to latest.\n")

# The name is quoted because the message holds a colon followed by a space, which a plain YAML
# scalar cannot carry. Only the bundle gets it: from a checkout the old project name may be the
# directory's, so there the file keeps compose's default.
replace_once(compose,
             "\nservices:\n",
             "\nname: \"${COMPOSE_PROJECT_NAME:?set COMPOSE_PROJECT_NAME in .env: barako-cms for a bundle install, "
             "or the old project name for a stack started from a checkout}\"\n"
             "\nservices:\n")

env = root / ".env.prod.example"
lines = env.read_text(encoding="utf-8").splitlines(keepends=True)
hits = [i for i, l in enumerate(lines) if l.startswith("BARAKO_TAG=")]
if len(hits) != 1:
    sys.exit(f"build-prod-bundle: expected one BARAKO_TAG= line in .env.prod.example, found {len(hits)}")
lines[hits[0]] = "BARAKO_TAG=" + version + "\n"
env.write_text("".join(lines), encoding="utf-8")

replace_once(env,
             "# Pin it. With a default, the next `docker compose pull` is an unplanned upgrade.\n",
             "# This bundle defaults to the release it came from, so leave it. Change it only to deploy\n"
             "# another release (Upgrading in docs/deploy-in-production.md). The line below is a real setting\n"
             "# and a value in .env wins over the compose default, so an .env copied from an older install\n"
             "# keeps its old tag and runs the old image. On upgrade, set BARAKO_TAG in .env to the new version.\n")
replace_once(env,
             "# --- Version -----------------------------------------------------------------\n",
             "# --- Compose project ---------------------------------------------------------\n"
             "# Compose names the data volumes after this. Without it they take the name of the directory,\n"
             "# and each release unpacks into a directory named for its version, so an upgrade would start\n"
             "# on an empty database. Keep it the same in every release's .env.\n"
             "COMPOSE_PROJECT_NAME=barako-cms\n"
             "\n"
             "# --- Version -----------------------------------------------------------------\n")
PY

# Every ./path the compose file mounts has to be in the bundle. The sed below reads the short form
# the file uses today (an unquoted `- ./x:/y` line); a quoted or long-form mount is not seen here.
# scripts/prod-bundle-check.sh, run by the compose job in ci.yml, resolves every mount form from
# `docker compose config` against the unpacked directory, so a form this check misses fails there.
missing=0
while IFS= read -r src; do
  if [ ! -e "$STAGE/$NAME/$src" ]; then
    echo "build-prod-bundle: docker-compose.prod.yml mounts $src and the bundle does not carry it" >&2
    missing=1
  elif [ -d "$STAGE/$NAME/$src" ] && [ -z "$(ls -A "$STAGE/$NAME/$src")" ]; then
    echo "build-prod-bundle: docker-compose.prod.yml mounts the directory $src and the bundle's copy is empty" >&2
    missing=1
  else
    echo "bind mount $src: in the bundle" >&2
  fi
done < <(sed -n -E 's/^[[:space:]]*-[[:space:]]+(\.\/[^:[:space:]]+):.*/\1/p' docker-compose.prod.yml | sort -u)
[ "$missing" = 0 ] || exit 1

python3 - "$STAGE" "$NAME" "$ARCHIVE" <<'PY'
import gzip, io, os, sys, tarfile

stage, name, archive = sys.argv[1:4]
root = os.path.join(stage, name)

def entry(path):
    rel = os.path.relpath(path, stage)
    info = tarfile.TarInfo(rel)
    info.uid = info.gid = 0
    info.uname = info.gname = ""
    info.mtime = 0
    if os.path.isdir(path):
        info.type = tarfile.DIRTYPE
        info.mode = 0o755
        return info, None
    # 0644 for every file: the compose file mounts them read-only and the containers read them as
    # other users (the role job runs as 70:70), so a 0600 file would not be readable there.
    info.mode = 0o644
    data = open(path, "rb").read()
    info.size = len(data)
    return info, data

paths = [root]
for d, dirs, files in os.walk(root):
    dirs.sort()
    paths += [os.path.join(d, x) for x in dirs] + [os.path.join(d, x) for x in sorted(files)]

buf = io.BytesIO()
with tarfile.open(fileobj=buf, mode="w", format=tarfile.PAX_FORMAT) as tar:
    for p in sorted(paths):
        info, data = entry(p)
        tar.addfile(info, io.BytesIO(data) if data is not None else None)

with open(archive, "wb") as raw:
    with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as gz:
        gz.write(buf.getvalue())
PY

if command -v sha256sum >/dev/null 2>&1; then
  SUM=$(cd "$OUT" && sha256sum "$NAME.tar.gz")
else
  SUM=$(cd "$OUT" && shasum -a 256 "$NAME.tar.gz")
fi
# Two spaces between the hash and the name, which is what `sha256sum -c` and `shasum -a 256 -c` read.
printf '%s\n' "$SUM" | awk '{ printf "%s  %s\n", $1, $2 }' > "$OUT/SHA256SUMS"

echo "wrote $ARCHIVE" >&2
cat "$OUT/SHA256SUMS" >&2
