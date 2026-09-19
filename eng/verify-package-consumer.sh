#!/usr/bin/env bash

set -euo pipefail

usage() {
    echo "Usage: $0 --package-dir <path> --version <version> --doka-source <path-or-url>" >&2
}

package_dir=""
package_version=""
doka_source=""

while (($# > 0)); do
    case "$1" in
        --package-dir)
            package_dir="${2:-}"
            shift 2
            ;;
        --version)
            package_version="${2:-}"
            shift 2
            ;;
        --doka-source)
            doka_source="${2:-}"
            shift 2
            ;;
        *)
            usage
            exit 2
            ;;
    esac
done

if [[ -z "$package_dir" || -z "$package_version" || -z "$doka_source" ]]; then
    usage
    exit 2
fi

package_dir="$(cd "$package_dir" && pwd -P)"
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd -P)"
core_nuspec="$(unzip -p \
    "$package_dir/Doka.EntityFrameworkCore.SafeMigrations.$package_version.nupkg" \
    Doka.EntityFrameworkCore.SafeMigrations.nuspec)"
ef_core_range="$(
    grep -o '<dependency id="Microsoft.EntityFrameworkCore.Relational" version="[^"]*"' \
        <<<"$core_nuspec" \
        | sed -E 's/.* version="([^"]*)"/\1/'
)"

if [[ ! "$ef_core_range" =~ ^\[([0-9]+\.[0-9]+\.[0-9]+),[[:space:]]*[0-9]+\.[0-9]+\.[0-9]+\)$ ]]; then
    echo "Core package does not declare the expected bounded EF Core dependency contract." >&2
    exit 1
fi
ef_core_version="${BASH_REMATCH[1]}"

for package_id in \
    Doka.EntityFrameworkCore.SafeMigrations \
    Doka.EntityFrameworkCore.SafeMigrations.MySql \
    Doka.EntityFrameworkCore.SafeMigrations.PostgreSql \
    Doka.EntityFrameworkCore.SafeMigrations.Sqlite; do
    test -f "$package_dir/$package_id.$package_version.nupkg"
    test -f "$package_dir/$package_id.$package_version.snupkg"
done

temporary_root="${TMPDIR:-/tmp}"
work_dir="$(mktemp -d "$temporary_root/safemigrations-consumer.XXXXXX")"
case "$work_dir" in
    "$temporary_root"/safemigrations-consumer.*) ;;
    *)
        echo "Unexpected temporary directory: $work_dir" >&2
        exit 1
        ;;
esac

cleanup() {
    rm -rf -- "$work_dir"
}
trap cleanup EXIT

package_consumer_project_name() {
    local consumer_name="$1"

    case "$consumer_name" in
        MySql)
            printf '%s\n' "Doka.EntityFrameworkCore.SafeMigrations.MySql.PackageConsumer.csproj"
            ;;
        PostgreSql)
            printf '%s\n' "Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.PackageConsumer.csproj"
            ;;
        Sqlite)
            printf '%s\n' "Doka.EntityFrameworkCore.SafeMigrations.Sqlite.PackageConsumer.csproj"
            ;;
        *)
            echo "Unknown package consumer: $consumer_name" >&2
            exit 1
            ;;
    esac
}

assert_design_reference_count() {
    local assembly_info_file="$1"
    local expected_count="$2"
    local actual_count

    if [[ ! -f "$assembly_info_file" ]]; then
        echo "Generated assembly info is missing: $assembly_info_file" >&2
        exit 1
    fi

    actual_count="$(
        grep -Fc \
            'Microsoft.EntityFrameworkCore.Design.DesignTimeServicesReferenceAttribute' \
            "$assembly_info_file" \
            || true
    )"

    if [[ "$actual_count" -ne "$expected_count" ]]; then
        echo \
            "Expected $expected_count SafeMigrations design reference(s) in $assembly_info_file, found $actual_count." \
            >&2
        exit 1
    fi
}

