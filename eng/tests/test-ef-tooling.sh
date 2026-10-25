#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
fixture_root="$(mktemp -d)"
trap 'rm -rf -- "$fixture_root"' EXIT
shared_script="$repository_root/eng/verify-ef-tooling.sh"

# WHY: Exercise the actual small shell boundaries, not a second implementation
# of migrations, containers or the CLI. Native engine gates test their behavior.
load_function() {
    local script="$1"
    local name="$2"
    local definition
    definition="$(sed -n "/^${name}() {$/,/^}$/p" "$script")"
    test -n "$definition"
    eval "$definition"
}

expect_status() {
    local expected="$1"
    shift
    local actual=0
    # WHY: Testing the subshell with `||` would disable errexit inside the
    # production function and could make a broken assertion look successful.
    set +e
    (set -e; "$@") >"$fixture_root/stdout" 2>"$fixture_root/stderr"
    actual=$?
    set -e
    if [[ "$actual" != "$expected" ]]; then
        cat "$fixture_root/stderr" >&2
        echo "Expected exit $expected, got $actual: $*" >&2
        exit 1
    fi
}

native_script_case() {
    local engine="$1"
    local native_status="$2"
    local client="$engine"
    local container_name=safe-migrations-tooling-test
    load_function "$shared_script" run_script

    docker() {
        local arguments=" $* "
        [[ "$arguments" == *" -i "* ]]
        [[ "$arguments" != *" --force "* && "$arguments" != *" -1 "* ]]
        if [[ "$engine" == postgres ]]; then
            [[ "$arguments" == *" -X "* && "$arguments" == *" ON_ERROR_STOP=1 "* ]]
        else
            [[ "$arguments" == *" --default-character-set=utf8mb4 "* ]]
            [[ "$arguments" == *" ${client} "* ]]
        fi

        local input
        IFS= read -r input
        [[ "$input" == 'SELECT 42;' ]]

        return "$native_status"
    }

    run_script tooling_generated_strict "$fixture_root/native.sql"
}

printf 'SELECT 42;\n' >"$fixture_root/native.sql"
for engine in mysql mariadb postgres; do
    expect_status 0 native_script_case "$engine" 0
    expect_status 53 native_script_case "$engine" 53
done

readback_case() {
    local fixture_observation="$1"
    load_function "$shared_script" assert_query
    run_query() { printf '%s\n' "$fixture_observation"; }

    assert_query tooling_generated_strict 1 'synthetic independent fact'
}

expect_status 0 readback_case 1
expect_status 1 readback_case 0

unowned_reset_case() {
    load_function "$shared_script" reset_owned_database
    docker() { exit 99; }

    reset_owned_database not_owned
}

expect_status 2 unowned_reset_case

bundle_build_case() {
    local remove_no_build="$1"
    local project=compiled.csproj
    local context=CompiledContext
    local mode=strict
    local artifacts_dir="$fixture_root"
    local bundle_commands
    export project context mode artifacts_dir
    # WHY: A prior RID publish changes copied locks. Execute both actual bundle
    # invocations and reject an implicit non-RID build before that publish.
    bundle_commands="$(awk '
        /^[[:space:]]*dotnet ef migrations bundle([[:space:]]|$)/ {
            command = $0
            while (command ~ /\\$/ && getline > 0) {
                sub(/\\$/, "", command)
                command = command $0
            }
            print command
            count++
        }
        END { if (count != 2) exit 1 }
    ' "$shared_script")"
    if [[ "$remove_no_build" == true ]]; then
        bundle_commands="${bundle_commands// --no-build/}"
    fi

    dotnet() { [[ " $* " == *" --no-build "* ]] || return 74; }
    eval "$bundle_commands"
}

expect_status 0 bundle_build_case false
expect_status 74 bundle_build_case true

index_identity_case() {
    local expected="$1"
    local actual
    load_function "$shared_script" read_generated_index_name
    actual="$(read_generated_index_name "$fixture_root/migration.cs")"

    [[ "$actual" == "$expected" ]]
}

long_index=IX_scaffolding_generated_nodes_HasDescendants_LeftBound_RightBound
for limit in 63 64; do
    index_name="${long_index:0:$((limit - 1))}~"
    printf '    name: "%s",\n' "$index_name" >"$fixture_root/migration.cs"
    expect_status 0 index_identity_case "$index_name"
done

: >"$fixture_root/migration.cs"
expect_status 1 index_identity_case missing
printf 'name: "%s",\nname: "%s",\n' "$long_index" "$long_index" >"$fixture_root/migration.cs"
expect_status 1 index_identity_case ambiguous

