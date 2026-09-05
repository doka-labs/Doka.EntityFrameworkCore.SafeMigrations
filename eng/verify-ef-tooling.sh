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
container_name=""

cleanup() {
  if [[ "${container_name}" == safe-migrations-tooling-* ]]; then
    docker rm -f "${container_name}" >/dev/null 2>&1 || true
  fi
  if [[ "${work_dir}" == "$temporary_root"/safemigrations-tooling.* ]]; then
    rm -rf -- "${work_dir}"
  fi
}
trap cleanup EXIT

repository_root="${work_dir}/source"
mkdir -p "${repository_root}"

hash_source_lockfiles() {
  local output_file="$1"
  find "${source_root}" \
    \( -name .git -o -name .fastembed_cache -o -name artifacts -o -name bin -o -name obj \) -prune -o \
    -type f -name packages.lock.json -exec shasum -a 256 {} + \
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
  postgres_ready=false
  for _ in {1..90}; do
    if docker exec "${container_name}" pg_isready \
      -h 127.0.0.1 -p 5432 -U postgres -d bootstrap -t 1 >/dev/null 2>&1; then
      postgres_ready=true
      break
    fi
    sleep 1
  done
  if [[ "${postgres_ready}" != "true" ]]; then
    echo "The postgres container did not become ready on TCP." >&2
    docker logs "${container_name}" >&2 || true
    exit 1
  fi
  docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    createdb -h 127.0.0.1 -p 5432 -U postgres tooling_cli
  docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    createdb -h 127.0.0.1 -p 5432 -U postgres tooling_bundle
  docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    createdb -h 127.0.0.1 -p 5432 -U postgres tooling_transition_strict
  docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    createdb -h 127.0.0.1 -p 5432 -U postgres tooling_transition_legacy
  docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    createdb -h 127.0.0.1 -p 5432 -U postgres tooling_generated_strict
  docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    createdb -h 127.0.0.1 -p 5432 -U postgres tooling_generated_legacy
  docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    createdb -h 127.0.0.1 -p 5432 -U postgres tooling_ownership
  port="$(docker port "${container_name}" 5432/tcp | head -n 1 | awk -F: '{print $NF}')"
  project="tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.csproj"
  cli_connection="Host=127.0.0.1;Port=${port};Username=postgres;Password=postgrespw;Database=tooling_cli"
  bundle_connection="Host=127.0.0.1;Port=${port};Username=postgres;Password=postgrespw;Database=tooling_bundle"
  strict_transition_connection="Host=127.0.0.1;Port=${port};Username=postgres;Password=postgrespw;Database=tooling_transition_strict"
  legacy_transition_connection="Host=127.0.0.1;Port=${port};Username=postgres;Password=postgrespw;Database=tooling_transition_legacy"
  generated_strict_connection="Host=127.0.0.1;Port=${port};Username=postgres;Password=postgrespw;Database=tooling_generated_strict"
  generated_legacy_connection="Host=127.0.0.1;Port=${port};Username=postgres;Password=postgrespw;Database=tooling_generated_legacy"
  ownership_connection="Host=127.0.0.1;Port=${port};Username=postgres;Password=postgrespw;Database=tooling_ownership"
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
    -e "CREATE DATABASE tooling_cli; CREATE DATABASE tooling_bundle; CREATE DATABASE tooling_transition_strict; CREATE DATABASE tooling_transition_legacy;"
  docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw \
    -e "CREATE DATABASE tooling_generated_strict; CREATE DATABASE tooling_generated_legacy;"
  docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw \
    -e "CREATE DATABASE tooling_ownership;"
  port="$(docker port "${container_name}" 3306/tcp | head -n 1 | awk -F: '{print $NF}')"
  project="tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.csproj"
  cli_connection="Server=127.0.0.1;Port=${port};User ID=root;Password=rootpw;Database=tooling_cli;Allow User Variables=true"
  bundle_connection="Server=127.0.0.1;Port=${port};User ID=root;Password=rootpw;Database=tooling_bundle;Allow User Variables=true"
  strict_transition_connection="Server=127.0.0.1;Port=${port};User ID=root;Password=rootpw;Database=tooling_transition_strict;Allow User Variables=true"
  legacy_transition_connection="Server=127.0.0.1;Port=${port};User ID=root;Password=rootpw;Database=tooling_transition_legacy;Allow User Variables=true"
  generated_strict_connection="Server=127.0.0.1;Port=${port};User ID=root;Password=rootpw;Database=tooling_generated_strict;Allow User Variables=true"
  generated_legacy_connection="Server=127.0.0.1;Port=${port};User ID=root;Password=rootpw;Database=tooling_generated_legacy;Allow User Variables=true"
  ownership_connection="Server=127.0.0.1;Port=${port};User ID=root;Password=rootpw;Database=tooling_ownership;Allow User Variables=true"
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

project_directory="$(dirname "${project}")"
ownership_core_project="eng/ef-ownership/Core/Doka.EntityFrameworkCore.SafeMigrations.EfOwnership.Core.csproj"
ownership_custom_project="eng/ef-ownership/Custom/Doka.EntityFrameworkCore.SafeMigrations.EfOwnership.Custom.csproj"
ownership_core_directory="$(dirname "${ownership_core_project}")"
ownership_custom_directory="$(dirname "${ownership_custom_project}")"
ownership_output="Migrations/${engine}"
strict_output="ScaffoldingProbes/${engine}/Strict"
legacy_output="ScaffoldingProbes/${engine}/Legacy"
strict_transition_output="ScaffoldingProbes/${engine}/StrictDataTransition"
legacy_transition_output="ScaffoldingProbes/${engine}/LegacyDataTransition"

dotnet ef migrations add StrictScaffoldingProbe \
  --project "${project}" \
  --context StrictSafeMigrationScaffoldingDbContext \
  --output-dir "${strict_output}" \
  --configuration Release \
  --no-build
dotnet ef migrations add LegacyScaffoldingProbe \
  --project "${project}" \
  --context LegacySafeMigrationScaffoldingDbContext \
  --output-dir "${legacy_output}" \
  --configuration Release \
  --no-build

export SAFE_MIGRATIONS_MODEL_MANAGED_DATA_STATE="source"
dotnet ef migrations add StrictDataTransitionBaseline \
  --project "${project}" \
  --context StrictSafeMigrationDataTransitionScaffoldingDbContext \
  --output-dir "${strict_transition_output}" \
  --configuration Release \
  --no-build
dotnet ef migrations add LegacyDataTransitionBaseline \
  --project "${project}" \
  --context LegacySafeMigrationDataTransitionScaffoldingDbContext \
  --output-dir "${legacy_transition_output}" \
  --configuration Release \
  --no-build

export SAFE_MIGRATIONS_OWNERSHIP_STATE="source"
dotnet ef migrations add CoreOwnershipBaseline \
  --project "${ownership_core_project}" \
  --startup-project "${project}" \
  --context CoreOwnershipDbContext \
  --output-dir "${ownership_output}" \
  --configuration Release \
  --no-build
dotnet ef migrations add CustomOwnershipBaseline \
  --project "${ownership_custom_project}" \
  --startup-project "${project}" \
  --context CustomOwnershipDbContext \
  --output-dir "${ownership_output}" \
  --configuration Release \
  --no-build

# The second scaffold must load the generated baseline snapshot from the compiled
# migrations assembly. Reusing the pre-baseline assembly would compare the target
# model with an empty model and would not qualify UpdateData/DeleteData pairing.
dotnet build "${project}" \
  --configuration Release --no-restore --disable-build-servers -m:1 /nodeReuse:false

export SAFE_MIGRATIONS_MODEL_MANAGED_DATA_STATE="target"
dotnet ef migrations add StrictDataTransitionProbe \
  --project "${project}" \
  --context StrictSafeMigrationDataTransitionScaffoldingDbContext \
  --output-dir "${strict_transition_output}" \
  --configuration Release \
  --no-build
dotnet ef migrations add LegacyDataTransitionProbe \
  --project "${project}" \
  --context LegacySafeMigrationDataTransitionScaffoldingDbContext \
  --output-dir "${legacy_transition_output}" \
  --configuration Release \
  --no-build
unset SAFE_MIGRATIONS_MODEL_MANAGED_DATA_STATE

export SAFE_MIGRATIONS_OWNERSHIP_STATE="target"
dotnet ef migrations add CoreOwnershipTransition \
  --project "${ownership_core_project}" \
  --startup-project "${project}" \
  --context CoreOwnershipDbContext \
  --output-dir "${ownership_output}" \
  --configuration Release \
  --no-build
dotnet ef migrations add CustomOwnershipTransition \
  --project "${ownership_custom_project}" \
  --startup-project "${project}" \
  --context CustomOwnershipDbContext \
  --output-dir "${ownership_output}" \
  --configuration Release \
  --no-build
unset SAFE_MIGRATIONS_OWNERSHIP_STATE

strict_migration="$(find "${project_directory}/${strict_output}" -type f -name '*_StrictScaffoldingProbe.cs' -print -quit)"
legacy_migration="$(find "${project_directory}/${legacy_output}" -type f -name '*_LegacyScaffoldingProbe.cs' -print -quit)"
strict_snapshot="$(find "${project_directory}/${strict_output}" -type f -name '*ModelSnapshot.cs' -print -quit)"
legacy_snapshot="$(find "${project_directory}/${legacy_output}" -type f -name '*ModelSnapshot.cs' -print -quit)"
strict_transition_baseline="$(find "${project_directory}/${strict_transition_output}" -type f -name '*_StrictDataTransitionBaseline.cs' -print -quit)"
legacy_transition_baseline="$(find "${project_directory}/${legacy_transition_output}" -type f -name '*_LegacyDataTransitionBaseline.cs' -print -quit)"
strict_transition_migration="$(find "${project_directory}/${strict_transition_output}" -type f -name '*_StrictDataTransitionProbe.cs' -print -quit)"
legacy_transition_migration="$(find "${project_directory}/${legacy_transition_output}" -type f -name '*_LegacyDataTransitionProbe.cs' -print -quit)"
ownership_core_baseline="$(find "${ownership_core_directory}/${ownership_output}" -type f -name '*_CoreOwnershipBaseline.cs' -print -quit)"
ownership_core_transition="$(find "${ownership_core_directory}/${ownership_output}" -type f -name '*_CoreOwnershipTransition.cs' -print -quit)"
ownership_core_snapshot="$(find "${ownership_core_directory}/${ownership_output}" -type f -name '*ModelSnapshot.cs' -print -quit)"
ownership_custom_baseline="$(find "${ownership_custom_directory}/${ownership_output}" -type f -name '*_CustomOwnershipBaseline.cs' -print -quit)"
ownership_custom_transition="$(find "${ownership_custom_directory}/${ownership_output}" -type f -name '*_CustomOwnershipTransition.cs' -print -quit)"
ownership_custom_snapshot="$(find "${ownership_custom_directory}/${ownership_output}" -type f -name '*ModelSnapshot.cs' -print -quit)"

if [[ -z "${strict_migration}" \
  || -z "${legacy_migration}" \
  || -z "${strict_snapshot}" \
  || -z "${legacy_snapshot}" \
  || -z "${strict_transition_baseline}" \
  || -z "${legacy_transition_baseline}" \
  || -z "${strict_transition_migration}" \
  || -z "${legacy_transition_migration}" \
  || -z "${ownership_core_baseline}" \
  || -z "${ownership_core_transition}" \
  || -z "${ownership_core_snapshot}" \
  || -z "${ownership_custom_baseline}" \
  || -z "${ownership_custom_transition}" \
  || -z "${ownership_custom_snapshot}" ]]; then
  echo "EF tooling did not create every SafeMigrations scaffolding probe." >&2
  exit 1
fi

for migration in "${ownership_core_baseline}" "${ownership_core_transition}"; do
  if ! grep -Fq 'table: "ownership_core_roles"' "${migration}"; then
    echo "Core ownership migration is missing Core model-managed data: ${migration}" >&2
    exit 1
  fi

  if grep -Fq 'ownership_custom_profiles' "${migration}"; then
    echo "Core ownership migration contains custom-lineage operations: ${migration}" >&2
    exit 1
  fi
done

for migration in "${ownership_custom_baseline}" "${ownership_custom_transition}"; do
  if grep -Fq 'table: "ownership_core_roles"' "${migration}" \
    || grep -Fq 'name: "ownership_core_roles"' "${migration}"; then
    echo "Custom ownership migration contains Core schema or data operations: ${migration}" >&2
    exit 1
  fi

  if ! grep -Fq 'ownership_custom_profiles' "${migration}"; then
    echo "Custom ownership migration is missing its instance-owned operations: ${migration}" >&2
    exit 1
  fi
done

for expected in \
  'ownership_core_roles' \
  'ExcludeFromMigrations'; do
  if ! grep -Fq "${expected}" "${ownership_custom_snapshot}"; then
    echo "Custom ownership snapshot is missing inherited metadata: ${expected}" >&2
    exit 1
  fi
done

generated_ownership_files="$(find "${repository_root}" -type f \
  \( -name '*_CoreOwnershipBaseline.cs' \
    -o -name '*_CoreOwnershipTransition.cs' \
    -o -name '*_CustomOwnershipBaseline.cs' \
    -o -name '*_CustomOwnershipTransition.cs' \))"
while IFS= read -r generated_ownership_file; do
  case "${generated_ownership_file}" in
    "${repository_root}/${ownership_core_directory}/${ownership_output}/"* \
      | "${repository_root}/${ownership_custom_directory}/${ownership_output}/"*) ;;
    *)
      echo "Ownership migration was generated outside its target project: ${generated_ownership_file}" >&2
      exit 1
      ;;
  esac