assert_safe_scaffolding_source() {
    local consumer_name="$1"
    local migration_file="$2"

    case "$consumer_name" in
        MySql)
            if ! grep -Fq 'migrationBuilder.ConvergeTableFromModel(' "$migration_file"; then
                echo "$consumer_name incremental migration did not use ConvergeTableFromModel." >&2
                sed -n '1,220p' "$migration_file" >&2
                exit 1
            fi
            ;;
        PostgreSql | Sqlite)
            if ! grep -Fq 'migrationBuilder.CreateTableIfNotExists(' "$migration_file"; then
                echo "$consumer_name incremental migration did not use CreateTableIfNotExists." >&2
                sed -n '1,220p' "$migration_file" >&2
                exit 1
            fi
            ;;
        *)
            echo "Unknown package consumer: $consumer_name" >&2
            exit 1
            ;;
    esac

    if ! grep -Fq 'using Doka.EntityFrameworkCore.SafeMigrations;' "$migration_file"; then
        echo "$consumer_name incremental migration is missing the SafeMigrations namespace." >&2
        exit 1
    fi

    if grep -Fq 'migrationBuilder.CreateTable(' "$migration_file"; then
        echo "$consumer_name incremental consumer bypassed SafeMigrations scaffolding." >&2
        exit 1
    fi
}

mkdir -p "$work_dir/.config" "$work_dir/eng/package-consumer"
cp "$script_dir/../.config/dotnet-tools.json" "$work_dir/.config/dotnet-tools.json"
cp "$script_dir/../.editorconfig" "$work_dir/.editorconfig"
cp "$script_dir/../Directory.Build.props" "$work_dir/Directory.Build.props"
cp "$script_dir/../global.json" "$work_dir/global.json"
cp "$script_dir/Directory.Build.props" "$work_dir/eng/Directory.Build.props"
cp "$script_dir/package-consumer/Directory.Build.props" \
    "$work_dir/eng/package-consumer/Directory.Build.props"
dotnet tool restore --tool-manifest "$work_dir/.config/dotnet-tools.json" --disable-parallel

