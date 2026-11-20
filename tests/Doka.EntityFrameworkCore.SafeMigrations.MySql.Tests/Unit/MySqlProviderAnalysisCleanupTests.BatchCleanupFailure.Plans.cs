namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

public sealed partial class MySqlProviderAnalysisCleanupTests
{
    /// <summary>Drives the actual analyzer lifecycle with one unambiguous captured classifier stage.</summary>
    /// <param name="context">The real provider context supplying validated mappings and the model.</param>
    /// <param name="stage">The classifier-owned batch whose actual disposal is under test.</param>
    /// <returns>The real analyzer with a deterministic plan-capture producer.</returns>
    private static MySqlSafeMigrationProviderAnalyzer CreateBatchCleanupAnalyzer(
        DbContext context,
        string stage
    )
    {
        var capture = new MySqlSafeMigrationPlanCapture();
        var plan = new MySqlSafeMigrationRuntimePlan("'matching'", "TRUE", SafeMigrationRepairCapability.None, "FALSE");
        plan = stage switch
        {
            "binary_proof" => plan with
            {
                BinaryDefaultProofs =
                [
                    new MySqlSafeMigrationBinaryDefaultProof(
                        "__DOKA_SM_BINARY_DEFAULT_0__",
                        "TRUE",
                        "SELECT TRUE"),
                ],
            },
            "identifier_proof" => plan with
            {
                IdentifierReferenceProofs =
                [
                    new MySqlSafeMigrationIdentifierReferenceProof(
                        "__DOKA_SM_IDENTIFIER_REFERENCE_0__",
                        "LEGACYWORD",
                        "11111111111111111111111111111111"),
                ],
            },
            "classification" => plan with
            {
                StateExpression = "CASE WHEN 'batch_cleanup_classification' IS NOT NULL THEN 'matching' END",
            },
            "qualification" => plan with
            {
                CurrentDatabaseQualificationExpression = "'batch_cleanup_qualification' IS NOT NULL",
            },
            "prerequisite" => plan with
            {
                PrerequisiteExpression = "'batch_cleanup_prerequisite' IS NOT NULL",
            },
            "state_guard" => plan with
            {
                StateEvaluationGuardExpression = "'batch_cleanup_state_guard' IS NOT NULL",
                StateEvaluationGuardFailureExpression = "'different'",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

        return new MySqlSafeMigrationProviderAnalyzer(
            new BatchCleanupPlanGenerator(capture, plan),
            capture,
            context.GetService<IRelationalTypeMappingSource>());
    }

    /// <summary>Supplies exact scalar-free metadata, prerequisite and classification result shapes.</summary>
    /// <param name="native">Whether the transport exposes actual native ADO batches.</param>
    /// <returns>The deterministic native or sequential statement source retained by its owning test.</returns>
    private static CatalogStatementTestConnection CreateBatchCleanupCommands(
        bool native
    ) => new(native,
        static (sql, _) =>
        {
            var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            if (sql == "SELECT @@max_allowed_packet, @@lower_case_table_names;")
            {
                table.Columns.Add("packet", typeof(long));
                table.Columns.Add("ownerMode", typeof(int));
                table.Rows.Add(16L * 1024 * 1024, 0);

                return table;
            }

            if (sql.Contains("@doka_sm_binary_default_sql", StringComparison.Ordinal)
                || sql.Contains("@doka_sm_ir_result_", StringComparison.Ordinal))
            {
                table.Columns.Add("ordinal", typeof(int));
                table.Columns.Add("proof", typeof(bool));
                table.Rows.Add(0, true);

                return table;
            }

            Assert.StartsWith("SELECT 0,", sql, StringComparison.Ordinal);
            table.Columns.Add("ordinal", typeof(int));
            table.Columns.Add("state", typeof(string));
            if (sql.Contains("batch_cleanup_qualification", StringComparison.Ordinal)
                || sql.Contains("batch_cleanup_prerequisite", StringComparison.Ordinal))
            {
                table.Rows.Add(0, DBNull.Value);

                return table;
            }

            if (sql.Contains("batch_cleanup_state_guard", StringComparison.Ordinal))
            {
                table.Columns.Add("code", typeof(string));
                table.Rows.Add(0, DBNull.Value, DBNull.Value);

                return table;
            }

            table.Columns.Add("postcondition", typeof(bool));
            table.Columns.Add("repair", typeof(bool));
            table.Columns.Add("code", typeof(string));
            table.Columns.Add("rowEvidence", typeof(string));
            table.Columns.Add("dependencyCounts", typeof(string));
            table.Columns.Add("diagnostics", typeof(string));
            table.Columns.Add("matchedName", typeof(string));
            table.Rows.Add(
                0,
                "matching",
                true,
                false,
                DBNull.Value,
                DBNull.Value,
                DBNull.Value,
                DBNull.Value,
                DBNull.Value);

            return table;
        });

    /// <summary>Records one exact isolated plan through the real analyzer's ordinary bounded capture lease.</summary>
    private sealed class BatchCleanupPlanGenerator : IMigrationsSqlGenerator
    {
        private readonly MySqlSafeMigrationPlanCapture _capture;
        private readonly MySqlSafeMigrationRuntimePlan _plan;

        /// <summary>Retains the exact invocation producer and immutable native plan shape.</summary>
        /// <param name="capture">The capture instance also owned by the real analyzer.</param>
        /// <param name="plan">The exact classifier plan selecting one resource boundary.</param>
        internal BatchCleanupPlanGenerator(
            MySqlSafeMigrationPlanCapture capture,
            MySqlSafeMigrationRuntimePlan plan
        )
        {
            _capture = capture;
            _plan = plan;
        }

        /// <inheritdoc />
        public IReadOnlyList<MigrationCommand> Generate(
            IReadOnlyList<MigrationOperation> operations,
            IModel? model = null,
            MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default
        )
        {
            // WHY: Exact operation identity and ordinal must still pass the real capture lease checks.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var ordinal = 0; ordinal < operations.Count; ordinal++)
            {
                _capture.Record(ordinal, Assert.IsType<SafeMigrationOperation>(operations[ordinal]), _plan);
            }

            return [];
        }
    }
}
