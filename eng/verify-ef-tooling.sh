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
  local original_status=$?
  local cleanup_status=0
  local status=0
  trap - EXIT

  if [[ "${container_name}" == safe-migrations-tooling-* ]]; then
    docker rm -f "${container_name}" >/dev/null || status=$?
    if [[ "${status}" != "0" ]]; then
      echo "Owned tooling container cleanup failed (${status})." >&2
      cleanup_status="${status}"
    fi
  fi

  if [[ "${work_dir}" == "$temporary_root"/safemigrations-tooling.* ]]; then
    status=0
    rm -rf -- "${work_dir}" || status=$?
    if [[ "${status}" != "0" ]]; then
      echo "Owned tooling directory cleanup failed (${status})." >&2
      cleanup_status="${status}"
    fi
  fi

  # WHY: Cleanup must attempt both owned resources, reveal its own failures and
  # retain the original migration/client error when the primary action failed.
  if [[ "${original_status}" != "0" ]]; then
    exit "${original_status}"
  fi

  exit "${cleanup_status}"
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
      --connect-timeout=2 -h127.0.0.1 -uroot -prootpw --silent >/dev/null 2>&1; then
      return
    fi
    sleep 1
  done
  echo "The ${engine} container did not become ready." >&2
  exit 1
}

run_query() {
  local database="$1"
  local query="$2"

  if [[ "${engine}" == "postgres" ]]; then
    docker exec -e PGPASSWORD=postgrespw -e PGCONNECT_TIMEOUT=10 "${container_name}" \
      psql -X -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 5432 -U postgres -d "${database}" -Atc "${query}"
  else
    docker exec "${container_name}" "${client}" --connect-timeout=10 --default-character-set=utf8mb4 \
      -h127.0.0.1 -uroot -prootpw -N -B "${database}" -e "${query}"
  fi
}

run_script() {
  local database="$1"
  local script="$2"
  if [[ ! -s "${script}" ]]; then
    echo "Expected native SQL artifact is missing or empty: ${script}" >&2
    exit 1
  fi

  # WHY: Native scripts retain EF's own transaction boundaries. psql otherwise
  # continues after SQL errors; MySQL/MariaDB must never receive --force here.
  if [[ "${engine}" == "postgres" ]]; then
    docker exec -i -e PGPASSWORD=postgrespw -e PGCONNECT_TIMEOUT=10 "${container_name}" \
      psql -X -v ON_ERROR_STOP=1 -h 127.0.0.1 -p 5432 -U postgres -d "${database}" \
      <"${script}"
  else
    docker exec -i "${container_name}" "${client}" --connect-timeout=10 --default-character-set=utf8mb4 \
      -h127.0.0.1 -uroot -prootpw --batch "${database}" <"${script}"
  fi
}

reset_owned_database() {
  local database="$1"
  case "${database}" in
    tooling_generated_strict|tooling_generated_legacy|tooling_ownership|tooling_script\
      |tooling_script_idempotent|tooling_script_no_transactions) ;;
    *)
      echo "Refusing to reset an unowned tooling database: ${database}" >&2
      exit 2
      ;;
  esac

  # WHY: MySQL's scaffolded default schema is the authored database identity.
  # Fresh lifetimes of that exact database qualify each transport independently
  # without silently rewriting captured migration/schema qualifiers.
  if [[ "${engine}" == "postgres" ]]; then
    docker exec -e PGPASSWORD=postgrespw -e PGCONNECT_TIMEOUT=10 "${container_name}" \
      dropdb -h 127.0.0.1 -p 5432 -U postgres --if-exists "${database}"
    docker exec -e PGPASSWORD=postgrespw -e PGCONNECT_TIMEOUT=10 "${container_name}" \
      createdb -h 127.0.0.1 -p 5432 -U postgres "${database}"
  else
    run_query bootstrap "DROP DATABASE IF EXISTS \`${database}\`; CREATE DATABASE \`${database}\`;"
  fi
}

assert_query() {
  local database="$1"
  local expected="$2"
  local query="$3"
  local actual
  actual="$(run_query "${database}" "${query}")"
  if [[ "${actual}" != "${expected}" ]]; then
    echo "Tooling readback failed for ${engine}/${database}: ${query}; expected ${expected}, got ${actual}." >&2
    exit 1
  fi
}

