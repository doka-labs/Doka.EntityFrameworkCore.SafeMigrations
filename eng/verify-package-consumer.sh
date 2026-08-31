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
    Doka.EntityFrameworkCore.SafeMigrations.PostgreSql; do
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

mkdir -p "$work_dir/.config" "$work_dir/eng/package-consumer"
cp "$script_dir/../.config/dotnet-tools.json" "$work_dir/.config/dotnet-tools.json"
cp "$script_dir/../.editorconfig" "$work_dir/.editorconfig"
cp "$script_dir/../Directory.Build.props" "$work_dir/Directory.Build.props"
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
    local source_project
    local -a msbuild_properties
    local -a restore_args

    case "$tooling_reference" in
        Design | Tools)
            expects_design_reference=true
            ;;
        None)
            expects_design_reference=false
            ;;
        *)
            echo "Unknown EF tooling reference: $tooling_reference" >&2
            exit 1
            ;;
    esac

    case "$consumer_name" in
        MySql)
            source_project="Doka.EntityFrameworkCore.SafeMigrations.MySql.PackageConsumer.csproj"
            ;;
        PostgreSql)
            source_project="Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.PackageConsumer.csproj"
            ;;
        *)
            echo "Unknown package consumer: $consumer_name" >&2
            exit 1
            ;;
    esac

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
        Design)
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
                || grep -Fq 'Npgsql.EntityFrameworkCore.PostgreSQL/' "$assets_file"; then
                echo "MySQL/MariaDB consumer resolved PostgreSQL assets." >&2
                exit 1
            fi
            ;;
        PostgreSql)
            grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/' "$assets_file"
            grep -Fq 'Npgsql.EntityFrameworkCore.PostgreSQL/' "$assets_file"

            if grep -Fq 'Doka.EntityFrameworkCore.SafeMigrations.MySql/' "$assets_file" \
                || grep -Fq 'Doka.EntityFrameworkCore.MySql/' "$assets_file"; then
                echo "PostgreSQL consumer resolved MySQL/MariaDB assets." >&2
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

    if [[ "$expects_design_reference" == false ]]; then
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
            'SafeMigrationsEfToolingReference must be Design, Tools, or None.' \
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

    grep -Fq 'migrationBuilder.CreateTableIfNotExists(' "$migration_file"
    grep -Fq 'migrationBuilder.DropTableIfExists(' "$migration_file"
    grep -Fq 'using Doka.EntityFrameworkCore.SafeMigrations;' "$migration_file"
    grep -Eq '^namespace .+;$' "$migration_file"

    if grep -Fq 'migrationBuilder.CreateTable(' "$migration_file" \
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

for consumer_name in MySql PostgreSql; do
    verify_consumer "$consumer_name" Design
    verify_consumer "$consumer_name" Tools
    verify_consumer "$consumer_name" None
done
