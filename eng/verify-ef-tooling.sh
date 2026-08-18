#!/usr/bin/env bash
set -euo pipefail

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
engine="${1:?Usage: verify-ef-tooling.sh <mysql|mariadb|postgres> <image> <version>}"
image="${2:?Usage: verify-ef-tooling.sh <mysql|mariadb|postgres> <image> <version>}"
version="${3:?Usage: verify-ef-tooling.sh <mysql|mariadb|postgres> <image> <version>}"

case "${engine}" in
  mysql|mariadb|postgres) ;;
  *)
    echo "Unsupported engine: ${engine}" >&2
    exit 2
    ;;
esac

temporary_root="$(cd "${TMPDIR:-/tmp}" && pwd -P)"
work_dir="$(mktemp -d "$temporary_root/safemigrations-tooling.XXXXXX")"
case "$work_dir" in
  "$temporary_root"/safemigrations-tooling.*) ;;
  *)
    echo "Unexpected temporary directory: $work_dir" >&2
    exit 1
    ;;
esac
repository_root="${work_dir}/source"
mkdir -p "${repository_root}"

hash_source_lockfiles() {
  local output_file="$1"
  find "${source_root}" \
    \( -name .git -o -name .fastembed_cache -o -name artifacts -o -name bin -o -name obj \) -prune -o \
    -type f -name packages.lock.json -exec shasum -a 256 {} \; \
    | LC_ALL=C sort >"${output_file}"
}

hash_source_lockfiles "${work_dir}/source-lockfiles.before"
rsync -a \
  --exclude '.fastembed_cache/' \
  --exclude '.git/' \
  --exclude 'artifacts/' \
  --exclude 'bin/' \
  --exclude 'obj/' \
  "${source_root}/" "${repository_root}/"

container_name="safe-migrations-tooling-${engine}-${RANDOM}-$$"
artifacts_dir="${source_root}/artifacts/ef-tooling/${engine}"
mkdir -p "${artifacts_dir}"

cleanup() {
  if [[ "${container_name}" == safe-migrations-tooling-* ]]; then
    docker rm -f "${container_name}" >/dev/null 2>&1 || true
  fi
  if [[ "${work_dir}" == "$temporary_root"/safemigrations-tooling.* ]]; then
    rm -rf -- "${work_dir}"
  fi
}
trap cleanup EXIT

wait_for_mysql() {
  local admin_client="$1"
  for _ in {1..90}; do
    if docker exec "${container_name}" "${admin_client}" ping \
      -h127.0.0.1 -uroot -prootpw --silent >/dev/null 2>&1; then
      return
    fi
    sleep 1
  done
  echo "The ${engine} container did not become ready." >&2
  exit 1
}

if [[ "${engine}" == "postgres" ]]; then
  docker run -d --name "${container_name}" \
    -e POSTGRES_PASSWORD=postgrespw \
    -e POSTGRES_DB=bootstrap \
    -p 0:5432 "${image}" >/dev/null
  for _ in {1..90}; do
    if docker exec "${container_name}" pg_isready -U postgres -d bootstrap >/dev/null 2>&1; then
      break
    fi
    sleep 1
  done
  docker exec "${container_name}" createdb -U postgres tooling_cli
  docker exec "${container_name}" createdb -U postgres tooling_bundle
  port="$(docker port "${container_name}" 5432/tcp | head -n 1 | awk -F: '{print $NF}')"
  project="tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj"
  cli_connection="Host=127.0.0.1;Port=${port};Username=postgres;Password=postgrespw;Database=tooling_cli"
  bundle_connection="Host=127.0.0.1;Port=${port};Username=postgres;Password=postgrespw;Database=tooling_bundle"
