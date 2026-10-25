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
container_requested=false
container_started=false
sqlcmd_path=""
artifacts_dir="$source_root/artifacts/ef-tooling/sqlserver/$version"

cleanup() {
    local exit_code="$1"
    local cleanup_code=0

    # WHY: Cleanup must attempt both owned resources and preserve the first
    # failure, including a cleanup-only failure after otherwise successful work.
    trap - EXIT

    if [[ "$container_name" == safe-migrations-sqlserver-tooling-* ]]; then
        if [[ "$exit_code" != 0 && "$container_started" == true ]]; then
            # WHY: The disposable server is otherwise lost on failure. Retain
            # diagnostics without replacing the original error or retrying work.
            docker logs "$container_name" >"$artifacts_dir/server.log" 2>&1 || true

            if [[ -n "$sqlcmd_path" ]]; then
                docker exec "$container_name" "$sqlcmd_path" \
                    -S localhost -U sa -P 'SafeMigrationsTooling123!' -C -b -I -l 5 -t 5 \
                    -Q "SELECT session_id, status, command, wait_type, wait_time, blocking_session_id
                        FROM sys.dm_exec_requests WHERE session_id <> @@SPID;
                        SELECT wait_type, waiting_tasks_count, wait_time_ms
                        FROM sys.dm_os_wait_stats
                        WHERE wait_type IN (N'WRITELOG', N'PAGEIOLATCH_SH', N'PAGEIOLATCH_EX',
                            N'IO_COMPLETION', N'ASYNC_IO_COMPLETION');" \
                    >"$artifacts_dir/server-waits.log" 2>&1 || true
            fi

            echo "SQL Server $version tooling failed (exit $exit_code); diagnostics: $artifacts_dir" >&2
        fi

        if [[ "$container_requested" == true ]]; then
            if docker rm -f "$container_name" >/dev/null; then
                container_started=false
            else
                cleanup_code="$?"
                echo "Failed to remove owned SQL Server tooling container: $container_name" >&2

                if [[ "$exit_code" == 0 ]]; then
                    exit_code="$cleanup_code"
                fi
            fi
        fi
    fi

    if [[ "$work_dir" == "$temporary_root"/safemigrations-sqlserver-tooling.* ]]; then
        if rm -rf -- "$work_dir"; then
            :
        else
            cleanup_code="$?"
            echo "Failed to remove SQL Server tooling directory: $work_dir" >&2

            if [[ "$exit_code" == 0 ]]; then
                exit_code="$cleanup_code"
            fi
        fi
    fi

    exit "$exit_code"
}

trap 'cleanup "$?"' EXIT

mkdir -p "$artifacts_dir"
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
container_requested=true
docker run -d --name "$container_name" \
    --platform linux/amd64 \
    -e ACCEPT_EULA=Y \
    -e MSSQL_PID=Developer \
    -e MSSQL_SA_PASSWORD='SafeMigrationsTooling123!' \
    -p 127.0.0.1::1433 "$image" >/dev/null
container_started=true

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
        -S localhost -U sa -P 'SafeMigrationsTooling123!' -C -b -I -l 5 -t 5 \
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