verify_consumer() {
    local consumer_name="$1"
    local tooling_reference="$2"
    local consumer_dir="$work_dir/eng/package-consumer/$consumer_name-$tooling_reference"
    local assets_file
    local consumer_project
    local expects_design_reference
    local scaffolding_expectation
    local source_project
    local -a msbuild_properties
    local -a restore_args

    case "$tooling_reference" in
        Design | Tools)
            expects_design_reference=true
            scaffolding_expectation=Safe
            ;;
        DesignWithoutSafeBuildAssets)
            expects_design_reference=false
            scaffolding_expectation=GuardRejects
            ;;
        None)
            expects_design_reference=false
            scaffolding_expectation=EfRejects
            ;;
        *)
            echo "Unknown EF tooling reference: $tooling_reference" >&2
            exit 1
            ;;
    esac

    source_project="$(package_consumer_project_name "$consumer_name")"

    mkdir -p "$consumer_dir"
    cp "$script_dir/package-consumer/$consumer_name/$source_project" \
        "$consumer_dir/$source_project"
    cp "$script_dir/package-consumer/$consumer_name/Imports.cs" "$consumer_dir/"
    cp "$script_dir/package-consumer/$consumer_name/Program.cs" "$consumer_dir/"

    consumer_project="$consumer_dir/$source_project"

    msbuild_properties=(
        -p:SafeMigrationsPackageVersion="$package_version"
        -p:EfCorePackageVersion="$ef_core_version"
        -p:SafeMigrationsPackageConsumerMode=Package
        -p:SafeMigrationsEfToolingReference="$tooling_reference"
    )

    restore_args=(
        "$consumer_project"
        --packages "$work_dir/packages"
        --source "$package_dir"
        --source "$doka_source"
        --source "https://api.nuget.org/v3/index.json"
        --use-lock-file
        --disable-parallel
        "${msbuild_properties[@]}"
    )

    dotnet restore "${restore_args[@]}"
    dotnet restore "${restore_args[@]}" --locked-mode

    assets_file="$(
        dotnet msbuild "$consumer_project" \
            -getProperty:ProjectAssetsFile \
            "${msbuild_properties[@]}"
    )"
    if [[ ! -f "$assets_file" ]]; then
        echo "$consumer_name $tooling_reference assets file is missing: $assets_file" >&2
        exit 1
    fi

    if grep -Fq '"type": "project"' "$assets_file"; then
        echo "$consumer_name package consumer unexpectedly resolved a ProjectReference." >&2
        exit 1
    fi

    grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations/' "$assets_file"

    case "$tooling_reference" in
        Design | DesignWithoutSafeBuildAssets)
            grep -Fq 'Microsoft.EntityFrameworkCore.Design/' "$assets_file"
            if grep -Fq 'Microsoft.EntityFrameworkCore.Tools/' "$assets_file"; then
                echo "$consumer_name direct-Design consumer unexpectedly resolved EF Tools." >&2
                exit 1
            fi
            ;;
        Tools)
            grep -Fq 'Microsoft.EntityFrameworkCore.Design/' "$assets_file"
            grep -Fq 'Microsoft.EntityFrameworkCore.Tools/' "$assets_file"
            ;;
        None)
            if grep -Fq 'Microsoft.EntityFrameworkCore.Design/' "$assets_file" \
                || grep -Fq 'Microsoft.EntityFrameworkCore.Tools/' "$assets_file"; then
                echo "$consumer_name runtime-only consumer resolved EF design-time assets." >&2
                exit 1
            fi
            ;;
    esac

    case "$consumer_name" in
        MySql)
            grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.MySql/' "$assets_file"
            grep -Fq 'Doka.EntityFrameworkCore.MySql/' "$assets_file"

            if grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/' "$assets_file" \
                || grep -Fq 'Npgsql.EntityFrameworkCore.PostgreSQL/' "$assets_file" \
                || grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.Sqlite/' "$assets_file" \
                || grep -Fq 'Microsoft.EntityFrameworkCore.Sqlite/' "$assets_file" \
                || grep -Fq 'Microsoft.EntityFrameworkCore.Sqlite.Core/' "$assets_file"; then
                echo "MySQL/MariaDB consumer resolved PostgreSQL or SQLite assets." >&2
                exit 1
            fi
            ;;
        PostgreSql)
            grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/' "$assets_file"
            grep -Fq 'Npgsql.EntityFrameworkCore.PostgreSQL/' "$assets_file"

            if grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.MySql/' "$assets_file" \
                || grep -Fq 'Doka.EntityFrameworkCore.MySql/' "$assets_file" \
                || grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.Sqlite/' "$assets_file" \
                || grep -Fq 'Microsoft.EntityFrameworkCore.Sqlite/' "$assets_file" \
                || grep -Fq 'Microsoft.EntityFrameworkCore.Sqlite.Core/' "$assets_file"; then
                echo "PostgreSQL consumer resolved MySQL/MariaDB or SQLite assets." >&2
                exit 1
            fi
            ;;
        Sqlite)
            grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.Sqlite/' "$assets_file"
            grep -Fq 'Microsoft.EntityFrameworkCore.Sqlite/' "$assets_file"
            grep -Fq 'Microsoft.EntityFrameworkCore.Sqlite.Core/' "$assets_file"

            if grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.MySql/' "$assets_file" \
                || grep -Fq 'Doka.EntityFrameworkCore.MySql/' "$assets_file" \
                || grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/' "$assets_file" \
                || grep -Fq 'Npgsql.EntityFrameworkCore.PostgreSQL/' "$assets_file"; then
                echo "SQLite consumer resolved MySQL/MariaDB or PostgreSQL assets." >&2
                exit 1
            fi
            ;;
        *)
            echo "Unknown package consumer: $consumer_name" >&2
            exit 1
            ;;
    esac

    dotnet build "$consumer_project" \
        --configuration Release \
        --no-restore \
        --disable-build-servers \
        "${msbuild_properties[@]}"

    if [[ "$expects_design_reference" == true ]]; then
        dotnet run \
            --project "$consumer_project" \
            --configuration Release \
            --no-build \
            --no-restore \
            "${msbuild_properties[@]}" \
            -- \
            --expect-design-reference
    else
        dotnet run \
            --project "$consumer_project" \
            --configuration Release \
            --no-build \
            --no-restore \
            "${msbuild_properties[@]}"
    fi

    local migration_name="Package${tooling_reference}ScaffoldingProbe"
    local scaffolding_dir="$consumer_dir/ScaffoldingProbe"

    if [[ "$scaffolding_expectation" == EfRejects ]]; then
        local failure_output
        if failure_output="$(
            cd "$work_dir"
            SafeMigrationsPackageConsumerMode=Package \
            SafeMigrationsEfToolingReference="$tooling_reference" \
            dotnet tool run dotnet-ef -- \
                migrations add "$migration_name" \
                --project "$consumer_project" \
                --context PackageScaffoldingDbContext \
                --output-dir ScaffoldingProbe \
                --configuration Release \
                --no-build 2>&1
        )"; then
            echo "$consumer_name runtime-only consumer unexpectedly scaffolded a migration." >&2
            exit 1
        fi

        printf '%s\n' "$failure_output"
        grep -Fq "doesn't reference Microsoft.EntityFrameworkCore.Design" <<<"$failure_output"

        if [[ -d "$scaffolding_dir" \
            && -n "$(find "$scaffolding_dir" -type f -name '*.cs' -print -quit)" ]]; then
            echo "$consumer_name runtime-only consumer left migration source after EF rejected it." >&2
            exit 1
        fi

        local invalid_reference_output
        if invalid_reference_output="$(
            dotnet build \
                "$consumer_project" \
                --configuration Release \
                --no-restore \
                --disable-build-servers \
                -p:SafeMigrationsPackageVersion="$package_version" \
                -p:EfCorePackageVersion="$ef_core_version" \
                -p:SafeMigrationsPackageConsumerMode=Package \
                -p:SafeMigrationsEfToolingReference=Invalid 2>&1
        )"; then
            echo "$consumer_name consumer accepted an invalid EF tooling reference." >&2
            exit 1
        fi

        printf '%s\n' "$invalid_reference_output"
        grep -Fq \
            'SafeMigrationsEfToolingReference must be Design, DesignWithoutSafeBuildAssets, Tools, or None.' \
            <<<"$invalid_reference_output"

        local invalid_mode_output
        if invalid_mode_output="$(
            dotnet build \
                "$consumer_project" \
                --configuration Release \
                --no-restore \
                --disable-build-servers \
                -p:SafeMigrationsPackageVersion="$package_version" \
                -p:EfCorePackageVersion="$ef_core_version" \
                -p:SafeMigrationsPackageConsumerMode=Invalid \
                -p:SafeMigrationsEfToolingReference=None 2>&1
        )"; then
            echo "$consumer_name consumer accepted an invalid consumer mode." >&2
            exit 1
        fi

        printf '%s\n' "$invalid_mode_output"
        grep -Fq \
            'SafeMigrationsPackageConsumerMode must be Source or Package.' \
            <<<"$invalid_mode_output"

        return 0
    fi

    if [[ "$scaffolding_expectation" == GuardRejects ]]; then
        local failure_output
        if failure_output="$(
            cd "$work_dir"
            SafeMigrationsPackageConsumerMode=Package \
            SafeMigrationsEfToolingReference="$tooling_reference" \
            dotnet tool run dotnet-ef -- \
                migrations add "$migration_name" \
                --project "$consumer_project" \
                --context PackageScaffoldingDbContext \
                --output-dir ScaffoldingProbe \
                --configuration Release \
                --no-build 2>&1
        )"; then
            echo "$consumer_name consumer without SafeMigrations build assets unexpectedly scaffolded a migration." >&2
            exit 1
        fi

        printf '%s\n' "$failure_output"
        grep -Fq 'SafeMigrationDesignTimeServicesRequiredOperation' <<<"$failure_output"

        if [[ -d "$scaffolding_dir" \
            && -n "$(find "$scaffolding_dir" -type f -name '*.cs' -print -quit)" ]]; then
            echo "$consumer_name design-time guard failure left migration source behind." >&2
            exit 1
        fi

        return 0
    fi

    (
        cd "$work_dir"
        SafeMigrationsPackageConsumerMode=Package \
        SafeMigrationsEfToolingReference="$tooling_reference" \
        dotnet tool run dotnet-ef -- \
            migrations add "$migration_name" \
            --project "$consumer_project" \
            --context PackageScaffoldingDbContext \
            --output-dir ScaffoldingProbe \
            --configuration Release \
            --no-build
    )

    local migration_file
    migration_file="$(find "$scaffolding_dir" \
        -type f -name "*_${migration_name}.cs" -print -quit)"
    if [[ -z "$migration_file" ]]; then
        echo "$consumer_name $tooling_reference consumer did not scaffold a migration." >&2
        exit 1
    fi

    case "$consumer_name" in
        MySql)
            grep -Fq 'migrationBuilder.ConvergeTableFromModel(' "$migration_file"
            grep -Fq \
                'policy: global::Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationPolicy.RepairIfSafe' \
                "$migration_file"
            grep -Fq 'IsActive = table.Column<bool>' "$migration_file"
            grep -Fq 'columns: ["Id", "IsActive", "Name"]' "$migration_file"
            grep -Fq 'throw new global::System.NotSupportedException(' "$migration_file"

            if grep -Fq 'migrationBuilder.DropTableIfExists(' "$migration_file"; then
                echo "$consumer_name $tooling_reference consumer scaffolded an unsafe LegacyConvergence down migration." >&2
                exit 1
            fi
            ;;
        PostgreSql)
            grep -Fq 'migrationBuilder.CreateTableIfNotExists(' "$migration_file"
            grep -Fq 'migrationBuilder.DropTableIfExists(' "$migration_file"
            ;;
        Sqlite)
            grep -Fq 'migrationBuilder.CreateTableIfNotExists(' "$migration_file"
            grep -Fq 'migrationBuilder.DropTableIfExists(' "$migration_file"
            ;;
        *)
            echo "Unknown package consumer: $consumer_name" >&2
            exit 1
            ;;
    esac

    grep -Fq 'migrationBuilder.EnsureModelManagedDataFromModel(' "$migration_file"
    grep -Fq 'using Doka.EntityFrameworkCore.SafeMigrations;' "$migration_file"
    grep -Eq '^namespace .+;$' "$migration_file"

    if grep -Fq 'migrationBuilder.CreateTable(' "$migration_file" \
        || grep -Fq 'migrationBuilder.InsertData(' "$migration_file" \
        || grep -Fq 'migrationBuilder.UpdateData(' "$migration_file" \
        || grep -Fq 'migrationBuilder.DeleteData(' "$migration_file" \
        || grep -Fq 'new[] {' "$migration_file"; then
        echo "$consumer_name $tooling_reference consumer scaffolded analyzer-incompatible or unsafe source." >&2
        exit 1
    fi

    dotnet build "$consumer_project" \
        --configuration Release \
        --no-restore \
        --disable-build-servers \
        "${msbuild_properties[@]}"
}

