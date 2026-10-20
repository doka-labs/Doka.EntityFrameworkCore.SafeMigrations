namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Checks accepted CHECK member dependencies without opening a database connection.</summary>
public sealed class SqlServerCheckDropRoleTests
{
    /// <summary>Parsed source roles use exact native identities, never a CLR case-folding approximation.</summary>
    /// <param name="sql">The accepted native CHECK expression.</param>
    /// <param name="sourceIdentity">The captured physical identity for the CHECK member.</param>
    /// <param name="expected">The expected structured dependency reason, or null for a proven unrelated CHECK.</param>
    [Theory]
    [InlineData("[L]>0", 1, "projected_check_source_dependency")]
    [InlineData("[l]>0", 1, "projected_check_source_dependency")]
    [InlineData("[l]>0", 2, null)]
    [InlineData("[l]>0", 0, "projected_check_source_dependency_unproven")]
    [InlineData("[R]>0", 2, null)]
    [InlineData("1=1", 0, null)]
    [InlineData("TRY_CONVERT(int,[R])>0", 2, "projected_check_source_dependency_unproven")]
    public async Task AcceptedCheckDropUsesBoundMemberRoles(string sql, int sourceIdentity, string? expected)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Server=localhost;Database=drop_roles;Integrated Security=true;");
        var analyzer = (SqlServerSafeMigrationProviderAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();
        var expression = SafeMigrationSqlExpressionParser.TryParseCapturedSource(sql, out var parsed)
            ? parsed : throw new InvalidOperationException("The source role fixture must parse.");

        var check = new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            ExpectedCheckConstraintDefinition.FromCapturedExpression("CK_roles", "nodes", expression, sql, null)),
            SafeMigrationPolicy.ThrowIfDifferent);

        var drop = new SafeMigrationOperation(new DropColumnIntent("L", "nodes"),
            SafeMigrationPolicy.ThrowIfDifferent);

        CaptureRoles(analyzer, sourceIdentity);
        var capture = typeof(SqlServerSafeMigrationProviderAnalyzer).GetMethod(
            "ReadProjectedCheckRenameDependenciesAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The accepted CHECK dependency capture is unavailable.");

        var read = capture.CreateDelegate<Func<System.Data.Common.DbConnection, System.Data.Common.DbTransaction?,
            IReadOnlyList<MigrationOperation>, int?, CancellationToken, Task>>(analyzer);

        // Act
        await read(context.Database.GetDbConnection(), null, [check, drop], null, CancellationToken.None);
        var missing = new SafeMigrationProviderAnalysis(SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "classified_missing");

        analyzer.ObserveAcceptedOperation(check, missing, missing,
            SafeMigrationDecisionPlanner.Plan(check.Intent.Kind, missing.ObservedState, check.Policy));
        var result = analyzer.ValidateCapturedProjection(drop, missing, new EmptyColumns());

        // Assert
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        if (expected is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(expected, result.Code);
            Assert.Equal(expected.EndsWith("_unproven", StringComparison.Ordinal)
                ? SafeMigrationObservedState.Unsupported : SafeMigrationObservedState.Different, result.ObservedState);
        }
    }

    private static void CaptureRoles(SqlServerSafeMigrationProviderAnalyzer analyzer, int sourceIdentity)
    {
        using var layout = new DataTable();
        layout.Columns.Add("schema", typeof(string));
        layout.Columns.Add("table", typeof(string));
        layout.Columns.Add("columns", typeof(int));
        layout.Columns.Add("fixed", typeof(int));
        layout.Columns.Add("bits", typeof(int));
        layout.Columns.Add("variables", typeof(int));
        layout.Columns.Add("clustered", typeof(int));
        layout.Columns.Add("known", typeof(bool));
        layout.Rows.Add("dbo", "nodes", 2, 16, 0, 0, 0, true);
        using var layoutReader = layout.CreateDataReader();
        layoutReader.Read();
        analyzer.CaptureProjectedColumnLayout(layoutReader);

        using var bindings = new DataTable();
        bindings.Columns.Add("schema", typeof(string));
        bindings.Columns.Add("table", typeof(string));
        bindings.Columns.Add("name", typeof(string));
        bindings.Columns.Add("identity", typeof(int));
        bindings.Columns.Add("variable", typeof(int));
        bindings.Columns.Add("clustered", typeof(bool));
        bindings.Rows.Add("dbo", "nodes", "L", 1, 0, false);
        if (sourceIdentity > 0)
        {
            bindings.Rows.Add("dbo", "nodes", "l", sourceIdentity, 0, false);
            bindings.Rows.Add("dbo", "nodes", "R", 2, 0, false);
        }

        using var reader = bindings.CreateDataReader();
        while (reader.Read())
        {
            analyzer.CaptureProjectedColumnLayoutBinding(reader);
        }
    }

    private sealed class EmptyColumns : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(string table, string? schema, string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition)
        {
            definition = null;

            return false;
        }
    }
}
