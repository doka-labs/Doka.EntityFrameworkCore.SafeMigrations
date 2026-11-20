#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
fixture_root="$(mktemp -d)"
trap 'rm -rf -- "$fixture_root"' EXIT
shared_script="$repository_root/eng/verify-ef-tooling.sh"

for tooling_script in "$shared_script" "$repository_root/eng/verify-sqlserver-ef-tooling.sh"; do
    if [[ ! -x "$tooling_script" ]]; then
        echo "Native tooling entrypoint is not executable: $tooling_script" >&2
        exit 1
    fi
done

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

server_identity_case() {
    local engine="$1"
    local version="$2"
    local native_identity="$3"
    local native_status="$4"
    local image=fixture/native:1
    local container_name=safe-migrations-tooling-identity-test
    local repository_root="$fixture_root"
    local startup
    export image
    load_function "$shared_script" run_query
    load_function "$shared_script" wait_for_mysql
    if grep -q '^verify_server_identity() {$' "$shared_script"; then
        load_function "$shared_script" verify_server_identity
    fi

    # WHY: Execute the real startup/exports, not a second identity validator.
    # A wrong native identity must stop before database setup or provider selection.
    startup="$(awk '
        /^if \[\[ "\$\{engine\}" == "postgres" \]\]; then$/ { capture = 1 }
        capture { print }
        capture && /^cd "\$\{repository_root\}"$/ { exit }
    ' "$shared_script")"
    [[ -n "$startup" ]]

    # WHY: The actual extracted startup and run_query invoke this scoped native-client double through eval.
    # shellcheck disable=SC2329
    docker() {
        local arguments=" $* "
        local query="${!#}"
        if [[ "$1" == port ]]; then
            printf '127.0.0.1:5544\n'
        elif [[ "$query" == 'SELECT VERSION(), @@version_comment;' || "$query" == 'SELECT version();' ]]; then
            [[ "$arguments" == *" --connect-timeout=10 "* || "$arguments" == *" PGCONNECT_TIMEOUT=10 "* ]]
            if [[ "$engine" == postgres ]]; then
                [[ "$arguments" == *" psql -X "* && "$arguments" == *" ON_ERROR_STOP=1 "* ]]
            else
                [[ "$arguments" == *" ${client} "* && "$arguments" == *" --default-character-set=utf8mb4 "* ]]
            fi

            printf 'readback\n' >>"$fixture_root/identity-readback"
            printf '%s\n' "$native_identity"

            return "$native_status"
        elif [[ "$arguments" == *" createdb "* || "$query" == 'CREATE DATABASE '* ]]; then
            printf 'database\n' >>"$fixture_root/identity-database-setup"
        fi
    }

    unset SAFE_MIGRATIONS_MYSQL_ENGINE SAFE_MIGRATIONS_MYSQL_VERSION
    eval "$startup"
    if [[ "$engine" != postgres ]]; then
        [[ "$SAFE_MIGRATIONS_MYSQL_ENGINE" == "$engine" && "$SAFE_MIGRATIONS_MYSQL_VERSION" == "$version" ]]
    fi
}

expect_identity_rejection() {
    local expected_status=1
    if [[ "$4" != 0 ]]; then
        expected_status="$4"
    fi

    : >"$fixture_root/identity-readback"
    : >"$fixture_root/identity-database-setup"
    expect_status "$expected_status" server_identity_case "$@"
    grep -Fxq readback "$fixture_root/identity-readback"
    [[ ! -s "$fixture_root/identity-database-setup" ]]
}

mysql_identity=$'9.7.4\tMySQL Community Server - GPL'
mariadb_identity=$'12.3.3-MariaDB-ubu2404\tUbuntu 24.04'
postgres_identity='PostgreSQL 18.4 (Debian 18.4-1.pgdg13+1) on aarch64-unknown-linux-gnu, 64-bit'

# WHY: These first cases are nonvacuous against the old script: it reaches
# provider export and accepts a mismatched patch or a different native family.
expect_identity_rejection mysql 9.7.0 "$mysql_identity" 0
expect_identity_rejection mariadb 12.3.0 "$mariadb_identity" 0
expect_identity_rejection postgres 18.3 "$postgres_identity" 0
expect_identity_rejection mysql 12.3.3 "$mariadb_identity" 0
expect_identity_rejection mariadb 9.7.4 "$mysql_identity" 0
expect_identity_rejection postgres 9.7.4 "$mysql_identity" 0
expect_identity_rejection mysql 9.7.4 $'9.7.4\tPercona Server' 0
expect_identity_rejection mysql 9.7.4 $'9.7.4-unknown\tMySQL Community Server - GPL' 0
expect_identity_rejection mysql 9.7.4 $'invalid\tMySQL Community Server - GPL' 0
expect_identity_rejection mariadb 12.3.3 $'12.3.3-unknown\tUbuntu 24.04' 0
expect_identity_rejection postgres 18.4 'PostgreSQL 18.4invalid' 0
expect_identity_rejection mysql 9.7.4 "$mysql_identity"$'\n'"$mysql_identity" 0
expect_identity_rejection mysql 9.7.4 $'9.7.4\t' 0
expect_identity_rejection mysql 9.7.4 $'\tMySQL Community Server - GPL' 0
expect_identity_rejection mysql 9.7.4 "$mysql_identity"$'\textra' 0
expect_identity_rejection mysql 9.7.4 "$mysql_identity"$'\033[31m' 0
expect_identity_rejection postgres 18.4 $'PostgreSQL\t18.4' 0
for engine in mysql mariadb postgres; do
    expect_identity_rejection "$engine" 9.7.4 '' 0
    expect_identity_rejection "$engine" 9.7.4 "$mysql_identity" 53
done

for accepted in mysql mariadb postgres; do
    case "$accepted" in
        mysql) observed="$mysql_identity"; pinned=9.7.4 ;;
        mariadb) observed="$mariadb_identity"; pinned=12.3.3 ;;
        postgres) observed="$postgres_identity"; pinned=18.4 ;;
    esac

    : >"$fixture_root/identity-readback"
    : >"$fixture_root/identity-database-setup"
    expect_status 0 server_identity_case "$accepted" "$pinned" "$observed" 0
    grep -Fxq readback "$fixture_root/identity-readback"
    grep -Fxq database "$fixture_root/identity-database-setup"
done

# WHY: Native banners are untrusted metadata, not safe diagnostic payloads.
assert_no_native_banner_leak() {
    if grep -Fq 'do-not-log-secret' "$fixture_root/stdout" "$fixture_root/stderr"; then
        echo 'Native identity diagnostics disclosed the banner canary.' >&2
        exit 1
    fi
}

for requested_version in 9.7.4 9.7.0; do
    expected_status=1
    if [[ "$requested_version" == 9.7.4 ]]; then
        expected_status=0
    fi

    expect_status "$expected_status" server_identity_case mysql "$requested_version" \
        $'9.7.4\tMySQL do-not-log-secret' 0
    assert_no_native_banner_leak
done
expect_identity_rejection mysql 9.7.4 $'9.7.4\tMySQL do-not-log-secret\033[31m' 0
assert_no_native_banner_leak

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