done <<<"${generated_ownership_files}"

for expected in \
  'migrationBuilder.CreateTableIfNotExists(' \
  'migrationBuilder.CreateIndexIfNotExistsFromModel(' \
  'migrationBuilder.DropTableIfExists('; do
  if ! grep -Fq "${expected}" "${strict_migration}"; then
    echo "Strict scaffolding output is missing: ${expected}" >&2
    exit 1
  fi
done

for snapshot in "${strict_snapshot}" "${legacy_snapshot}"; do
  for numeric_discriminator_contract in \
    'HasDiscriminator<int>("Discriminator")' \
    '.IsComplete(false)' \
    '.HasValue(0)' \
    '.HasValue(1)'; do
    if ! grep -Fq "${numeric_discriminator_contract}" "${snapshot}"; then
      echo "Scaffolding snapshot lost numeric discriminator metadata: ${numeric_discriminator_contract}" >&2
      exit 1
    fi
  done

  for string_discriminator_contract in \
    'HasDiscriminator<string>("AttributedDiscriminator")' \
    '.HasValue("json-named")' \
    '.HasValue("contract-named")' \
    '.HasValue("Fallback")'; do
    if ! grep -Fq "${string_discriminator_contract}" "${snapshot}"; then
      echo "Scaffolding snapshot lost converted discriminator metadata: ${string_discriminator_contract}" >&2
      exit 1
    fi
  done
