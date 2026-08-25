#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 <version>" >&2
}

if (($# != 1)) || [[ -z "$1" ]]; then
    usage
    exit 2
fi

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
exec python3 "$source_root/eng/release/version_contract.py" \
    --version "$1" \
    --version-props "$source_root/src/Directory.Build.props" \
    --changelog "$source_root/CHANGELOG.md"
