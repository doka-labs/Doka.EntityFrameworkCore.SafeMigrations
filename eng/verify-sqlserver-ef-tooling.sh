#!/usr/bin/env bash
set -euo pipefail

image="${1:?Usage: verify-sqlserver-ef-tooling.sh <image> <version>}"
version="${2:?Usage: verify-sqlserver-ef-tooling.sh <image> <version>}"
if [[ "$(uname -m)" != x86_64 ]]; then
    echo "SQL Server live EF tooling requires a supported x86-64 host; ARM64 emulation is not qualification evidence." >&2
    exit 1
fi

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
temporary_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
work_dir="$(mktemp -d "$temporary_root/safemigrations-sqlserver-tooling.XXXXXX")"

case "$work_dir" in
    "$temporary_root"/safemigrations-sqlserver-tooling.*) ;;
    *)
        echo "Unexpected temporary directory: $work_dir" >&2
        exit 1
        ;;
esac

container_name="safe-migrations-sqlserver-tooling-${RANDOM}-$$"

cleanup() {
    if [[ "$container_name" == safe-migrations-sqlserver-tooling-* ]]; then
        docker rm -f "$container_name" >/dev/null 2>&1 || true
    fi

    if [[ "$work_dir" == "$temporary_root"/safemigrations-sqlserver-tooling.* ]]; then
        rm -rf -- "$work_dir"
    fi
}

trap cleanup EXIT

repository_root="$work_dir/source"
mkdir -p "$repository_root"
rsync -a \
    --exclude '.git/' \
    --exclude 'artifacts/' \
    --exclude 'bin/' \
    --exclude 'obj/' \
    "$source_root/" "$repository_root/"

# A Docker-assigned loopback port avoids collisions between qualification
# cells without exposing this test database on public runner interfaces.
docker run -d --name "$container_name" \
    --platform linux/amd64 \
    -e ACCEPT_EULA=Y \
    -e MSSQL_PID=Developer \
    -e MSSQL_SA_PASSWORD='SafeMigrationsTooling123!' \
    -p 127.0.0.1::1433 "$image" >/dev/null

sqlcmd_path=""
for candidate in /opt/mssql-tools18/bin/sqlcmd /opt/mssql-tools/bin/sqlcmd; do
    if docker exec "$container_name" test -x "$candidate"; then
        sqlcmd_path="$candidate"
        break
    fi
done

if [[ -z "$sqlcmd_path" ]]; then
    echo "SQL Server $version image does not expose the expected sqlcmd client." >&2
    exit 1
fi

ready=false
for _ in {1..120}; do
    if docker exec "$container_name" "$sqlcmd_path" \
        -S localhost -U sa -P 'SafeMigrationsTooling123!' -C \
        -Q 'SELECT 1' >/dev/null 2>&1; then
        ready=true
        break
    fi

    sleep 1
done

if [[ "$ready" != true ]]; then
    echo "SQL Server $version did not become ready." >&2
    docker logs "$container_name" >&2 || true
    exit 1
fi

port="$(docker port "$container_name" 1433/tcp | head -n 1 | awk -F: '{print $NF}')"
project='tests/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests.csproj'
project_directory="$(dirname "$project")"
artifacts_dir="$source_root/artifacts/ef-tooling/sqlserver/$version"
mkdir -p "$artifacts_dir"

cd "$repository_root"
dotnet restore "$project" \
    --disable-parallel --disable-build-servers -m:1 /nodeReuse:false
dotnet build "$project" \
    --configuration Release --no-restore --disable-build-servers -m:1 /nodeReuse:false
dotnet tool restore --tool-manifest "$repository_root/.config/dotnet-tools.json" \
    --disable-parallel

for mode in strict legacy; do
    case "$mode" in
        strict)
            context=SqlServerSafeMigrationScaffoldingDbContext
            migration_name=SqlServerStrictScaffoldingProbe
            ;;
        legacy)
            context=SqlServerLegacySafeMigrationScaffoldingDbContext
            migration_name=SqlServerLegacyScaffoldingProbe
            ;;
    esac

    export SAFE_MIGRATIONS_CONNECTION_STRING="Server=127.0.0.1,$port;Database=tooling_${mode}_cli;User ID=sa;Password=SafeMigrationsTooling123!;TrustServerCertificate=True"
    dotnet ef migrations add "$migration_name" \
        --project "$project" \
        --context "$context" \
        --output-dir "ScaffoldingProbe/$mode" \
        --configuration Release \
        --no-build

    migration_file="$(find "$project_directory/ScaffoldingProbe/$mode" \
        -type f -name "*_${migration_name}.cs" -print -quit)"

    if [[ -z "$migration_file" ]]; then
        echo "SQL Server $version $mode EF tooling did not scaffold a migration." >&2
        exit 1
    fi

    grep -Fq 'migrationBuilder.EnsureModelManagedDataFromModel(' "$migration_file"
    grep -Fq 'includedColumns: ["DisplayName"]' "$migration_file"

    if grep -Fq 'migrationBuilder.CreateTable(' "$migration_file" \
        || grep -Fq '"SqlServer:Include"' "$migration_file"; then
        echo "SQL Server $version $mode EF tooling emitted ordinary DDL or leaked a provider annotation." >&2
        exit 1
    fi

    if [[ "$mode" == strict ]]; then
        grep -Fq 'migrationBuilder.CreateTableIfNotExists(' "$migration_file"
        grep -Fq 'migrationBuilder.DropTableIfExists(' "$migration_file"
    else
        grep -Fq 'migrationBuilder.ConvergeTableFromModel(' "$migration_file"
        grep -Fq 'SafeMigrationPolicy.RepairIfSafe' "$migration_file"

        if grep -Fq 'migrationBuilder.DropTableIfExists(' "$migration_file"; then
            echo "SQL Server $version legacy down migration unexpectedly drops the converged table." >&2
            exit 1
        fi
    fi