read_generated_index_name() {
  local migration="$1"
  local index_name
  # WHY: EF truncates convention names to the provider's identifier limit.
  # The generated fixture, not an unconstrained name repeated here, owns identity.
  index_name="$(sed -n 's/^[[:space:]]*name: "\(IX_scaffolding_generated_nodes_[^"]*\)".*/\1/p' "${migration}")"
  if [[ ! "${index_name}" =~ ^IX_scaffolding_generated_nodes_[A-Za-z0-9_~]+$ ]]; then
    echo "Expected exactly one generated-node index identity in ${migration}." >&2
    exit 1
  fi

  printf '%s\n' "${index_name}"
}

assert_generated_state() {
  local database="$1"
  local migration_id="$2"
  local generated_index="$3"
  local invalid_output

  if [[ "${engine}" == "postgres" ]]; then
    assert_query "${database}" 1 "/* tooling:generated-history */ SELECT CASE WHEN COUNT(*) = 1
      AND MIN(\"MigrationId\") = '${migration_id}' THEN 1 ELSE 0 END FROM \"__EFMigrationsHistory\";"
    assert_query "${database}" '1:7:administrator@example.test' "/* tooling:generated-seed */
      SELECT string_agg(\"Id\"::text || ':' || \"TenantId\"::text || ':' || \"Email\", ',' ORDER BY \"Id\")
      FROM scaffolding_users;"
    assert_query "${database}" 1 "/* tooling:generated-metadata */ SELECT COUNT(*) FROM pg_attribute a
      WHERE a.attrelid = 'scaffolding_generated_nodes'::regclass AND a.attname = 'HasDescendants'
        AND a.atttypid = 'boolean'::regtype AND a.attgenerated = 's'
        AND pg_get_expr((SELECT adbin FROM pg_attrdef WHERE adrelid = a.attrelid AND adnum = a.attnum),
          a.attrelid) LIKE '%CASE%';"
    assert_query "${database}" 1 "/* tooling:generated-index */ SELECT COUNT(*) FROM pg_index i
      JOIN pg_class c ON c.oid = i.indexrelid WHERE i.indrelid = 'scaffolding_generated_nodes'::regclass
        AND c.relname = '${generated_index}' AND i.indisvalid AND i.indisready AND NOT i.indisunique
        AND i.indnatts = 3 AND (SELECT string_agg(a.attname, ',' ORDER BY k.ordinality)
          FROM unnest(i.indkey) WITH ORDINALITY k(attnum, ordinality)
          JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum)
          = 'HasDescendants,LeftBound,RightBound';"
    assert_query "${database}" 1 "/* tooling:generated-check */ SELECT COUNT(*) FROM pg_constraint
      WHERE conrelid = 'scaffolding_generated_nodes'::regclass AND conname = 'ck_scaffolding_generated_bounds'
        AND contype = 'c' AND convalidated;"
    assert_query "${database}" 1 "/* tooling:generated-hierarchy */ SELECT CASE WHEN
      (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public'
        AND table_name = 'scaffolding_work_items' AND column_name = 'Discriminator' AND data_type = 'integer') = 1
      AND (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public'
        AND table_name = 'scaffolding_attributed_work_items' AND column_name = 'AttributedDiscriminator'
        AND data_type = 'character varying') = 1 THEN 1 ELSE 0 END;"
    assert_query "${database}" 1 "/* tooling:generated-foreign-keys */ SELECT CASE WHEN COUNT(*) = 2
      THEN 1 ELSE 0 END FROM pg_constraint WHERE contype = 'f'
        AND conrelid IN ('scaffolding_cycle_left'::regclass, 'scaffolding_cycle_right'::regclass);"
    run_query "${database}" 'INSERT INTO scaffolding_generated_nodes ("Id", "LeftBound", "RightBound")
      VALUES (100, 1, 2), (101, 1, 4), (102, 9223372036854775806, 9223372036854775807);' >/dev/null
    assert_query "${database}" '100:0,101:1,102:0' "/* tooling:generated-values */ SELECT
      string_agg(\"Id\"::text || ':' || \"HasDescendants\"::integer::text, ',' ORDER BY \"Id\")
      FROM scaffolding_generated_nodes;"
    if invalid_output="$(run_query "${database}" '/* tooling:invalid-generated-row */
      INSERT INTO scaffolding_generated_nodes ("Id", "LeftBound", "RightBound") VALUES (103, 5, 5);' 2>&1)"; then
      echo "The native CHECK accepted an invalid generated row for ${engine}/${database}." >&2
      exit 1
    fi

    assert_query "${database}" 0 '/* tooling:invalid-generated-row-absent */
      SELECT COUNT(*) FROM scaffolding_generated_nodes WHERE "Id" = 103;'
    run_query "${database}" 'DELETE FROM scaffolding_generated_nodes WHERE "Id" IN (100, 101, 102);' >/dev/null
  else
    assert_query "${database}" 1 "/* tooling:generated-history */ SELECT IF(COUNT(*) = 1
      AND MIN(\`MigrationId\`) = '${migration_id}', 1, 0) FROM \`__EFMigrationsHistory\`;"
    assert_query "${database}" '1:7:administrator@example.test' "/* tooling:generated-seed */
      SELECT GROUP_CONCAT(CONCAT(\`Id\`, ':', \`TenantId\`, ':', \`Email\`) ORDER BY \`Id\` SEPARATOR ',')
      FROM \`scaffolding_users\`;"
    assert_query "${database}" 1 "/* tooling:generated-metadata */ SELECT COUNT(*) FROM information_schema.columns
      WHERE table_schema = DATABASE() AND table_name = 'scaffolding_generated_nodes'
        AND column_name = 'HasDescendants' AND data_type = 'tinyint'
        AND LOWER(extra) LIKE '%virtual generated%' AND LOWER(generation_expression) LIKE '%case%';"
    assert_query "${database}" 1 "/* tooling:generated-index */ SELECT COUNT(*) FROM (
      SELECT index_name FROM information_schema.statistics WHERE table_schema = DATABASE()
        AND table_name = 'scaffolding_generated_nodes' AND index_name = '${generated_index}'
      GROUP BY index_name HAVING COUNT(*) = 3 AND MIN(non_unique) = 1
        AND GROUP_CONCAT(column_name ORDER BY seq_in_index SEPARATOR ',') = 'HasDescendants,LeftBound,RightBound'
        AND SUM(sub_part IS NOT NULL) = 0) exact_index;"
    assert_query "${database}" 1 "/* tooling:generated-check */ SELECT COUNT(*) FROM information_schema.table_constraints
      WHERE constraint_schema = DATABASE() AND table_name = 'scaffolding_generated_nodes'
        AND constraint_name = 'ck_scaffolding_generated_bounds' AND constraint_type = 'CHECK';"
    assert_query "${database}" 1 "/* tooling:generated-hierarchy */ SELECT IF(
      (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'scaffolding_work_items' AND column_name = 'Discriminator' AND data_type = 'int') = 1
      AND (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'scaffolding_attributed_work_items' AND column_name = 'AttributedDiscriminator'
        AND data_type = 'varchar') = 1, 1, 0);"
    assert_query "${database}" 1 "/* tooling:generated-foreign-keys */ SELECT IF(COUNT(*) = 2, 1, 0)
      FROM information_schema.referential_constraints WHERE constraint_schema = DATABASE()
        AND table_name IN ('scaffolding_cycle_left', 'scaffolding_cycle_right');"
    run_query "${database}" "INSERT INTO scaffolding_generated_nodes (\`Id\`, \`LeftBound\`, \`RightBound\`)
      VALUES (100, 1, 2), (101, 1, 4), (102, 9223372036854775806, 9223372036854775807);" >/dev/null
    assert_query "${database}" '100:0,101:1,102:0' "/* tooling:generated-values */ SELECT
      GROUP_CONCAT(CONCAT(\`Id\`, ':', \`HasDescendants\`) ORDER BY \`Id\` SEPARATOR ',')
      FROM scaffolding_generated_nodes;"
    if invalid_output="$(run_query "${database}" "/* tooling:invalid-generated-row */
      INSERT INTO scaffolding_generated_nodes (\`Id\`, \`LeftBound\`, \`RightBound\`) VALUES (103, 5, 5);" 2>&1)"; then
      echo "The native CHECK accepted an invalid generated row for ${engine}/${database}." >&2
      exit 1
    fi

    assert_query "${database}" 0 "/* tooling:invalid-generated-row-absent */
      SELECT COUNT(*) FROM scaffolding_generated_nodes WHERE \`Id\` = 103;"
    run_query "${database}" "DELETE FROM scaffolding_generated_nodes WHERE \`Id\` IN (100, 101, 102);" >/dev/null
  fi

  # WHY: Any unrelated SQL/connection failure is not evidence that the CHECK
  # rejected the intended invalid row. Require its actual constraint identity.
  if [[ "${invalid_output}" != *ck_scaffolding_generated_bounds* ]]; then
    echo "The native CHECK counterprobe failed for an unrelated reason: ${invalid_output}" >&2
    exit 1
  fi
}

assert_fixed_state() {
  local database="$1"
  local migration_id="$2"

  if [[ "${engine}" == "postgres" ]]; then
    assert_query "${database}" 1 "/* tooling:fixed-history */ SELECT CASE WHEN COUNT(*) = 1
      AND MIN(\"MigrationId\") = '${migration_id}' THEN 1 ELSE 0 END
      FROM \"__ApplicationDbContextMigrationsHistory\";"
    assert_query "${database}" 1 "/* tooling:fixed-metadata */ SELECT CASE WHEN
      (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public'
        AND table_name = 'pipeline_probe' AND column_name = 'Id' AND data_type = 'integer'
        AND is_nullable = 'NO') = 1 AND
      (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = 'public'
        AND table_name = 'pipeline_state' AND column_name = 'payload' AND data_type = 'character varying'
        AND character_maximum_length = 80 AND is_nullable = 'NO') = 1 THEN 1 ELSE 0 END;"
    run_query "${database}" 'INSERT INTO pipeline_probe ("Id") VALUES (1);
      INSERT INTO pipeline_state ("Id", payload) VALUES (1, '\''tooling'\'');' >/dev/null
    assert_query "${database}" '1:tooling' '/* tooling:fixed-values */
      SELECT s."Id"::text || '\'':'\'' || s.payload FROM pipeline_state s
      JOIN pipeline_probe p ON p."Id" = s."Id";'
    run_query "${database}" 'DELETE FROM pipeline_state; DELETE FROM pipeline_probe;' >/dev/null
  else
    assert_query "${database}" 1 "/* tooling:fixed-history */ SELECT IF(COUNT(*) = 1
      AND MIN(\`MigrationId\`) = '${migration_id}', 1, 0) FROM \`__ApplicationDbContextMigrationsHistory\`;"
    assert_query "${database}" 1 "/* tooling:fixed-metadata */ SELECT IF(
      (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'pipeline_probe' AND column_name = 'Id' AND data_type = 'int' AND is_nullable = 'NO') = 1
      AND (SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = DATABASE()
        AND table_name = 'pipeline_state' AND column_name = 'payload' AND data_type = 'varchar'
        AND character_maximum_length = 80 AND is_nullable = 'NO') = 1, 1, 0);"
    run_query "${database}" "INSERT INTO pipeline_probe (\`Id\`) VALUES (1);
      INSERT INTO pipeline_state (\`Id\`, payload) VALUES (1, 'tooling');" >/dev/null
    assert_query "${database}" '1:tooling' "/* tooling:fixed-values */
      SELECT CONCAT(s.\`Id\`, ':', s.payload) FROM pipeline_state s JOIN pipeline_probe p ON p.\`Id\` = s.\`Id\`;"
    run_query "${database}" 'DELETE FROM pipeline_state; DELETE FROM pipeline_probe;' >/dev/null
  fi
}

assert_ownership_state() {
  if [[ "${engine}" == "postgres" ]]; then
    assert_query tooling_ownership '1:owner,3:auditor' 'SELECT
      string_agg("Id"::text || '\'':'\'' || "Name", '\'','\'' ORDER BY "Id") FROM ownership_core_roles;'
    assert_query tooling_ownership '10:1:premium,12:3:audit' 'SELECT
      string_agg("Id"::text || '\'':'\'' || "CoreRoleId"::text || '\'':'\'' || "Name", '\'','\'' ORDER BY "Id")
      FROM ownership_custom_profiles;'
    assert_query tooling_ownership 2 'SELECT COUNT(*) FROM "__SafeMigrationsCoreHistory";'
    assert_query tooling_ownership 2 'SELECT COUNT(*) FROM "__SafeMigrationsCustomHistory";'
  else
    assert_query tooling_ownership '1:owner,3:auditor' "SELECT
      GROUP_CONCAT(CONCAT(\`Id\`, ':', \`Name\`) ORDER BY \`Id\` SEPARATOR ',') FROM ownership_core_roles;"
    assert_query tooling_ownership '10:1:premium,12:3:audit' "SELECT
      GROUP_CONCAT(CONCAT(\`Id\`, ':', \`CoreRoleId\`, ':', \`Name\`) ORDER BY \`Id\` SEPARATOR ',')
      FROM ownership_custom_profiles;"
    assert_query tooling_ownership 2 "SELECT COUNT(*) FROM \`__SafeMigrationsCoreHistory\`;"
    assert_query tooling_ownership 2 "SELECT COUNT(*) FROM \`__SafeMigrationsCustomHistory\`;"
  fi
}

qualify_generated_context() {
  local mode="$1"
  local context="$2"
  local connection="$3"
  local migration="$4"
  local database="tooling_generated_${mode}"
  local migration_id
  local generated_index
  local script
  local variant
  migration_id="$(basename "${migration}" .cs)"
  generated_index="$(read_generated_index_name "${migration}")"
  export SAFE_MIGRATIONS_CONNECTION_STRING="${connection}"
  reset_owned_database "${database}"
  dotnet ef database update --project "${project}" --context "${context}" --configuration Release --no-build
  assert_generated_state "${database}" "${migration_id}" "${generated_index}"
  dotnet ef database update --project "${project}" --context "${context}" --configuration Release --no-build
  assert_generated_state "${database}" "${migration_id}" "${generated_index}"

  for variant in normal idempotent no-transactions; do
    # WHY: A nonempty argument vector also works under macOS Bash 3.2's nounset
    # semantics, where expanding an empty optional-argument array is an error.
    local script_command=(ef migrations script --project "${project}" --context "${context}"
      --configuration Release --no-build --output "${artifacts_dir}/generated-${mode}-${variant}.sql")
    if [[ "${variant}" != normal ]]; then
      script_command+=(--idempotent)
    fi

    if [[ "${variant}" == no-transactions ]]; then
      script_command+=(--no-transactions)
    fi

    dotnet "${script_command[@]}"
  done

  dotnet ef migrations bundle --project "${project}" --context "${context}" --configuration Release \
    --output "${artifacts_dir}/generated-${mode}-efbundle" --force --verbose
  reset_owned_database "${database}"
  "${artifacts_dir}/generated-${mode}-efbundle" --connection "${connection}"
  assert_generated_state "${database}" "${migration_id}" "${generated_index}"
  "${artifacts_dir}/generated-${mode}-efbundle" --connection "${connection}"
  assert_generated_state "${database}" "${migration_id}" "${generated_index}"

  for variant in normal idempotent no-transactions; do
    script="${artifacts_dir}/generated-${mode}-${variant}.sql"
    reset_owned_database "${database}"
    run_script "${database}" "${script}"
    assert_generated_state "${database}" "${migration_id}" "${generated_index}"
    if [[ "${variant}" != normal ]]; then
      run_script "${database}" "${script}"
      assert_generated_state "${database}" "${migration_id}" "${generated_index}"
    fi
  done
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
  --disable-parallel --disable-build-servers -m:1 /nodeReuse:false
dotnet build "${project}" \
  --configuration Release --no-restore --disable-build-servers -m:1 /nodeReuse:false
dotnet tool restore --tool-manifest "${repository_root}/.config/dotnet-tools.json" \
  --disable-parallel
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

export SAFE_MIGRATIONS_CONNECTION_STRING="${generated_strict_connection}"
dotnet ef migrations add StrictScaffoldingProbe \
  --project "${project}" \
  --context StrictSafeMigrationScaffoldingDbContext \
  --output-dir "${strict_output}" \
  --configuration Release \
  --no-build

export SAFE_MIGRATIONS_CONNECTION_STRING="${generated_legacy_connection}"
dotnet ef migrations add LegacyScaffoldingProbe \
  --project "${project}" \
  --context LegacySafeMigrationScaffoldingDbContext \
  --output-dir "${legacy_output}" \
  --configuration Release \
  --no-build

export SAFE_MIGRATIONS_CONNECTION_STRING="${cli_connection}"
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
    'migrationBuilder.DeleteModelManagedDataFromModel(' \
    'migrationBuilder.AddColumnIfNotExistsFromModel(' \
    'migrationBuilder.DropColumnIfExists(' \
    'migrationBuilder.RenameColumnIfExists('; do
    if ! grep -Fq "${expected}" "${migration}"; then
      echo "Data-transition scaffolding output is missing from ${migration}: ${expected}" >&2
      grep -n 'migrationBuilder\.' "${migration}" >&2 || true
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
  for generated_contract in \
    'scaffolding_generated_nodes' \
    'HasDescendants' \
    'CASE WHEN' \
    'ck_scaffolding_generated_bounds'; do
    if ! grep -Fq "${generated_contract}" "${migration}"; then
      echo "Scaffolding output is missing its generated expression contract: ${generated_contract}" >&2
      exit 1
    fi
  done

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

  if ! grep -Fq 'migrationBuilder.AddForeignKeyIfNotExists(' "${migration}"; then
    echo "Scaffolding output is missing its safe standalone foreign key." >&2
    exit 1
  fi

  if grep -Fq 'migrationBuilder.AddForeignKey(' "${migration}"; then
    echo "Scaffolding output contains an unsafe standalone foreign key." >&2
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
    if ! grep -Fq 'migrationBuilder.EnsureSchemaExists(' "${migration}"; then
      echo "MySQL scaffolding output is missing safe current-database qualification." >&2
      exit 1
    fi

    if grep -Fq 'migrationBuilder.EnsureSchema(' "${migration}"; then
      echo "MySQL scaffolding output contains an unsafe provider schema operation." >&2
      exit 1
    fi

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

# WHY: Generating an ownership-safe file does not prove native script execution.
# Recreate only this harness-owned database, establish its external Core lineage
# through EF, then apply the custom SQL and observe both independent histories.
reset_owned_database tooling_ownership
export SAFE_MIGRATIONS_OWNERSHIP_STATE="target"
dotnet ef database update --project "${ownership_core_project}" --startup-project "${project}" \
  --context CoreOwnershipDbContext --configuration Release --no-build
run_script tooling_ownership "${artifacts_dir}/ownership-custom.sql"
assert_ownership_state
unset SAFE_MIGRATIONS_OWNERSHIP_STATE

export SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING="${generated_strict_connection}"
export SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING="${generated_legacy_connection}"
dotnet test "${project}" \
  --configuration Release --no-build --no-restore \
  --filter FullyQualifiedName~GeneratedInitialMigrationPreflightTests
unset SAFE_MIGRATIONS_GENERATED_STRICT_CONNECTION_STRING
unset SAFE_MIGRATIONS_GENERATED_LEGACY_CONNECTION_STRING

# Keep the direct preflight fixture above as separate evidence. These transports
# must load real compiled migrations, write history and replay their own output.
qualify_generated_context strict StrictSafeMigrationScaffoldingDbContext \
  "${generated_strict_connection}" "${strict_migration}"
qualify_generated_context legacy LegacySafeMigrationScaffoldingDbContext \
  "${generated_legacy_connection}" "${legacy_migration}"

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
dotnet ef migrations bundle \
  --project "${project}" --context SafeMigrationDbContext \
  --configuration Release --output "${artifacts_dir}/efbundle" --force --verbose
"${artifacts_dir}/efbundle" --connection "${bundle_connection}"
"${artifacts_dir}/efbundle" --connection "${bundle_connection}"

# WHY: The engine's own migration fixture declares this identifier. Reading it here
# instead of repeating the literal keeps a fixture rename from verifying a history
# row that the migration no longer writes, which passes the tests and fails only here.
migration_fixture="$(dirname "${project}")/Integration/CoreConvergenceMigration.cs"
migration_id="$(sed -n 's/.*MigrationIdentifier = "\([^"]*\)".*/\1/p' "${migration_fixture}" | head -n 1)"
if [[ -z "${migration_id}" ]]; then
  echo "Could not read the convergence migration identifier from ${migration_fixture}." >&2
  exit 1
fi

assert_fixed_state tooling_cli "${migration_id}"
assert_fixed_state tooling_bundle "${migration_id}"
for variant in normal idempotent no-transactions; do
  case "${variant}" in
    normal) database=tooling_script; script="${artifacts_dir}/migration.sql" ;;
    idempotent) database=tooling_script_idempotent; script="${artifacts_dir}/migration-idempotent.sql" ;;
    no-transactions)
      database=tooling_script_no_transactions
      script="${artifacts_dir}/migration-idempotent-no-transactions.sql"
      ;;
  esac

  reset_owned_database "${database}"
  run_script "${database}" "${script}"
  assert_fixed_state "${database}" "${migration_id}"
  if [[ "${variant}" != normal ]]; then
    run_script "${database}" "${script}"
    assert_fixed_state "${database}" "${migration_id}"
  fi
done

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