else
  if [[ "${engine}" == "mariadb" ]]; then
    database_variable="MARIADB_DATABASE"
    password_variable="MARIADB_ROOT_PASSWORD"
    client="mariadb"
    admin_client="mariadb-admin"
  else
    database_variable="MYSQL_DATABASE"
    password_variable="MYSQL_ROOT_PASSWORD"
    client="mysql"
    admin_client="mysqladmin"
  fi
  docker run -d --name "${container_name}" \
    -e "${password_variable}=rootpw" \
    -e "${database_variable}=bootstrap" \
    -p 0:3306 "${image}" >/dev/null
  wait_for_mysql "${admin_client}"
  docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw \
    -e "CREATE DATABASE tooling_cli; CREATE DATABASE tooling_bundle;"
  port="$(docker port "${container_name}" 3306/tcp | head -n 1 | awk -F: '{print $NF}')"
  project="tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj"
  cli_connection="Server=127.0.0.1;Port=${port};User ID=root;Password=rootpw;Database=tooling_cli;Allow User Variables=true"
  bundle_connection="Server=127.0.0.1;Port=${port};User ID=root;Password=rootpw;Database=tooling_bundle;Allow User Variables=true"
  export SAFE_MIGRATIONS_MYSQL_ENGINE="${engine}"
  export SAFE_MIGRATIONS_MYSQL_VERSION="${version}"
fi

cd "${repository_root}"
dotnet restore "${project}" \
  --locked-mode --disable-parallel --disable-build-servers -m:1 /nodeReuse:false
dotnet build "${project}" \
  --configuration Release --no-restore --disable-build-servers -m:1 /nodeReuse:false
dotnet tool restore --tool-manifest "${repository_root}/.config/dotnet-tools.json" \
  --disable-parallel
export SAFE_MIGRATIONS_CONNECTION_STRING="${cli_connection}"
dotnet ef database update --project "${project}" --context SafeMigrationDbContext \
  --configuration Release --no-build
dotnet ef database update --project "${project}" --context SafeMigrationDbContext \
  --configuration Release --no-build
dotnet ef migrations script --project "${project}" --context SafeMigrationDbContext --no-build \
  --configuration Release --output "${artifacts_dir}/migration.sql"
dotnet ef migrations script --project "${project}" --context SafeMigrationDbContext --no-build \
  --configuration Release --idempotent --output "${artifacts_dir}/migration-idempotent.sql"
dotnet ef migrations script --project "${project}" --context SafeMigrationDbContext --no-build \
  --configuration Release --idempotent --no-transactions \
  --output "${artifacts_dir}/migration-idempotent-no-transactions.sql"
dotnet ef migrations bundle --project "${project}" --context SafeMigrationDbContext \
  --configuration Release --output "${artifacts_dir}/efbundle" --force
"${artifacts_dir}/efbundle" --connection "${bundle_connection}"
"${artifacts_dir}/efbundle" --connection "${bundle_connection}"

if [[ "${engine}" == "postgres" ]]; then
  cli_count="$(docker exec "${container_name}" psql -U postgres -d tooling_cli -Atc \
    'SELECT COUNT(*) FROM "__CoreDbContextMigrationsHistory" WHERE "MigrationId" = '\''202608170001_CoreConvergence'\'';')"
  bundle_count="$(docker exec "${container_name}" psql -U postgres -d tooling_bundle -Atc \
    'SELECT COUNT(*) FROM "__CoreDbContextMigrationsHistory" WHERE "MigrationId" = '\''202608170001_CoreConvergence'\'';')"
else
  cli_count="$(docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B tooling_cli \
    -e "SELECT COUNT(*) FROM \`__CoreDbContextMigrationsHistory\` WHERE \`MigrationId\` = '202608170001_CoreConvergence';")"
  bundle_count="$(docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B tooling_bundle \
    -e "SELECT COUNT(*) FROM \`__CoreDbContextMigrationsHistory\` WHERE \`MigrationId\` = '202608170001_CoreConvergence';")"
fi

if [[ "${cli_count}" != "1" || "${bundle_count}" != "1" ]]; then
  echo "EF tooling history verification failed for ${engine}." >&2
  exit 1
fi

for artifact in \
  "${artifacts_dir}/migration.sql" \
  "${artifacts_dir}/migration-idempotent.sql" \
  "${artifacts_dir}/migration-idempotent-no-transactions.sql" \
  "${artifacts_dir}/efbundle"; do
  if [[ ! -s "${artifact}" ]]; then
    echo "Expected tooling artifact is missing or empty: ${artifact}" >&2
    exit 1
  fi
done

hash_source_lockfiles "${work_dir}/source-lockfiles.after"
if ! diff -u "${work_dir}/source-lockfiles.before" "${work_dir}/source-lockfiles.after"; then
  echo "EF tooling verification modified source package lock files." >&2
  exit 1
fi

echo "EF tooling verification passed for ${engine} ${version}."
