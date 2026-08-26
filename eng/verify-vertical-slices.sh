#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

failures=0

fail() {
  echo "vertical-slice gate: $1" >&2
  failures=$((failures + 1))
}

require_file() {
  [[ -f "$1" ]] || fail "required file is missing: $1"
}

require_directory() {
  [[ -d "$1" ]] || fail "required directory is missing: $1"
}

directory_has_match() {
  local pattern="$1"
  local directory="$2"
  local file
  local status

  while IFS= read -r -d '' file; do
    if grep -Eq -- "$pattern" "$file"; then
      return 0
    else
      status=$?
      if ((status > 1)); then
        return "$status"
      fi
    fi
  done < <(find "$directory" -type f -name '*.cs' -print0)

  return 1
}

require_directory_match() {
  local pattern="$1"
  local directory="$2"
  local description="$3"
  local status

  if directory_has_match "$pattern" "$directory"; then
    return
  else
    status=$?
  fi

  if ((status == 1)); then
    fail "$description: $directory"
  else
    fail "could not inspect directory: $directory"
  fi
}

require_no_match() {
  local pattern="$1"
  local file="$2"
  local status

  if grep -Eq -- "$pattern" "$file"; then
    fail "forbidden feature implementation in central file: $file ($pattern)"
  else
    status=$?
    if ((status > 1)); then
      fail "could not inspect file: $file"
    fi
  fi
}

core_root="src/Doka.EntityFrameworkCore.SafeMigrations/Features"
mysql_root="src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Features"
postgresql_root="src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Features"

for slice in Schemas Tables Columns Indexes; do
  require_directory "$core_root/$slice"
  require_directory "$mysql_root/$slice"
  require_directory "$postgresql_root/$slice"
done

for slice in PrimaryKeys UniqueConstraints CheckConstraints ForeignKeys; do
  require_directory "$core_root/Constraints/$slice"
  require_directory "$mysql_root/Constraints/$slice"
  require_directory "$postgresql_root/Constraints/$slice"
done

require_file "$core_root/Columns/ExpectedColumnDefinition.cs"
require_file "$core_root/Tables/ExpectedTableDefinition.cs"
require_file "$core_root/Indexes/ExpectedIndexDefinition.cs"
require_file "$core_root/Columns/SafeMigrationColumnRepairHelper.cs"
require_file "$core_root/Schemas/SafeMigrationPreflightProjection.Schemas.cs"
require_file "$postgresql_root/Indexes/PostgreSqlSafeMigrationsSqlGenerator.Indexes.cs"
require_file "$mysql_root/Indexes/MySqlSafeMigrationOperationHandler.Indexes.cs"
require_file "benchmarks/Doka.EntityFrameworkCore.SafeMigrations.Benchmarks/Features/Columns/ColumnBenchmarkWorkload.cs"
require_file "benchmarks/Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks.csproj"
require_file "benchmarks/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks.csproj"

core_test_root="tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features"
for slice in Schemas Tables Columns Indexes Lifecycle; do
  require_directory "$core_test_root/$slice"
  require_directory_match \
    "^    \\[Fact\\]" \
    "$core_test_root/$slice" \
    "core test slice has no facts"
done

require_no_match "public sealed record" \
  "src/Doka.EntityFrameworkCore.SafeMigrations/Operations/SafeMigrationIntent.cs"
require_no_match "^    private .* Build(Ensure|Drop|Rename|Alter)[A-Za-z]+\\(" \
  "src/Doka.EntityFrameworkCore.SafeMigrations.MySql/SqlGeneration/MySqlSafeMigrationCatalogSqlBuilder.cs"
require_no_match "^    private .* Build(Ensure|Drop|Rename|Alter)[A-Za-z]+\\(" \
  "src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/SqlGeneration/PostgreSqlSafeMigrationCatalogSqlBuilder.cs"
require_no_match "^    \\[Fact\\]" \
  "tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/MySqlSafeMigrationIntegrationTests.cs"
require_no_match "^    \\[Fact\\]" \
  "tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Integration/PostgreSqlSafeMigrationIntegrationTests.cs"

for fixture in \
  SafeMigrationBuilderExtensionsTests \
  SafeMigrationContractFingerprintTests \
  SafeMigrationDefinitionTests \
  SafeMigrationExpectedCatalogTests \
  SafeMigrationPreflightProjectionTests \
  SafeMigrationStandardOperationFactoryTests; do
  require_no_match "^    \\[Fact\\]" \
    "tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/$fixture.cs"
done

for provider in MySql PostgreSql; do
  test_root="tests/Doka.EntityFrameworkCore.SafeMigrations.${provider}.Tests/Integration/Features"
  for slice in Schemas Tables Columns Indexes Constraints Lifecycle Identifiers; do
    require_directory "$test_root/$slice"
    require_directory_match \
      "^    \\[Fact\\]" \
      "$test_root/$slice" \
      "test slice has no facts"
  done
done

if ((failures > 0)); then
  echo "vertical-slice gate failed with $failures violation(s)." >&2
  exit 1
fi

echo "vertical-slice gate passed."
