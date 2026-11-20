#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 || ! "$1" =~ ^[a-z][a-z0-9]*$ ]]; then
    echo "Usage: select-database-image.sh <catalog-target>" >&2
    exit 64
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
manifest="$script_dir/../docker/database-images.Dockerfile"
requested_target="$1"
selected_image=""
targets=()

# WHY: Read declarations as data, never source/eval a Dockerfile. Validate the
# complete catalog before printing a match so a valid prefix cannot hide drift.
while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    if [[ "$line" =~ ^[[:space:]]*(#.*)?$ ]]; then
        continue
    fi

    read -r -a fields <<< "$line"
    if [[ ${#fields[@]} -ne 4 || "${fields[0]}" != FROM || "${fields[2]}" != AS ]]; then
        echo "Database image catalog requires data-only FROM image AS target declarations." >&2
        exit 1
    fi

    image="${fields[1]}"
    target="${fields[3]}"
    if [[ ! "$target" =~ ^[a-z][a-z0-9]*$ ||
          ! "$image" =~ ^[a-z0-9][a-z0-9./_-]*:[A-Za-z0-9][A-Za-z0-9._-]*@sha256:[0-9a-f]{64}$ ]]; then
        echo "Database image catalog requires named targets and exact tagged SHA-256 references." >&2
        exit 1
    fi

    # WHY: Bash 3.2 treats an empty array as unset under nounset; only expand
    # entries after the array has an element, without disabling strict mode.
    for existing in ${targets[@]+"${targets[@]}"}; do
        if [[ "$existing" == "$target" ]]; then
            echo "Database image catalog contains a duplicate target." >&2
            exit 1
        fi
    done

    targets+=("$target")
    if [[ "$target" == "$requested_target" ]]; then
        selected_image="$image"
    fi
done < "$manifest"

if [[ -z "$selected_image" ]]; then
    echo "The requested database image target is not declared in the canonical catalog." >&2
    exit 1
fi

printf '%s\n' "$selected_image"
