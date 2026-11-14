#!/usr/bin/env bash
# Downloads the CC0 quality corpus and verifies every byte against corpus.tsv.
#
#   bash tests/Fixtures/download-corpus.sh             # download missing files, then verify
#   bash tests/Fixtures/download-corpus.sh --verify    # verify only (offline-friendly)
#
# The cache is git-ignored: images are NEVER committed. If the network is unavailable, the
# deterministic synthetic fixtures in SyntheticCorpus.cs are the offline substitute.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
manifest="$here/corpus.tsv"
cache="$here/.cache"
verify_only=0
[ "${1:-}" = "--verify" ] && verify_only=1
mkdir -p "$cache"
fail=0
count=0

while IFS=$'\t' read -r id url sha license width height mime tags; do
    case "$id" in ''|'#'*) continue ;; esac
    dest="$cache/$id.jpg"
    count=$((count + 1))

    if [ ! -s "$dest" ] && [ "$verify_only" = "0" ]; then
        echo "fetching $id"
        if ! curl -sS -L -m 120 -A 'SimdImaging-corpus/1.0 (test fixtures)' -o "$dest" "$url"; then
            echo "FAIL  $id: download failed" >&2
            fail=1
            continue
        fi
    fi

    if [ ! -s "$dest" ]; then
        echo "FAIL  $id: missing from cache (run without --verify, or use SyntheticCorpus)" >&2
        fail=1
        continue
    fi

    if printf '%s  %s\n' "$sha" "$dest" | sha256sum -c - --status 2>/dev/null; then
        echo "PASS  $id  sha256=$sha  $(stat -c%s "$dest") bytes"
    else
        echo "FAIL  $id: SHA-256 mismatch" >&2
        fail=1
    fi
# Strip a UTF-8 BOM on the first line: a comment header with a BOM is otherwise read as a data row.
done < <(sed '1s/^\xEF\xBB\xBF//' "$manifest" | grep -v -e '^#' -e '^[[:space:]]*$')

echo "observed: corpus-entries=$count sha256-verified=$((count - fail))"
if [ "$count" = "0" ]; then
    echo "FAIL  corpus.tsv has no entries" >&2
    fail=1
fi

if [ "$fail" = "0" ]; then
    echo "CORPUS: all verified"
else
    echo "CORPUS: FAILED"
fi
exit "$fail"