verify_incremental_design_registration() {
    local consumer_name="$1"
    local tooling_reference="$2"
    local consumer_dir="$work_dir/eng/package-consumer/$consumer_name-Incremental-$tooling_reference"
    local source_project
    source_project="$(package_consumer_project_name "$consumer_name")"
    local consumer_project="$consumer_dir/$source_project"
    local artifacts_project_name="$consumer_name.Incremental.$tooling_reference"
    local scaffolding_dir="$consumer_dir/IncrementalScaffoldingProbe"
    local migration_name="Incremental${tooling_reference}ScaffoldingProbe"
    local -a transition_references=(None "$tooling_reference" None)
    local -a expected_reference_counts=(0 1 0)

    mkdir -p "$consumer_dir"
    cp "$script_dir/package-consumer/$consumer_name/$source_project" "$consumer_project"
    cp "$script_dir/package-consumer/$consumer_name/Imports.cs" "$consumer_dir/"
    cp "$script_dir/package-consumer/$consumer_name/Program.cs" "$consumer_dir/"

    local transition_index
    for transition_index in "${!transition_references[@]}"; do
        local current_reference="${transition_references[$transition_index]}"
        local expected_count="${expected_reference_counts[$transition_index]}"
        local generated_assembly_info
        local -a msbuild_properties=(
            -p:SafeMigrationsPackageVersion="$package_version"
            -p:EfCorePackageVersion="$ef_core_version"
            -p:SafeMigrationsPackageConsumerMode=Package
            -p:SafeMigrationsEfToolingReference="$current_reference"
            -p:ArtifactsProjectName="$artifacts_project_name"
        )
        local -a restore_args=(
            "$consumer_project"
            --packages "$work_dir/packages"
            --source "$package_dir"
            --source "$doka_source"
            --source "https://api.nuget.org/v3/index.json"
            --use-lock-file
            --force-evaluate
            --disable-parallel
            "${msbuild_properties[@]}"
        )

        dotnet restore "${restore_args[@]}"
        dotnet build "$consumer_project" \
            --configuration Release \
            --no-restore \
            --disable-build-servers \
            "${msbuild_properties[@]}"

        generated_assembly_info="$(
            dotnet msbuild "$consumer_project" \
                -p:Configuration=Release \
                -getProperty:GeneratedAssemblyInfoFile \
                "${msbuild_properties[@]}"
        )"

        assert_design_reference_count "$generated_assembly_info" "$expected_count"

        if [[ "$expected_count" -eq 1 ]]; then
            dotnet run \
                --project "$consumer_project" \
                --configuration Release \
                --no-build \
                --no-restore \
                "${msbuild_properties[@]}" \
                -- \
                --expect-design-reference
        else
            dotnet run \
                --project "$consumer_project" \
                --configuration Release \
                --no-build \
                --no-restore \
                "${msbuild_properties[@]}"
        fi

        if [[ "$transition_index" -eq 1 ]]; then
            # WHY: The same output and intermediate paths reproduce the consumer transition that clean builds miss.
            (
                cd "$work_dir"
                ArtifactsProjectName="$artifacts_project_name" \
                SafeMigrationsPackageConsumerMode=Package \
                SafeMigrationsEfToolingReference="$current_reference" \
                SafeMigrationsPackageVersion="$package_version" \
                EfCorePackageVersion="$ef_core_version" \
                dotnet tool run dotnet-ef -- \
                    migrations add "$migration_name" \
                    --project "$consumer_project" \
                    --context PackageScaffoldingDbContext \
                    --output-dir IncrementalScaffoldingProbe \
                    --configuration Release \
                    --no-build
            )

            local migration_file
            migration_file="$(find "$scaffolding_dir" -type f -name "*_${migration_name}.cs" -print -quit)"
            if [[ -z "$migration_file" ]]; then
                echo "$consumer_name $tooling_reference transition did not scaffold a migration." >&2
                exit 1
            fi

            assert_safe_scaffolding_source "$consumer_name" "$migration_file"
        fi
    done
}