# WHY: SqlClient uses ConnectTimeout for native transaction-manager requests,
# including EF history bootstrap commits; CommandTimeout alone cannot cover them.
# Use the same finite 120-second budget as live test commands, not a timing gate.
tooling_connection_settings='User ID=sa;Password=SafeMigrationsTooling123!;TrustServerCertificate=True'
tooling_connection_settings+=';Connect Timeout=120;Command Timeout=120'
tooling_endpoint="Server=127.0.0.1,$port"

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

    export SAFE_MIGRATIONS_CONNECTION_STRING="$tooling_endpoint;Database=tooling_${mode}_cli;$tooling_connection_settings"
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

    for generated_contract in \
        'scaffolding_generated_nodes' \
        'HasDescendants' \
        'CASE WHEN' \
        'ck_scaffolding_generated_bounds'; do
        if ! grep -Fq "$generated_contract" "$migration_file"; then
            echo "SQL Server scaffolding is missing its generated expression contract: $generated_contract" >&2
            exit 1
        fi
    done

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
            migration_name=SqlServerStrictScaffoldingProbe
            ;;
        legacy)
            context=SqlServerLegacySafeMigrationScaffoldingDbContext
            history_table=__SqlServerLegacySafeMigrationsHistory
            migration_name=SqlServerLegacyScaffoldingProbe
            ;;
    esac

    migration_file="$(find "$project_directory/ScaffoldingProbe/$mode" \
        -type f -name "*_${migration_name}.cs" -print -quit)"
    migration_id="$(basename "$migration_file" .cs)"

    export SAFE_MIGRATIONS_CONNECTION_STRING="$tooling_endpoint;Database=tooling_${mode}_cli;$tooling_connection_settings"
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

    bundle_connection="$tooling_endpoint;Database=tooling_${mode}_bundle;$tooling_connection_settings"
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
            -S localhost -U sa -P 'SafeMigrationsTooling123!' -C -b -I \
            -l 120 -t 120 \
            -Q "CREATE DATABASE [$script_database];"
        docker cp "$script_file" "$container_name:$container_script"

        # WHY: ODBC sqlcmd defaults QUOTED_IDENTIFIER to OFF, unlike SqlClient.
        # Computed indexes require ON; set the caller option without weakening runtime guards.
        for ((script_run = 0; script_run < script_repetitions; script_run++)); do
            docker exec "$container_name" "$sqlcmd_path" \
                -S localhost -U sa -P 'SafeMigrationsTooling123!' -C -b -I \
                -l 120 -t 120 \
                -d "$script_database" -i "$container_script"
        done
    done

    for database in \
        "tooling_${mode}_cli" \
        "tooling_${mode}_bundle" \
        "tooling_${mode}_normal_script" \
        "tooling_${mode}_idempotent_script"; do
        # WHY: History alone can pass after an incomplete or wrong migration.
        # Keep facts independent so extra objects cannot compensate for omissions.
        # All probe rows are synthetic and rolled back, preserving replay state.
        docker exec "$container_name" "$sqlcmd_path" \
            -S localhost -U sa -P 'SafeMigrationsTooling123!' -C -b -I \
            -l 120 -t 120 -d "$database" \
            -Q "SET NOCOUNT ON;
                SET XACT_ABORT ON;
                IF (SELECT COUNT_BIG(*) FROM dbo.scaffolding_users
                    WHERE Id = 1 AND Email = N'administrator@example.test'
                        AND DisplayName = N'Administrator') <> 1
                    THROW 51080, N'doka_tooling_seed', 1;

                IF (SELECT COUNT_BIG(*) FROM dbo.[$history_table]) <> 1
                    OR (SELECT COUNT_BIG(*) FROM dbo.[$history_table]
                        WHERE MigrationId = N'$migration_id') <> 1
                    THROW 51080, N'doka_tooling_history', 1;

                IF (SELECT COUNT_BIG(*)
                FROM sys.indexes AS i
                JOIN sys.index_columns AS ic
                    ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns AS c
                    ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(N'dbo.scaffolding_users')
                    AND i.name = N'IX_scaffolding_users_Email'
                    AND c.name = N'DisplayName'
                    AND ic.is_included_column = 1 AND i.is_unique = 1 AND i.is_disabled = 0) <> 1
                    THROW 51080, N'doka_tooling_included_index', 1;

                DECLARE @generated_object int = OBJECT_ID(N'dbo.scaffolding_generated_nodes', N'U');
                IF @generated_object IS NULL
                    OR (SELECT COUNT_BIG(*) FROM sys.columns WHERE object_id = @generated_object) <> 4
                    OR (SELECT COUNT_BIG(*) FROM sys.columns WHERE object_id = @generated_object
                        AND name = N'Id' AND system_type_id = TYPE_ID(N'int')
                        AND is_nullable = 0 AND is_computed = 0) <> 1
                    OR (SELECT COUNT_BIG(*) FROM sys.columns WHERE object_id = @generated_object
                        AND name IN (N'LeftBound', N'RightBound') AND system_type_id = TYPE_ID(N'bigint')
                        AND is_nullable = 0 AND is_computed = 0) <> 2
                    OR (SELECT COUNT_BIG(*) FROM sys.computed_columns WHERE object_id = @generated_object
                        AND name = N'HasDescendants' AND system_type_id = TYPE_ID(N'bit')
                        AND is_persisted = 0 AND CHARINDEX(N'CASE', UPPER(definition)) > 0
                        AND COLUMNPROPERTY(object_id, name, N'IsDeterministic') = 1) <> 1
                    THROW 51080, N'doka_tooling_generated_columns', 1;

                IF (SELECT COUNT_BIG(*) FROM sys.indexes AS i
                    WHERE i.object_id = @generated_object
                        AND i.name = N'IX_scaffolding_generated_nodes_HasDescendants_LeftBound_RightBound'
                        AND i.type = 2 AND i.is_unique = 0 AND i.is_disabled = 0 AND i.has_filter = 0
                        AND (SELECT COUNT_BIG(*) FROM sys.index_columns AS k
                            WHERE k.object_id = i.object_id AND k.index_id = i.index_id
                                AND k.key_ordinal > 0) = 3
                        AND EXISTS (SELECT 1 FROM sys.index_columns AS k JOIN sys.columns AS c
                            ON c.object_id = k.object_id AND c.column_id = k.column_id
                            WHERE k.object_id = i.object_id AND k.index_id = i.index_id
                                AND k.key_ordinal = 1 AND k.is_descending_key = 0
                                AND c.name = N'HasDescendants')
                        AND EXISTS (SELECT 1 FROM sys.index_columns AS k JOIN sys.columns AS c
                            ON c.object_id = k.object_id AND c.column_id = k.column_id
                            WHERE k.object_id = i.object_id AND k.index_id = i.index_id
                                AND k.key_ordinal = 2 AND k.is_descending_key = 0 AND c.name = N'LeftBound')
                        AND EXISTS (SELECT 1 FROM sys.index_columns AS k JOIN sys.columns AS c
                            ON c.object_id = k.object_id AND c.column_id = k.column_id
                            WHERE k.object_id = i.object_id AND k.index_id = i.index_id
                                AND k.key_ordinal = 3 AND k.is_descending_key = 0 AND c.name = N'RightBound')) <> 1
                    THROW 51080, N'doka_tooling_computed_index', 1;

                IF (SELECT COUNT_BIG(*) FROM sys.check_constraints
                    WHERE parent_object_id = @generated_object AND name = N'ck_scaffolding_generated_bounds'
                        AND is_disabled = 0 AND is_not_trusted = 0) <> 1
                    THROW 51080, N'doka_tooling_check_constraint', 1;

                BEGIN TRANSACTION;
                INSERT INTO dbo.scaffolding_generated_nodes (Id, LeftBound, RightBound)
                    VALUES (-2147483001, 10, 11), (-2147483002, 20, 24),
                        (-2147483003, 9223372036854775806, 9223372036854775807);
                IF (SELECT COUNT_BIG(*) FROM dbo.scaffolding_generated_nodes
                    WHERE Id IN (-2147483001, -2147483003) AND HasDescendants = 0) <> 2
                    OR (SELECT COUNT_BIG(*) FROM dbo.scaffolding_generated_nodes
                        WHERE Id = -2147483002 AND HasDescendants = 1) <> 1
                    THROW 51080, N'doka_tooling_generated_values', 1;

                DECLARE @check_rejected bit = 0;
                BEGIN TRY
                    INSERT INTO dbo.scaffolding_generated_nodes (Id, LeftBound, RightBound)
                        VALUES (-2147483004, 30, 30);
                END TRY
                BEGIN CATCH
                    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
                    IF ERROR_NUMBER() <> 547
                        OR CHARINDEX(N'ck_scaffolding_generated_bounds', ERROR_MESSAGE()) = 0
                        THROW;
                    SET @check_rejected = 1;
                END CATCH;

                IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
                IF @check_rejected <> 1
                    THROW 51080, N'doka_tooling_check_enforcement', 1;

                PRINT N'SQL Server $database schema, generated behavior, data and history verified.';"
    done
done

echo "SQL Server $version Strict and LegacyConvergence EF CLI, scripts, bundles, and replay verified."