done

dotnet build "$project" \
    --configuration Release --no-restore --disable-build-servers -m:1 /nodeReuse:false

for mode in strict legacy; do
    case "$mode" in
        strict)
            context=SqlServerSafeMigrationScaffoldingDbContext
            history_table=__SqlServerSafeMigrationsHistory
            ;;
        legacy)
            context=SqlServerLegacySafeMigrationScaffoldingDbContext
            history_table=__SqlServerLegacySafeMigrationsHistory
            ;;
    esac

    export SAFE_MIGRATIONS_CONNECTION_STRING="Server=127.0.0.1,$port;Database=tooling_${mode}_cli;User ID=sa;Password=SafeMigrationsTooling123!;TrustServerCertificate=True"
    dotnet ef migrations script \
        --project "$project" \
        --context "$context" \
        --configuration Release \
        --no-build \
        --output "$artifacts_dir/$mode-migration.sql"

    dotnet ef migrations script \
        --project "$project" \
        --context "$context" \
        --configuration Release \
        --no-build \
        --idempotent \
        --output "$artifacts_dir/$mode-migration-idempotent.sql"

    test -s "$artifacts_dir/$mode-migration.sql"
    test -s "$artifacts_dir/$mode-migration-idempotent.sql"
    grep -Fq 'IF NOT EXISTS' "$artifacts_dir/$mode-migration-idempotent.sql"

    dotnet ef database update \
        --project "$project" \
        --context "$context" \
        --configuration Release \
        --no-build
    dotnet ef database update \
        --project "$project" \
        --context "$context" \
        --configuration Release \
        --no-build

    dotnet ef migrations bundle \
        --project "$project" \
        --context "$context" \
        --configuration Release \
        --no-build \
        --output "$work_dir/sqlserver-$mode-migration-bundle"

    bundle_connection="Server=127.0.0.1,$port;Database=tooling_${mode}_bundle;User ID=sa;Password=SafeMigrationsTooling123!;TrustServerCertificate=True"
    "$work_dir/sqlserver-$mode-migration-bundle" --connection "$bundle_connection"
    "$work_dir/sqlserver-$mode-migration-bundle" --connection "$bundle_connection"

    # WHY: Generating a script does not qualify its executable batch boundaries.
    # Normal scripts apply once; only idempotent scripts promise history replay.
    for script_kind in normal idempotent; do
        script_database="tooling_${mode}_${script_kind}_script"
        script_file="$artifacts_dir/$mode-migration.sql"
        script_repetitions=1
        if [[ "$script_kind" == idempotent ]]; then
            script_file="$artifacts_dir/$mode-migration-idempotent.sql"
            script_repetitions=2
        fi

        container_script="/tmp/safemigrations-$mode-$script_kind.sql"
        docker exec "$container_name" "$sqlcmd_path" \
            -S localhost -U sa -P 'SafeMigrationsTooling123!' -C -b \
            -Q "CREATE DATABASE [$script_database];"
        docker cp "$script_file" "$container_name:$container_script"

        for ((script_run = 0; script_run < script_repetitions; script_run++)); do
            docker exec "$container_name" "$sqlcmd_path" \
                -S localhost -U sa -P 'SafeMigrationsTooling123!' -C -b \
                -d "$script_database" -i "$container_script"
        done
    done

    for database in \
        "tooling_${mode}_cli" \
        "tooling_${mode}_bundle" \
        "tooling_${mode}_normal_script" \
        "tooling_${mode}_idempotent_script"; do
        row_count="$(docker exec "$container_name" "$sqlcmd_path" \
            -S localhost -U sa -P 'SafeMigrationsTooling123!' -C -b \
            -d "$database" -h -1 -W \
            -Q 'SET NOCOUNT ON; SELECT COUNT_BIG(*) FROM dbo.scaffolding_users WHERE Email = '\''administrator@example.test'\'';' \
            | tr -d '[:space:]')"
        history_count="$(docker exec "$container_name" "$sqlcmd_path" \
            -S localhost -U sa -P 'SafeMigrationsTooling123!' -C -b \
            -d "$database" -h -1 -W \
            -Q "SET NOCOUNT ON; SELECT COUNT_BIG(*) FROM dbo.[$history_table];" \
            | tr -d '[:space:]')"
        included_index_count="$(docker exec "$container_name" "$sqlcmd_path" \
            -S localhost -U sa -P 'SafeMigrationsTooling123!' -C -b \
            -d "$database" -h -1 -W \
            -Q "SET NOCOUNT ON;
                SELECT COUNT_BIG(*)
                FROM sys.indexes AS i
                JOIN sys.index_columns AS ic
                    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns AS c
                    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(N'dbo.scaffolding_users')
                    AND i.name = N'IX_scaffolding_users_Email'
                    AND c.name = N'DisplayName'
                    AND ic.is_included_column = 1;" \
            | tr -d '[:space:]')"

        if [[ "$row_count" != 1 || "$history_count" != 1 || "$included_index_count" != 1 ]]; then
            echo "SQL Server $version $database did not retain the model-managed row, history entry, and included index column after replay." >&2
            exit 1
        fi
    done
done

echo "SQL Server $version Strict and LegacyConvergence EF CLI, scripts, bundles, and replay verified."