sqlite_rejection_case() {
    local diagnostic="$1"
    local cli_status="$2"
    local partial="$3"
    local context="$4"
    local variant="$5"
    local project=synthetic.csproj
    local script_output="$fixture_root/rejected.sql"
    export project
    load_function "$repository_root/eng/verify-sqlite-ef-tooling.sh" verify_script_rejection
    dotnet() {
        [[ "$*" == *"--context $context"* ]]
        if [[ "$variant" == idempotent ]]; then
            [[ "$*" == *--idempotent* ]]
        fi

        printf '%s\n' "$diagnostic"
        if [[ "$partial" == true ]]; then
            printf 'partial SQL;\n' >"$script_output"
        fi

        return "$cli_status"
    }

    rm -f -- "$script_output"
    if [[ "$variant" == idempotent ]]; then
        verify_script_rejection "$context" "$script_output" --idempotent
    else
        verify_script_rejection "$context" "$script_output"
    fi
}

for context in SqliteToolingDbContext SqliteLegacyToolingDbContext; do
    for variant in normal idempotent; do
        expect_status 0 sqlite_rejection_case 'doka_sm_script_unsupported: intended guard' 42 false "$context" "$variant"
        expect_status 1 sqlite_rejection_case 'unrelated startup failure' 42 false "$context" "$variant"
        expect_status 1 sqlite_rejection_case 'doka_sm_script_unsupported: intended guard' 0 false "$context" "$variant"
        expect_status 1 sqlite_rejection_case 'doka_sm_script_unsupported: intended guard' 42 true "$context" "$variant"
    done
done

native_header='System.NotSupportedException: Generating idempotent scripts for migrations is not currently supported '
native_header+='for SQLite. See https://go.microsoft.com/fwlink/?LinkId=723262 for more information and examples.'
native_error="$native_header"$'\n   at Microsoft.EntityFrameworkCore.Sqlite.Migrations.Internal.'
native_error+='SqliteHistoryRepository.GetEndIfScript()'
for context in SqliteToolingDbContext SqliteLegacyToolingDbContext; do
    expect_status 0 sqlite_rejection_case "$native_error" 42 false "$context" idempotent
    expect_status 1 sqlite_rejection_case "$native_error" 42 false "$context" normal
    expect_status 1 sqlite_rejection_case "$native_header" 42 false "$context" idempotent
    expect_status 1 sqlite_rejection_case "$native_error" 0 false "$context" idempotent
    expect_status 1 sqlite_rejection_case "$native_error" 42 true "$context" idempotent
done

cleanup_case() {
    local provider="$1"
    local primary_status="$2"
    local fixture_cleanup_status="$3"
    local temporary_root="$fixture_root"
    local container_requested=true
    local container_started=false
    local sqlcmd_path=""
    local work_dir="$temporary_root/safemigrations-tooling.owned"
    local container_name=safe-migrations-tooling-test
    local cleanup_script="$shared_script"
    case "$provider" in
        sqlite)
            cleanup_script="$repository_root/eng/verify-sqlite-ef-tooling.sh"
            work_dir="$temporary_root/safemigrations-sqlite-tooling.owned"
            ;;
        sqlserver)
            cleanup_script="$repository_root/eng/verify-sqlserver-ef-tooling.sh"
            work_dir="$temporary_root/safemigrations-sqlserver-tooling.owned"
            container_name=safe-migrations-sqlserver-tooling-test
            ;;
    esac

    export container_requested container_started sqlcmd_path work_dir container_name
    load_function "$cleanup_script" cleanup
    docker() { printf 'container\n' >>"$fixture_root/cleanup-attempts"; return "$fixture_cleanup_status"; }
    rm() { printf 'directory\n' >>"$fixture_root/cleanup-attempts"; return "$fixture_cleanup_status"; }
    trap 'cleanup "$?"' EXIT
    exit "$primary_status"
}

for provider in shared sqlite sqlserver; do
    for statuses in '0 0 0' '23 31 23' '0 31 31'; do
        read -r primary cleanup expected <<<"$statuses"
        : >"$fixture_root/cleanup-attempts"
        expect_status "$expected" cleanup_case "$provider" "$primary" "$cleanup"
        grep -Fxq directory "$fixture_root/cleanup-attempts"
        if [[ "$provider" != sqlite ]]; then
            grep -Fxq container "$fixture_root/cleanup-attempts"
        fi
    done
done

echo "EF tooling native-client, independent readback and cleanup counterprobes passed."