done

for migration in \
  "${strict_migration}" \
  "${legacy_migration}" \
  "${strict_transition_baseline}" \
  "${legacy_transition_baseline}"; do
  for ignored_member in \
    'RequestMetadata' \
    'RequestId' \
    'scaffolding_transition_requests'; do
    if grep -Fq "${ignored_member}" "${migration}"; then
      echo "Ignored model member leaked into scaffolding output: ${ignored_member}" >&2
      exit 1
    fi
  done
done

for migration in "${strict_transition_migration}" "${legacy_transition_migration}"; do
  for mapped_member in \
    'RequestMetadata' \
    'RequestId' \
    'scaffolding_transition_requests'; do
    if ! grep -Fq "${mapped_member}" "${migration}"; then
      echo "Mapped model member is missing from transition output: ${mapped_member}" >&2
      exit 1
    fi
  done

  for expected in \
    'migrationBuilder.EnsureModelManagedDataFromModel(' \
    'migrationBuilder.UpdateModelManagedDataFromModel(' \
    'migrationBuilder.DeleteModelManagedDataFromModel('; do
    if ! grep -Fq "${expected}" "${migration}"; then
      echo "Data-transition scaffolding output is missing: ${expected}" >&2
      exit 1
    fi
  done

  for unsafe_data_call in \
    'migrationBuilder.InsertData(' \
    'migrationBuilder.UpdateData(' \
    'migrationBuilder.DeleteData('; do
    if grep -Fq "${unsafe_data_call}" "${migration}"; then
      echo "Data-transition scaffolding output contains an unsafe call: ${unsafe_data_call}" >&2
      exit 1
    fi
  done

  if [[ "$(grep -Fc 'using Doka.EntityFrameworkCore.SafeMigrations;' "${migration}")" != "1" ]]; then
    echo "Data-transition scaffolding output must contain exactly one SafeMigrations import." >&2
    exit 1
  fi
