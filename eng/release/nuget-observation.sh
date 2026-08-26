#!/usr/bin/env bash

nuget_validate_polling() {
    if [[ ! "$1" =~ ^[1-9][0-9]{0,3}$ || ! "$2" =~ ^[1-9][0-9]?$ ]] \
        || (( $1 > 3600 || $2 > 60 )); then
        echo "Timeout must be an integer from 1 to 3600 seconds; poll interval must be from 1 to 60." >&2
        return 2
    fi
}

nuget_record() {
    printf '%s: %s\n' "$1" "$2"
    if [[ -n "${nuget_observation_log:-}" ]]; then
        printf '%s: %s\n' "$1" "$2" >> "$nuget_observation_log"
    fi
}

nuget_request() {
    local subject="$1"
    local url="$2"
    local destination="$3"
    local deadline="$4"
    local request_timeout=$((deadline - SECONDS))
    local status
    local curl_exit
    shift 4

    if ((request_timeout <= 0)); then
        nuget_http_state=retryable
        return
    fi
    if ((request_timeout > 60)); then
        request_timeout=60
    fi

    if status="$(curl --silent --show-error --location --connect-timeout 10 \
        --max-time "$request_timeout" --output "$destination" --write-out '%{http_code}' "$@" "$url")"; then
        case "$status" in
            200) nuget_http_state=available ;;
            404) nuget_http_state=absent ;;
            408|429|5[0-9][0-9]) nuget_http_state=retryable ;;
            *)
                nuget_record "$subject" "terminal HTTP $status"
                echo "NuGet returned HTTP $status while reading $subject." >&2
                return 1
                ;;
        esac
        nuget_record "$subject" "$nuget_http_state (HTTP $status)"
        if [[ "$nuget_http_state" == retryable ]]; then
            echo "NuGet returned HTTP $status while reading $subject; retrying within the deadline." >&2
        fi
    else
        curl_exit=$?
        case "$curl_exit" in
            5|6|7|16|18|28|52|55|56|92|95)
                nuget_http_state=retryable
                nuget_record "$subject" "retryable transport failure (curl exit $curl_exit)"
                echo "NuGet transport failure (curl exit $curl_exit) while reading $subject." >&2
                ;;
            *)
                nuget_record "$subject" "terminal curl exit $curl_exit"
                echo "NuGet request failed with terminal curl exit $curl_exit while reading $subject." >&2
                return 1
                ;;
        esac
    fi
}

nuget_wait() {
    local delay=$(( $1 - SECONDS ))
    if ((delay <= 0)); then
        return 1
    fi
    if ((delay > $2)); then
        delay="$2"
    fi
    sleep "$delay"
}

nuget_compare_package() {
    local expected_package="$1"
    local published_package="$2"
    local verification_log="$3"

    if ! nuget_package_state="$(python3 - "$expected_package" "$published_package" <<'PY'
import sys
import zipfile
import zlib


def entries(archive):
    metadata = archive.infolist()
    result = {entry.filename: entry for entry in metadata}
    if len(metadata) != len(result):
        raise ValueError("duplicate ZIP entries")
    return result


try:
    with zipfile.ZipFile(sys.argv[1]) as expected, zipfile.ZipFile(sys.argv[2]) as published:
        expected_entries = entries(expected)
        published_entries = entries(published)
        signature = published_entries.pop(".signature.p7s", None)
        if expected_entries.keys() != published_entries.keys():
            raise ValueError("published payload differs from the qualified package")
        for name, entry in expected_entries.items():
            if entry.file_size != published_entries[name].file_size:
                raise ValueError(f"entry size differs from the qualified package: {name}")
        for name in expected_entries:
            with expected.open(name) as expected_file, published.open(name) as published_file:
                while True:
                    expected_chunk = expected_file.read(65536)
                    published_chunk = published_file.read(65536)
                    if expected_chunk != published_chunk:
                        raise ValueError(f"published payload differs from the qualified package: {name}")
                    if not expected_chunk:
                        break
        if signature is not None:
            with published.open(signature) as signature_file:
                while signature_file.read(65536):
                    pass
except (OSError, ValueError, RuntimeError, zipfile.BadZipFile, zlib.error) as error:
    print(f"NuGet invalid package archive or payload: {error}", file=sys.stderr)
    sys.exit(1)

print("matching-signed" if signature is not None else "matching-pending-signature")
PY
    )"; then
        return 1
    fi

    if [[ "$nuget_package_state" == matching-signed ]]; then
        if ! dotnet nuget verify "$published_package" --all > "$verification_log" 2>&1; then
            cat "$verification_log" >&2
            return 1
        fi
        cat "$verification_log"
    fi
}

nuget_symbol_entry() {
    local entry
    entry="$(jq -r --arg package_id "$2" \
        '[.symbols[] | select(.packageId == $package_id)]
            | if length == 0 then empty
              elif length != 1 then error("Qualified symbol manifest entries must be unique.")
              else .[0] | [.pdbName, .symbolUrl, .checksumHeader, .sha256] | @tsv end' "$1")" || return 1
    if [[ -z "$entry" ]]; then
        echo "Qualified symbol manifest omits $2." >&2
        return 1
    fi
    printf '%s\n' "$entry"
}

nuget_compare_symbol() {
    local published_symbol="$1"
    local expected_sha256="$2"
    local package_id="$3"
    local actual_sha256
    local pdb_header
    if ! actual_sha256="$(shasum -a 256 "$published_symbol" | awk '{print $1}')"; then
        echo "NuGet symbol checksum calculation failed for $package_id." >&2
        return 1
    fi
    if ! pdb_header="$(head -c 4 "$published_symbol")"; then
        echo "NuGet Portable PDB header read failed for $package_id." >&2
        return 1
    fi
    if [[ "$pdb_header" != BSJB || "$actual_sha256" != "$expected_sha256" ]]; then
        echo "NuGet symbols differ from the qualified Portable PDB: $package_id" >&2
        return 1
    fi
}
