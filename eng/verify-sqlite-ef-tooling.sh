#!/usr/bin/env bash

set -euo pipefail

if (($# != 0)); then
    echo "Usage: $0" >&2
    exit 2
fi

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
source_root="$(cd "$script_dir/.." && pwd -P)"
temporary_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
work_dir="$(mktemp -d "$temporary_root/safemigrations-sqlite-tooling.XXXXXX")"

case "$work_dir" in
    "$temporary_root"/safemigrations-sqlite-tooling.*) ;;
    *)
        echo "Unexpected temporary directory: $work_dir" >&2
        exit 1
        ;;
esac

cleanup() {
    local original_exit=$?
    local cleanup_exit
    trap - EXIT
    set +e

    rm -rf -- "$work_dir"
    cleanup_exit=$?
    if ((cleanup_exit != 0)); then
        echo "SQLite tooling cleanup failed (exit $cleanup_exit)." >&2
        if ((original_exit == 0)); then
            original_exit=$cleanup_exit
        fi
    fi

    exit "$original_exit"
}
trap cleanup EXIT

# WHY: An unrelated design-time/startup error is not evidence that the intended
# script capability guard ran. Keep both failure identity and no-output checks.
verify_script_rejection() {
    local context=$1
    local output_path=$2
    shift 2
    local diagnostic_path="$output_path.log"
    local idempotent_requested=false
    local argument
    for argument in "$@"; do
        if [[ "$argument" == --idempotent ]]; then
            idempotent_requested=true
        fi
    done

    local native_idempotent_message="System.NotSupportedException: Generating idempotent scripts for migrations "
    native_idempotent_message+="is not currently supported for SQLite. "
    native_idempotent_message+="See https://go.microsoft.com/fwlink/?LinkId=723262 for more information and examples."
    local native_idempotent_stack='^[[:space:]]+at Microsoft\.EntityFrameworkCore\.Sqlite\.Migrations\.Internal\.'
    native_idempotent_stack+='SqliteHistoryRepository\.GetEndIfScript\(\)($|[[:space:]])'

    # WHY: EF's native capability diagnostic is localized; select English for
    # the exact expected-error contract rather than accepting arbitrary failure.
    if LC_ALL=C LANG=C DOTNET_CLI_UI_LANGUAGE=en-US dotnet ef migrations script \
        --project "$project" \
        --context "$context" \
        --configuration Release \
        --no-build \
        --output "$output_path" "$@" >"$diagnostic_path" 2>&1; then
        cat "$diagnostic_path"
        echo "SQLite tooling unexpectedly generated a safe-operation SQL script." >&2
        return 1
    fi

    cat "$diagnostic_path"
    local expected_rejection=false
    if grep -Eq '(^|[[:space:]])doka_sm_script_unsupported:' "$diagnostic_path"; then
        expected_rejection=true
    elif [[ "$idempotent_requested" == true ]] \
        && grep -Fxq "$native_idempotent_message" "$diagnostic_path" \
        && grep -Eq "$native_idempotent_stack" "$diagnostic_path"; then
        # WHY: The pinned EF SQLite history repository rejects idempotent
        # scripts before SafeMigrations sees the operations. Normal scripts
        # must still reach the SafeMigrations guard, not this separate boundary.
        expected_rejection=true
    fi

    if [[ "$expected_rejection" != true ]]; then
        echo "SQLite script rejection did not reach its expected capability guard ($context)." >&2
        return 1
    fi

    if [[ -s "$output_path" ]]; then
        echo "SQLite tooling left a partial SQL script after rejecting generation." >&2
        return 1
    fi
}

repository_root="$work_dir/repository"
rsync -a \
    --exclude '.fastembed_cache/' \
    --exclude '.git/' \
    --exclude 'artifacts/' \
    --exclude 'bin/' \
    --exclude 'obj/' \
    "$source_root/" "$repository_root/"

project="tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests.csproj"
project_directory="$(dirname "$project")"
strict_output_directory="ScaffoldingProbes/Sqlite/Strict"
legacy_output_directory="ScaffoldingProbes/Sqlite/Legacy"
strict_migration_name="SqliteStrictToolingProbe"
legacy_migration_name="SqliteLegacyToolingProbe"
consumer_project="eng/package-consumer/Sqlite/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.PackageConsumer.csproj"
database_directory="$work_dir/databases"
mkdir -p "$database_directory"

cd "$repository_root"
dotnet restore "$project" \
    --disable-parallel --disable-build-servers -m:1 /nodeReuse:false
dotnet build "$project" \
    --configuration Release --no-restore --disable-build-servers -m:1 /nodeReuse:false
dotnet tool restore --tool-manifest "$repository_root/.config/dotnet-tools.json" \
    --disable-parallel

export SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING="Data Source=$database_directory/strict-cli.db;Foreign Keys=True"
unset SAFE_MIGRATIONS_SQLITE_TOOLING_STATE
dotnet ef migrations add "$strict_migration_name" \
    --project "$project" \
    --context SqliteToolingDbContext \
    --output-dir "$strict_output_directory" \
    --configuration Release \
    --no-build

export SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING="Data Source=$database_directory/legacy-cli.db;Foreign Keys=True"
dotnet ef migrations add "$legacy_migration_name" \
    --project "$project" \
    --context SqliteLegacyToolingDbContext \
    --output-dir "$legacy_output_directory" \
    --configuration Release \
    --no-build

strict_migration_file="$(find "$project_directory/$strict_output_directory" \
    -type f -name "*_${strict_migration_name}.cs" -print -quit)"
legacy_migration_file="$(find "$project_directory/$legacy_output_directory" \
    -type f -name "*_${legacy_migration_name}.cs" -print -quit)"
if [[ -z "$strict_migration_file" || -z "$legacy_migration_file" ]]; then
    echo "SQLite tooling did not scaffold both strict and legacy migrations." >&2
    exit 1
fi

# WHY: A compiled baseline snapshot is required before EF can scaffold the
# add/drop/rename transition against the previous model rather than null.
dotnet build "$project" \
    --configuration Release --no-restore --disable-build-servers -m:1 /nodeReuse:false
export SAFE_MIGRATIONS_SQLITE_TOOLING_STATE="target"

strict_transition_name="SqliteStrictColumnTransition"
legacy_transition_name="SqliteLegacyColumnTransition"
export SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING="Data Source=$database_directory/strict-cli.db;Foreign Keys=True"
dotnet ef migrations add "$strict_transition_name" \
    --project "$project" \
    --context SqliteToolingDbContext \
    --output-dir "$strict_output_directory" \
    --configuration Release \
    --no-build

export SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING="Data Source=$database_directory/legacy-cli.db;Foreign Keys=True"
dotnet ef migrations add "$legacy_transition_name" \
    --project "$project" \
    --context SqliteLegacyToolingDbContext \
    --output-dir "$legacy_output_directory" \
    --configuration Release \
    --no-build

strict_transition_file="$(find "$project_directory/$strict_output_directory" \
    -type f -name "*_${strict_transition_name}.cs" -print -quit)"
legacy_transition_file="$(find "$project_directory/$legacy_output_directory" \
    -type f -name "*_${legacy_transition_name}.cs" -print -quit)"
if [[ -z "$strict_transition_file" || -z "$legacy_transition_file" ]]; then
    echo "SQLite tooling did not scaffold both column transitions." >&2
    exit 1
fi

for migration in "$strict_transition_file" "$legacy_transition_file"; do
    if ! grep -Eq '^        migrationBuilder\.DropColumnIfExists\(' "$migration"; then
        echo "SQLite column-transition scaffolding has incorrect first-operation indentation." >&2
        exit 1
    fi

    for expected in \
        'migrationBuilder.AddColumnIfNotExistsFromModel(' \
        'migrationBuilder.DropColumnIfExists(' \
        'migrationBuilder.RenameColumnIfExists('; do
        if ! grep -Fq "$expected" "$migration"; then
            echo "SQLite column-transition scaffolding is missing: $expected" >&2
            grep -n 'migrationBuilder\.' "$migration" >&2 || true
            exit 1
        fi
    done
done

grep -Fq 'migrationBuilder.CreateTableIfNotExists(' "$strict_migration_file"
grep -Fq 'migrationBuilder.EnsureModelManagedDataFromModel(' "$strict_migration_file"
grep -Fq 'using Doka.EntityFrameworkCore.SafeMigrations;' "$strict_migration_file"
grep -Fq 'migrationBuilder.ConvergeTableFromModel(' "$legacy_migration_file"
grep -Fq 'migrationBuilder.EnsureModelManagedDataFromModel(' "$legacy_migration_file"
grep -Fq 'using Doka.EntityFrameworkCore.SafeMigrations;' "$legacy_migration_file"

for migration in "$strict_migration_file" "$legacy_migration_file"; do
    for generated_contract in \
        'scaffolding_generated_nodes' \
        'HasDescendants' \
        'CASE WHEN' \
        'ck_scaffolding_generated_bounds'; do
        if ! grep -Fq "$generated_contract" "$migration"; then
            echo "SQLite scaffolding is missing its generated expression contract: $generated_contract" >&2
            exit 1
        fi
    done
done

if grep -Fq 'migrationBuilder.CreateTable(' "$strict_migration_file" \
    || grep -Fq 'migrationBuilder.InsertData(' "$strict_migration_file" \
    || grep -Fq 'migrationBuilder.CreateTable(' "$legacy_migration_file" \
    || grep -Fq 'migrationBuilder.InsertData(' "$legacy_migration_file"; then
    echo "SQLite tooling bypassed SafeMigrations scaffolding." >&2
    exit 1