done

if ! grep -Fq 'throw new global::System.NotSupportedException(' "${legacy_transition_migration}"; then
  echo "Legacy data-transition scaffolding output is missing its deterministic rollback rejection." >&2
  exit 1
fi

for expected in \
  'migrationBuilder.ConvergeTableFromModel(' \
  'policy: global::Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationPolicy.RepairIfSafe' \
  'migrationBuilder.CreateIndexIfNotExistsFromModel(' \
  'throw new global::System.NotSupportedException('; do
  if ! grep -Fq "${expected}" "${legacy_migration}"; then
    echo "Legacy scaffolding output is missing: ${expected}" >&2
    exit 1
  fi
done

if [[ "${engine}" == "postgres" ]]; then
  composite_index_call='migrationBuilder.CreateCompositeIndexIfNotExistsFromModel('
else
  composite_index_call='migrationBuilder.CreateCompositeIndexWithPrefixesIfNotExistsFromModel('
fi

for migration in "${strict_migration}" "${legacy_migration}"; do
  for hierarchy_contract in \
    'scaffolding_work_items' \
    'Discriminator' \
    'scaffolding_attributed_work_items' \
    'AttributedDiscriminator'; do
    if ! grep -Fq "${hierarchy_contract}" "${migration}"; then
      echo "Scaffolding output is missing its incomplete discriminator hierarchy: ${hierarchy_contract}" >&2
      exit 1
    fi
  done

  if ! grep -Fq "${composite_index_call}" "${migration}"; then
    echo "Scaffolding output is missing: ${composite_index_call}" >&2
    exit 1
  fi

  if ! grep -Fq 'migrationBuilder.EnsureModelManagedDataFromModel(' "${migration}"; then
    echo "Scaffolding output is missing safe model-managed data convergence." >&2
    exit 1
  fi

  for unsafe_data_call in \
    'migrationBuilder.InsertData(' \
    'migrationBuilder.UpdateData(' \
    'migrationBuilder.DeleteData('; do
    if grep -Fq "${unsafe_data_call}" "${migration}"; then
      echo "Scaffolding output contains an unsafe data call: ${unsafe_data_call}" >&2
      exit 1
    fi
  done
