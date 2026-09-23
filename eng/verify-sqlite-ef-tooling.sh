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
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

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
for database_and_migration in \
    "$database_directory/strict-cli.db:$strict_migration_id" \
    "$database_directory/legacy-cli.db:$legacy_migration_id" \
    "$database_directory/strict-bundle.db:$strict_migration_id" \
    "$database_directory/legacy-bundle.db:$legacy_migration_id"; do
    database_path="${database_and_migration%%:*}"
    migration_id="${database_and_migration#*:}"
    dotnet run \
        --project "$consumer_project" \
        --configuration Release --no-build --no-restore \
        -p:SafeMigrationsPackageConsumerMode=Source \
        -p:SafeMigrationsEfToolingReference=Design \
        -- --verify-database "$database_path" "$migration_id"
done

script_path="$work_dir/unsupported.sql"
export SAFE_MIGRATIONS_SQLITE_CONNECTION_STRING="Data Source=$database_directory/strict-cli.db;Foreign Keys=True"
if dotnet ef migrations script \
    --project "$project" \
    --context SqliteToolingDbContext \
    --configuration Release \
    --no-build \
    --output "$script_path"; then
    echo "SQLite tooling unexpectedly generated a safe-operation SQL script." >&2
    exit 1
fi

if [[ -s "$script_path" ]]; then
    echo "SQLite tooling left a partial SQL script after rejecting generation." >&2
    exit 1
fi

evidence_directory="$source_root/artifacts/ef-tooling/sqlite"
mkdir -p "$evidence_directory"
printf '%s\n' \
    'SQLite SafeMigrations EF tooling qualification' \
    '- strict and legacy safe migration scaffolding: passed' \
    '- database update and identical replay: passed for both modes' \
    '- migration bundle application and identical replay: passed for both modes' \
    '- history, schema, model-managed data, and integrity readback: passed' \
    '- safe-operation SQL script rejection without partial output: passed' \
    >"$evidence_directory/summary.txt"

echo "SafeMigrations SQLite EF CLI and bundle tooling verified."