fi

dotnet build "$project" \
    --configuration Release --no-restore --disable-build-servers -m:1 /nodeReuse:false
export SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING="Data Source=$database_directory/strict-cli.db;Foreign Keys=True"
dotnet ef database update \
    --project "$project" \
    --context SqliteToolingDbContext \
    --configuration Release \
    --no-build
dotnet ef database update \
    --project "$project" \
    --context SqliteToolingDbContext \
    --configuration Release \
    --no-build

export SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING="Data Source=$database_directory/legacy-cli.db;Foreign Keys=True"
dotnet ef database update \
    --project "$project" \
    --context SqliteLegacyToolingDbContext \
    --configuration Release \
    --no-build
dotnet ef database update \
    --project "$project" \
    --context SqliteLegacyToolingDbContext \
    --configuration Release \
    --no-build

# WHY: `dotnet ef migrations bundle` publishes for the current RID and may
# rewrite copied package-project lock files. Build the readback consumer first
# so the later mutation cannot influence its source-graph verification.
dotnet restore "$consumer_project" \
    --disable-parallel --disable-build-servers -m:1 /nodeReuse:false \
    -p:SafeMigrationsPackageConsumerMode=Source \
    -p:SafeMigrationsEfToolingReference=Design
dotnet build "$consumer_project" \
    --configuration Release --no-restore --disable-build-servers -m:1 /nodeReuse:false \
    -p:SafeMigrationsPackageConsumerMode=Source \
    -p:SafeMigrationsEfToolingReference=Design

strict_bundle_path="$work_dir/sqlite-strict-migration-bundle"
dotnet ef migrations bundle \
    --project "$project" \
    --context SqliteToolingDbContext \
    --configuration Release \
    --no-build \
    --verbose \
    --output "$strict_bundle_path"

legacy_bundle_path="$work_dir/sqlite-legacy-migration-bundle"
dotnet ef migrations bundle \
    --project "$project" \
    --context SqliteLegacyToolingDbContext \
    --configuration Release \
    --no-build \
    --verbose \
    --output "$legacy_bundle_path"

strict_bundle_connection="Data Source=$database_directory/strict-bundle.db;Foreign Keys=True"
"$strict_bundle_path" --connection "$strict_bundle_connection"
"$strict_bundle_path" --connection "$strict_bundle_connection"

legacy_bundle_connection="Data Source=$database_directory/legacy-bundle.db;Foreign Keys=True"
"$legacy_bundle_path" --connection "$legacy_bundle_connection"
"$legacy_bundle_path" --connection "$legacy_bundle_connection"

strict_migration_id="$(basename "$strict_migration_file" .cs)"
legacy_migration_id="$(basename "$legacy_migration_file" .cs)"
strict_transition_id="$(basename "$strict_transition_file" .cs)"
legacy_transition_id="$(basename "$legacy_transition_file" .cs)"
for database_name in strict-cli legacy-cli strict-bundle legacy-bundle; do
    if [[ "$database_name" == strict-* ]]; then
        migration_id="$strict_migration_id"
        transition_id="$strict_transition_id"
    else
        migration_id="$legacy_migration_id"
        transition_id="$legacy_transition_id"
    fi

    dotnet run \
        --project "$consumer_project" \
        --configuration Release --no-build --no-restore \
        -p:SafeMigrationsPackageConsumerMode=Source \
        -p:SafeMigrationsEfToolingReference=Design \
        -- --verify-database "$database_directory/$database_name.db" "$migration_id" "$transition_id"
done

for mode in strict legacy; do
    if [[ "$mode" == strict ]]; then
        context="SqliteToolingDbContext"
    else
        context="SqliteLegacyToolingDbContext"
    fi

    export SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING="Data Source=$database_directory/$mode-cli.db;Foreign Keys=True"
    verify_script_rejection "$context" "$work_dir/$mode-unsupported.sql"
    verify_script_rejection "$context" "$work_dir/$mode-idempotent-unsupported.sql" --idempotent
done

evidence_directory="$source_root/artifacts/ef-tooling/sqlite"
mkdir -p "$evidence_directory"
printf '%s\n' \
    'SQLite SafeMigrations EF tooling qualification' \
    '- strict and legacy safe migration scaffolding: passed' \
    '- database update and identical replay: passed for both modes' \
    '- migration bundle application and identical replay: passed for both modes' \
    '- exact initial/transition history, final schema, generated CASE/CHECK/index, data and integrity: passed' \
    '- strict/legacy normal/idempotent scripts: expected guard verified without partial output' \
    >"$evidence_directory/summary.txt"

echo "SafeMigrations SQLite EF CLI and bundle tooling verified."