done

if grep -Fq 'migrationBuilder.CreateTable(' "${strict_migration}"; then
  echo "Strict scaffolding output contains an unsafe CreateTable call." >&2
  exit 1
fi

if grep -Fq 'migrationBuilder.DropTable' "${legacy_migration}"; then
  echo "Legacy scaffolding output contains a destructive rollback." >&2
  exit 1
fi

if [[ "${engine}" == "postgres" ]]; then
  identity_annotation='Npgsql:ValueGenerationStrategy'
  identity_strategy='NpgsqlValueGenerationStrategy.IdentityByDefaultColumn'
else
  identity_annotation='Doka:MySql:ValueGenerationStrategy'
  identity_strategy='MySqlValueGenerationStrategy.AutoIncrement'
fi

for migration in "${strict_migration}" "${legacy_migration}"; do
  if ! grep -Fq 'using Doka.EntityFrameworkCore.SafeMigrations;' "${migration}"; then
    echo "Scaffolding output is missing the SafeMigrations namespace import." >&2
    exit 1
  fi

  if ! grep -Eq '^namespace .+;$' "${migration}"; then
    echo "Scaffolding output does not use an analyzer-compatible file-scoped namespace." >&2
    exit 1
  fi

  if grep -Fq 'new[] {' "${migration}"; then
    echo "Scaffolding output contains an analyzer-incompatible constant array argument." >&2
    exit 1
  fi

  if grep -Fq 'object?[' "${migration}"; then
    echo "Scaffolding output contains nullable syntax inside EF's nullable-disabled migration source." >&2
    exit 1
  fi

  if ! grep -Fq "${identity_annotation}" "${migration}"; then
    echo "Scaffolding output is missing provider identity annotation: ${identity_annotation}" >&2
    exit 1
  fi

  if ! grep -Fq "${identity_strategy}" "${migration}"; then
    echo "Scaffolding output is missing provider identity strategy: ${identity_strategy}" >&2
    exit 1
  fi

  if [[ "${engine}" != "postgres" ]]; then
    if ! grep -Fq 'prefixLengths: [0, 64]' "${migration}"; then
      echo "MySQL scaffolding output is missing the projected index prefix lengths." >&2
      exit 1
    fi

    if grep -Fq 'Doka:MySql:IndexPrefixLength' "${migration}"; then
      echo "MySQL scaffolding output leaked consumed provider metadata onto the SafeMigrations operation." >&2
      exit 1
    fi
  fi
done

dotnet build "${project}" \
  --configuration Release --no-restore --disable-build-servers -m:1 /nodeReuse:false

export SAFE_MIGRATIONS_OWNERSHIP_STATE="target"
export SAFE_MIGRATIONS_CONNECTION_STRING="${ownership_connection}"
dotnet ef migrations has-pending-model-changes \
  --project "${ownership_core_project}" \
  --startup-project "${project}" \
  --context CoreOwnershipDbContext \
  --configuration Release \
  --no-build
dotnet ef migrations has-pending-model-changes \
  --project "${ownership_custom_project}" \
  --startup-project "${project}" \
  --context CustomOwnershipDbContext \
  --configuration Release \
  --no-build