verify_split_mysql_consumer() {
    local split_root="$work_dir/eng/package-consumer/MySqlSplit"
    local target_project="$split_root/MigrationsTarget/Doka.EntityFrameworkCore.SafeMigrations.MySql.SplitTarget.csproj"
    local startup_project="$split_root/Generator/Doka.EntityFrameworkCore.SafeMigrations.MySql.SplitGenerator.csproj"
    local migration_name="SplitPackageScaffoldingProbe"
    local migration_file
    local -a msbuild_properties
    local -a restore_args

    cp -R "$script_dir/package-consumer/MySqlSplit" \
        "$work_dir/eng/package-consumer/MySqlSplit"

    msbuild_properties=(
        -p:SafeMigrationsPackageVersion="$package_version"
        -p:EfCorePackageVersion="$ef_core_version"
        -p:SafeMigrationsPackageConsumerMode=Package
        -p:SafeMigrationsSplitBuildAssets=Enabled
    )

    restore_args=(
        "$startup_project"
        --packages "$work_dir/packages"
        --source "$package_dir"
        --source "$doka_source"
        --source "https://api.nuget.org/v3/index.json"
        --use-lock-file
        --disable-parallel
        "${msbuild_properties[@]}"
    )

    dotnet restore "${restore_args[@]}"
    dotnet restore "${restore_args[@]}" --locked-mode
    dotnet build "$startup_project" \
        --configuration Release \
        --no-restore \
        --disable-build-servers \
        "${msbuild_properties[@]}"

    (
        cd "$work_dir"
        SafeMigrationsPackageConsumerMode=Package \
        SafeMigrationsSplitBuildAssets=Enabled \
        SafeMigrationsPackageVersion="$package_version" \
        EfCorePackageVersion="$ef_core_version" \
        dotnet tool run dotnet-ef -- \
            migrations add "$migration_name" \
            --project "$target_project" \
            --startup-project "$startup_project" \
            --context SplitPackageDbContext \
            --output-dir ScaffoldingProbe \
            --configuration Release \
            --no-build
    )

    migration_file="$(find "$split_root/MigrationsTarget/ScaffoldingProbe" \
        -type f -name "*_${migration_name}.cs" -print -quit)"
    if [[ -z "$migration_file" ]]; then
        echo "The split MySQL/MariaDB consumer did not scaffold a migration." >&2
        exit 1
    fi

    grep -Fq 'migrationBuilder.ConvergeTableFromModel(' "$migration_file"
    grep -Fq 'using Doka.EntityFrameworkCore.SafeMigrations;' "$migration_file"

    if grep -Fq 'migrationBuilder.CreateTable(' "$migration_file"; then
        echo "The split MySQL/MariaDB consumer bypassed SafeMigrations scaffolding." >&2
        exit 1
    fi

    dotnet build "$startup_project" \
        --configuration Release \
        --no-restore \
        --disable-build-servers \
        "${msbuild_properties[@]}"
}

for consumer_name in MySql PostgreSql Sqlite; do
    verify_consumer "$consumer_name" Design
    verify_consumer "$consumer_name" Tools
    verify_consumer "$consumer_name" DesignWithoutSafeBuildAssets
    verify_consumer "$consumer_name" None

    verify_incremental_design_registration "$consumer_name" Design
    verify_incremental_design_registration "$consumer_name" Tools
done

verify_split_mysql_consumer