dotnet ef database update \
  --project "${ownership_core_project}" \
  --startup-project "${project}" \
  --context CoreOwnershipDbContext \
  --configuration Release \
  --no-build
dotnet ef database update \
  --project "${ownership_custom_project}" \
  --startup-project "${project}" \
  --context CustomOwnershipDbContext \
  --configuration Release \
  --no-build

if [[ "${engine}" == "postgres" ]]; then
  ownership_core_state="$(docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    psql -h 127.0.0.1 -p 5432 -U postgres -d tooling_ownership -Atc \
    "SELECT COALESCE(string_agg(\"Id\"::text || ':' || \"Name\", ',' ORDER BY \"Id\"), '') FROM ownership_core_roles;")"
  ownership_custom_state="$(docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    psql -h 127.0.0.1 -p 5432 -U postgres -d tooling_ownership -Atc \
    "SELECT COALESCE(string_agg(\"Id\"::text || ':' || \"CoreRoleId\"::text || ':' || \"Name\", ',' ORDER BY \"Id\"), '') FROM ownership_custom_profiles;")"
  ownership_core_history="$(docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    psql -h 127.0.0.1 -p 5432 -U postgres -d tooling_ownership -Atc \
    'SELECT COUNT(*) FROM "__SafeMigrationsCoreHistory";')"
  ownership_custom_history="$(docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    psql -h 127.0.0.1 -p 5432 -U postgres -d tooling_ownership -Atc \
    'SELECT COUNT(*) FROM "__SafeMigrationsCustomHistory";')"
else
  ownership_core_state="$(docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B tooling_ownership \
    -e "SELECT COALESCE(GROUP_CONCAT(CONCAT(\`Id\`, ':', \`Name\`) ORDER BY \`Id\` SEPARATOR ','), '') FROM \`ownership_core_roles\`;")"
  ownership_custom_state="$(docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B tooling_ownership \
    -e "SELECT COALESCE(GROUP_CONCAT(CONCAT(\`Id\`, ':', \`CoreRoleId\`, ':', \`Name\`) ORDER BY \`Id\` SEPARATOR ','), '') FROM \`ownership_custom_profiles\`;")"
  ownership_core_history="$(docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B tooling_ownership \
    -e "SELECT COUNT(*) FROM \`__SafeMigrationsCoreHistory\`;")"
  ownership_custom_history="$(docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B tooling_ownership \
    -e "SELECT COUNT(*) FROM \`__SafeMigrationsCustomHistory\`;")"
fi

if [[ "${ownership_core_state}" != "1:owner,3:auditor" \
  || "${ownership_custom_state}" != "10:1:premium,12:3:audit" \
  || "${ownership_core_history}" != "2" \
  || "${ownership_custom_history}" != "2" ]]; then
  echo "EF ownership lineage verification failed for ${engine}." >&2
  exit 1
fi

dotnet ef database update 0 \
  --project "${ownership_custom_project}" \
  --startup-project "${project}" \
  --context CustomOwnershipDbContext \
  --configuration Release \
  --no-build

if [[ "${engine}" == "postgres" ]]; then
  ownership_core_after_rollback="$(docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    psql -h 127.0.0.1 -p 5432 -U postgres -d tooling_ownership -Atc \
    'SELECT COUNT(*) FROM ownership_core_roles;')"
  ownership_core_history_after_rollback="$(docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    psql -h 127.0.0.1 -p 5432 -U postgres -d tooling_ownership -Atc \
    'SELECT COUNT(*) FROM "__SafeMigrationsCoreHistory";')"
  ownership_custom_history_after_rollback="$(docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    psql -h 127.0.0.1 -p 5432 -U postgres -d tooling_ownership -Atc \
    'SELECT COUNT(*) FROM "__SafeMigrationsCustomHistory";')"
  ownership_custom_table_after_rollback="$(docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    psql -h 127.0.0.1 -p 5432 -U postgres -d tooling_ownership -Atc \
    "SELECT COUNT(*) FROM pg_catalog.pg_class c INNER JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND c.relname = 'ownership_custom_profiles' AND c.relkind = 'r';")"
else
  ownership_core_after_rollback="$(docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B tooling_ownership \
    -e "SELECT COUNT(*) FROM \`ownership_core_roles\`;")"
  ownership_core_history_after_rollback="$(docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B tooling_ownership \
    -e "SELECT COUNT(*) FROM \`__SafeMigrationsCoreHistory\`;")"
  ownership_custom_history_after_rollback="$(docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B tooling_ownership \
    -e "SELECT COUNT(*) FROM \`__SafeMigrationsCustomHistory\`;")"
  ownership_custom_table_after_rollback="$(docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B tooling_ownership \
    -e "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'ownership_custom_profiles';")"
fi

if [[ "${ownership_core_after_rollback}" != "2" \
  || "${ownership_core_history_after_rollback}" != "2" \
  || "${ownership_custom_history_after_rollback}" != "0" \
  || "${ownership_custom_table_after_rollback}" != "0" ]]; then
  echo "Custom ownership rollback changed the Core lineage for ${engine}." >&2
  exit 1
fi

dotnet ef database update \
  --project "${ownership_custom_project}" \
  --startup-project "${project}" \
  --context CustomOwnershipDbContext \
  --configuration Release \
  --no-build
dotnet ef migrations script \
  --project "${ownership_custom_project}" \
  --startup-project "${project}" \
  --context CustomOwnershipDbContext \
  --configuration Release \
  --no-build \
  --output "${artifacts_dir}/ownership-custom.sql"
unset SAFE_MIGRATIONS_OWNERSHIP_STATE

if [[ "${engine}" == "postgres" ]]; then
  core_sql_mutations=(
    'CREATE TABLE IF NOT EXISTS "ownership_core_roles"'
    'INSERT INTO "ownership_core_roles"'
    'UPDATE "ownership_core_roles"'
    'DELETE FROM "ownership_core_roles"'
  )
else
  core_sql_mutations=(
    'CREATE TABLE `ownership_core_roles`'
    'INSERT INTO `ownership_core_roles`'
    'UPDATE `ownership_core_roles`'
    'DELETE FROM `ownership_core_roles`'
  )
fi

for core_sql_mutation in "${core_sql_mutations[@]}"; do
  if grep -Fq "${core_sql_mutation}" "${artifacts_dir}/ownership-custom.sql"; then
    echo "Custom ownership SQL mutates the Core lineage for ${engine}: ${core_sql_mutation}" >&2
    exit 1
  fi
done

export SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING="${generated_strict_connection}"
export SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING="${generated_legacy_connection}"
dotnet test "${project}" \
  --configuration Release --no-build --no-restore \
  --filter FullyQualifiedName~GeneratedInitialMigrationPreflightTests
unset SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING
unset SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING

read_transition_state() {
  local database="$1"

  if [[ "${engine}" == "postgres" ]]; then
    docker exec -e PGPASSWORD=postgrespw "${container_name}" \
      psql -h 127.0.0.1 -p 5432 -U postgres -d "${database}" -Atc \
      "SELECT COALESCE(string_agg(\"Id\"::text || ':' || \"Email\", ',' ORDER BY \"Id\"), '') FROM scaffolding_transition_users;"
  else
    docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B "${database}" \
      -e "SELECT COALESCE(GROUP_CONCAT(CONCAT(\`id\`, ':', \`email\`) ORDER BY \`id\` SEPARATOR ','), '') FROM \`scaffolding_transition_users\`;"
  fi
}

assert_transition_state() {
  local database="$1"
  local expected="$2"
  local phase="$3"
  local actual

  actual="$(read_transition_state "${database}")"
  if [[ "${actual}" != "${expected}" ]]; then
    echo "EF tooling ${phase} state verification failed for ${engine}: ${actual}" >&2
    exit 1
  fi
}

read_transition_model_state() {
  local database="$1"

  if [[ "${engine}" == "postgres" ]]; then
    docker exec -e PGPASSWORD=postgrespw "${container_name}" \
      psql -h 127.0.0.1 -p 5432 -U postgres -d "${database}" -Atc \
      "SELECT format('%s:%s:%s:%s',
        to_regclass('public.scaffolding_transition_requests') IS NOT NULL,
        EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'scaffolding_transition_users' AND column_name = 'RequestMetadata'),
        EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'scaffolding_transition_users' AND column_name = 'RequestId'),
        EXISTS (
          SELECT 1 FROM pg_constraint c
          INNER JOIN pg_class dependent ON dependent.oid = c.conrelid
          INNER JOIN pg_class principal ON principal.oid = c.confrelid
          INNER JOIN pg_namespace n ON n.oid = dependent.relnamespace
          WHERE c.contype = 'f' AND n.nspname = 'public'
            AND dependent.relname = 'scaffolding_transition_users'
            AND principal.relname = 'scaffolding_transition_requests'));"
  else
    docker exec "${container_name}" "${client}" -h127.0.0.1 -uroot -prootpw -N -B "${database}" \
      -e "SELECT CONCAT(
        EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'scaffolding_transition_requests'), ':',
        EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'scaffolding_transition_users' AND column_name = 'RequestMetadata'), ':',
        EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = DATABASE() AND table_name = 'scaffolding_transition_users' AND column_name = 'RequestId'), ':',
        EXISTS (SELECT 1 FROM information_schema.key_column_usage WHERE constraint_schema = DATABASE() AND table_name = 'scaffolding_transition_users' AND referenced_table_name = 'scaffolding_transition_requests'));"
  fi
}

assert_transition_model_state() {
  local database="$1"
  local expected="$2"
  local phase="$3"
  local actual

  actual="$(read_transition_model_state "${database}")"
  if [[ "${actual}" != "${expected}" ]]; then
    echo "EF tooling ${phase} model verification failed for ${engine}: ${actual}" >&2
    exit 1
  fi
}

source_transition_state="1:administrator@example.test,2:member@example.test"
target_transition_state="1:owner@example.test,3:auditor@example.test"
if [[ "${engine}" == "postgres" ]]; then
  ignored_transition_model_state="f:f:f:f"
  mapped_transition_model_state="t:t:t:t"
else
  ignored_transition_model_state="0:0:0:0"
  mapped_transition_model_state="1:1:1:1"
fi

export SAFE_MIGRATIONS_MODEL_MANAGED_DATA_STATE="target"
export SAFE_MIGRATIONS_CONNECTION_STRING="${strict_transition_connection}"
dotnet ef database update --project "${project}" \
  --context StrictSafeMigrationDataTransitionScaffoldingDbContext \
  --configuration Release --no-build
assert_transition_state "tooling_transition_strict" "${target_transition_state}" "strict target"
assert_transition_model_state "tooling_transition_strict" "${mapped_transition_model_state}" "strict target"
dotnet ef database update StrictDataTransitionBaseline --project "${project}" \
  --context StrictSafeMigrationDataTransitionScaffoldingDbContext \
  --configuration Release --no-build
assert_transition_state "tooling_transition_strict" "${source_transition_state}" "strict rollback"
assert_transition_model_state "tooling_transition_strict" "${ignored_transition_model_state}" "strict rollback"
dotnet ef database update --project "${project}" \
  --context StrictSafeMigrationDataTransitionScaffoldingDbContext \
  --configuration Release --no-build
assert_transition_state "tooling_transition_strict" "${target_transition_state}" "strict replay"
assert_transition_model_state "tooling_transition_strict" "${mapped_transition_model_state}" "strict replay"
dotnet ef database update --project "${project}" \
  --context StrictSafeMigrationDataTransitionScaffoldingDbContext \
  --configuration Release --no-build
assert_transition_state "tooling_transition_strict" "${target_transition_state}" "strict idempotent replay"
assert_transition_model_state "tooling_transition_strict" "${mapped_transition_model_state}" "strict idempotent replay"

export SAFE_MIGRATIONS_CONNECTION_STRING="${legacy_transition_connection}"
dotnet ef database update --project "${project}" \
  --context LegacySafeMigrationDataTransitionScaffoldingDbContext \
  --configuration Release --no-build
assert_transition_state "tooling_transition_legacy" "${target_transition_state}" "legacy target"
assert_transition_model_state "tooling_transition_legacy" "${mapped_transition_model_state}" "legacy target"
dotnet ef database update --project "${project}" \
  --context LegacySafeMigrationDataTransitionScaffoldingDbContext \
  --configuration Release --no-build
assert_transition_state "tooling_transition_legacy" "${target_transition_state}" "legacy idempotent replay"
assert_transition_model_state "tooling_transition_legacy" "${mapped_transition_model_state}" "legacy idempotent replay"
unset SAFE_MIGRATIONS_MODEL_MANAGED_DATA_STATE

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
  cli_count="$(docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    psql -h 127.0.0.1 -p 5432 -U postgres -d tooling_cli -Atc \
    'SELECT COUNT(*) FROM "__CoreDbContextMigrationsHistory" WHERE "MigrationId" = '\''202608170001_CoreConvergence'\'';')"
  bundle_count="$(docker exec -e PGPASSWORD=postgrespw "${container_name}" \
    psql -h 127.0.0.1 -p 5432 -U postgres -d tooling_bundle -Atc \
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
  "${artifacts_dir}/ownership-custom.sql" \
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
