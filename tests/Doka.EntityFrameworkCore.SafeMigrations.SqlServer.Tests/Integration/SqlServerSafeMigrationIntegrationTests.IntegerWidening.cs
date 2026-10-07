namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

public sealed partial class SqlServerSafeMigrationIntegrationTests
{
    /// <summary>Preserves complete integer source domains and replays qualified and default-schema repairs.</summary>
    [SqlServerLiveTheory]
    [InlineData("tinyint", typeof(byte), "smallint", typeof(short), "0,255", null)]
    [InlineData("tinyint", typeof(byte), "int", typeof(int), "0,255", "dbo")]
    [InlineData("tinyint", typeof(byte), "bigint", typeof(long), "0,255", null)]
    [InlineData("smallint", typeof(short), "int", typeof(int), "-32768,0,32767", "dbo")]
    [InlineData("smallint", typeof(short), "bigint", typeof(long), "-32768,0,32767", null)]
    [InlineData("int", typeof(int), "bigint", typeof(long), "-2147483648,0,2147483647", "dbo")]
    public async Task IntegerWidening_PreservesBoundaryRowsAndReplays(
        string sourceType,
        Type sourceClr,
        string targetType,
        Type targetClr,
        string values,
        string? schema
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var rows = string.Join(",", values.Split(',').Select(static value => "(" + value + ")"));
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.widening_values (Value " + sourceType + " NOT NULL); "
            + "INSERT dbo.widening_values VALUES " + rows + ";");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("widening_values",
            new ExpectedColumnDefinition("Value", targetClr, false, targetType),
            new ExpectedColumnDefinition("Value", sourceClr, false, sourceType),
            SafeMigrationPolicy.RepairIfSafe, schema);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-integer-widening"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.widening_values WHERE Value IN (" + values + ");");

        var matching = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns c JOIN sys.types ty ON ty.user_type_id=c.user_type_id "
            + "WHERE c.object_id=OBJECT_ID(N'dbo.widening_values') AND c.name=N'Value' AND ty.name=N'"
            + targetType + "' AND c.is_nullable=0;");

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(report.Assessments).Action);
        Assert.Equal(values.Split(',').Length, preserved);
        Assert.Equal(1, matching);
    }

    /// <summary>Does not confuse a fitting value with permission to narrow an integer domain.</summary>
    [SqlServerLiveFact]
    public async Task IntegerNarrowing_RemainsBlockedEvenWhenAllRowsFit()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.narrowing_values (Value bigint NOT NULL); INSERT dbo.narrowing_values VALUES (1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("narrowing_values",
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"),
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-integer-narrowing"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
    }

    /// <summary>Rejects both indexed columns and tables allocated on even a single partition scheme.</summary>
    [SqlServerLiveTheory]
    [InlineData("index")]
    [InlineData("partition")]
    [InlineData("statistics")]
    [InlineData("check")]
    public async Task IntegerWidening_PreservesUnremovedDependencies(
        string dependency
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        var setup = dependency == "partition"
            ? "CREATE PARTITION FUNCTION widening_partition(int) AS RANGE RIGHT FOR VALUES (100); "
                + "CREATE PARTITION SCHEME widening_scheme AS PARTITION widening_partition ALL TO ([PRIMARY]); "
                + "CREATE TABLE dbo.dependent_values (Value int NOT NULL) ON widening_scheme(Value);"
            : "CREATE TABLE dbo.dependent_values (Value int NOT NULL); " + (dependency switch
            {
                "index" => "CREATE INDEX IX_dependent_values ON dbo.dependent_values(Value);",
                "statistics" => "CREATE STATISTICS caller_statistics ON dbo.dependent_values(Value);",
                "check" => "ALTER TABLE dbo.dependent_values ADD CONSTRAINT CK_dependent_values CHECK(Value>=0);",
                _ => throw new ArgumentOutOfRangeException(nameof(dependency)),
            });

        await ExecuteSqlAsync(connectionString, setup + "INSERT dbo.dependent_values VALUES(1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("dependent_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-integer-dependencies"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var preserved = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.dependent_values') "
            + "AND name=N'Value' AND system_type_id=56;");

        // Assert
        Assert.NotEqual(SafeMigrationAction.Repair, Assert.Single(report.Assessments).Action);
        Assert.NotNull(failure);
        Assert.Equal(1, preserved);
    }

    /// <summary>Preserves identity generation state without reseeding or rewriting stored values.</summary>
    [SqlServerLiveFact]
    public async Task IntegerWidening_IdentityRetainsSeedIncrementAndNextGeneratedValue()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.identity_widening_values(Value int IDENTITY(3,7) NOT NULL); "
            + "INSERT dbo.identity_widening_values DEFAULT VALUES;");
        await using var context = CreateContext(connectionString);
        var sourceColumn = new AddColumnOperation
        {
            Name = "Value", Table = "identity_widening_values", ClrType = typeof(int), ColumnType = "int",
        };

        sourceColumn.AddAnnotation("SqlServer:Identity", "3, 7");
        var targetColumn = new AddColumnOperation
        {
            Name = "Value", Table = "identity_widening_values", ClrType = typeof(long), ColumnType = "bigint",
        };

        targetColumn.AddAnnotation("SqlServer:Identity", "3, 7");
        var operation = new SafeMigrationOperation(new AlterColumnIntent("identity_widening_values",
            SafeMigrationExpectedDefinitionFactory.From(targetColumn),
            SafeMigrationExpectedDefinitionFactory.From(sourceColumn)),
            SafeMigrationPolicy.RepairIfSafe);

        var sql = string.Join("\n", context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model)
            .Select(static command => command.CommandText));

        // Act
        var analyses = await context.GetService<ISafeMigrationProviderAnalyzer>().AnalyzeAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        await ExecuteOperationsAsync(context, [operation]);
        await ExecuteSqlAsync(connectionString, "INSERT dbo.identity_widening_values DEFAULT VALUES;");
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.identity_columns WHERE object_id=OBJECT_ID(N'dbo.identity_widening_values') "
            + "AND system_type_id=127 AND CONVERT(bigint,seed_value)=3 AND CONVERT(bigint,increment_value)=7 "
            + "AND CONVERT(bigint,last_value)=10;");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.identity_widening_values WHERE Value IN(3,10);");

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, Assert.Single(analyses).RepairCapability);
        Assert.DoesNotContain("DBCC CHECKIDENT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, retained);
        Assert.Equal(2, rows);
    }

    /// <summary>A zero-NULL widening retains its default without firing a redundant UPDATE trigger.</summary>
    [SqlServerLiveFact]
    public async Task IntegerWidening_DefaultAndNullTighteningDoesNotFireUpdateTrigger()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.default_widening_values(Id int NOT NULL); "
            + "CREATE TABLE dbo.update_audit(Events int NOT NULL); "
            + "INSERT dbo.update_audit VALUES(0);");
        await using var context = CreateContext(connectionString);
        var setup = new MigrationBuilder(context.Database.ProviderName!);
        setup.AddColumnIfNotExists<int>("Value", "default_widening_values", type: "int", nullable: true,
            defaultValue: 7);
        await ExecuteOperationsAsync(context, setup.Operations);
        await ExecuteSqlAsync(connectionString, "INSERT dbo.default_widening_values VALUES(1,7);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER dbo.default_widening_audit ON dbo.default_widening_values AFTER UPDATE AS "
            + "UPDATE dbo.update_audit SET Events=Events+1;");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("default_widening_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint",
                defaultValue: SafeMigrationDefaultValue.Literal(7L)),
            new ExpectedColumnDefinition("Value", typeof(int), true, "int",
                defaultValue: SafeMigrationDefaultValue.Literal(7)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-default-null-tightening"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteSqlAsync(connectionString, "INSERT dbo.default_widening_values(Id) VALUES(2);");
        var events = await ScalarIntAsync(connectionString, "SELECT Events FROM dbo.update_audit;");
        var retained = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.default_widening_values WHERE Value=7;");

        var stamps = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.default_constraints dc JOIN sys.extended_properties ep "
            + "ON ep.major_id=dc.object_id WHERE dc.parent_object_id=OBJECT_ID(N'dbo.default_widening_values') "
            + "AND ep.name=N'Doka:SafeMigrations:Contract';");

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, Assert.Single(report.Assessments).Action);
        Assert.Equal(0, events);
        Assert.Equal(2, retained);
        Assert.Equal(1, stamps);
    }

    /// <summary>An undeclared physical comment is drift, not permission to discard or ignore a facet.</summary>
    [SqlServerLiveFact]
    public async Task IntegerWidening_PhysicalCommentDriftPreservesRowsAndMetadataWithoutDdl()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.comment_widening_values(Value int NOT NULL); "
            + "INSERT dbo.comment_widening_values VALUES(-2147483648),(2147483647); "
            + "CREATE TABLE dbo.comment_ddl_audit(Events int NOT NULL); INSERT dbo.comment_ddl_audit VALUES(0); "
            + "EXEC sys.sp_addextendedproperty @name=N'MS_Description', @value=N'caller owned exact comment', "
            + "@level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', "
            + "@level1name=N'comment_widening_values', "
            + "@level2type=N'COLUMN', @level2name=N'Value';");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER comment_ddl_audit_trigger ON DATABASE FOR ALTER_TABLE AS "
            + "UPDATE dbo.comment_ddl_audit SET Events=Events+1;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("comment_widening_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-physical-comment-drift"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var events = await ScalarIntAsync(connectionString, "SELECT Events FROM dbo.comment_ddl_audit;");
        var source = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.comment_widening_values') "
            + "AND name=N'Value' AND system_type_id=56;");

        var comment = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.extended_properties WHERE class=1 "
            + "AND major_id=OBJECT_ID(N'dbo.comment_widening_values') "
            + "AND minor_id=COLUMNPROPERTY(major_id,N'Value',N'ColumnId') AND name=N'MS_Description' "
            + "AND CONVERT(nvarchar(100),value)=N'caller owned exact comment';");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.comment_widening_values WHERE Value IN(-2147483648,2147483647);");

        // Assert
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(0, events);
        Assert.Equal(1, source);
        Assert.Equal(1, comment);
        Assert.Equal(2, rows);
    }

    /// <summary>Qualified widening discharges primary, unique, and either-side FK blockers through drops.</summary>
    [SqlServerLiveTheory]
    [InlineData("primary")]
    [InlineData("unique")]
    [InlineData("foreign-child")]
    [InlineData("foreign-principal")]
    public async Task IntegerWidening_QualifiedNamedDependencyDropsPreflightAndExecute(
        string dependency
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA application;");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE application.dependent_values(Value int NOT NULL); "
            + "INSERT application.dependent_values VALUES(1);");
        var extra = dependency switch
        {
            "primary" => "ALTER TABLE application.dependent_values "
                + "ADD CONSTRAINT PK_dependent_values PRIMARY KEY(Value);",
            "unique" => "ALTER TABLE application.dependent_values ADD CONSTRAINT UQ_dependent_values UNIQUE(Value);",
            "foreign-child" => "CREATE TABLE application.principal_values(Value int NOT NULL PRIMARY KEY); "
                + "INSERT application.principal_values VALUES(1); ALTER TABLE application.dependent_values "
                + "ADD CONSTRAINT FK_dependent_values FOREIGN KEY(Value) "
                + "REFERENCES application.principal_values(Value);",
            "foreign-principal" => "ALTER TABLE application.dependent_values "
                + "ADD CONSTRAINT PK_dependent_values PRIMARY KEY(Value); "
                + "CREATE TABLE application.child_values(Value int NOT NULL); "
                + "INSERT application.child_values VALUES(1); "
                + "ALTER TABLE application.child_values ADD CONSTRAINT FK_dependent_values FOREIGN KEY(Value) "
                + "REFERENCES application.dependent_values(Value);",
            _ => throw new ArgumentOutOfRangeException(nameof(dependency)),
        };

        await ExecuteSqlAsync(connectionString, extra);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (dependency is "foreign-child" or "foreign-principal")
        {
            builder.DropForeignKeyIfExists("FK_dependent_values",
                dependency == "foreign-child" ? "dependent_values" : "child_values", "application");
        }

        if (dependency is "primary" or "foreign-principal")
        {
            builder.DropPrimaryKeyIfExists("PK_dependent_values", "dependent_values", "application");
        }
        else if (dependency == "unique")
        {
            builder.DropUniqueConstraintIfExists("UQ_dependent_values", "dependent_values", "application");
        }

        builder.AlterColumnIfDifferent("dependent_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe,
            "application");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-qualified-named-dependency-upgrade"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var target = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'application.dependent_values') "
            + "AND name=N'Value' AND system_type_id=127;");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM application.dependent_values WHERE Value=1;");

        // Assert
        Assert.True(report.Status == SafeMigrationReportStatus.Ready,
            string.Join(Environment.NewLine, report.Assessments.Select(static assessment =>
                $"{assessment.Ordinal}: {assessment.ObservedState}/{assessment.Action}/{assessment.AnalysisCode}")));
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[^1].Action);
        Assert.Equal(1, target);
        Assert.Equal(1, rows);
    }

    /// <summary>Only the target backing key's accepted FK drops authorize PK or UQ removal before widening.</summary>
    [SqlServerLiveTheory]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public async Task IntegerWidening_ReferencedKeyDropUsesExactBackingIndex(
        bool primaryKey,
        bool removeTargetForeignKey,
        bool addProjectedReference
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString, "CREATE SCHEMA application;");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE application.key_precision_values(Value int NOT NULL, Other int NOT NULL); "
            + "INSERT application.key_precision_values VALUES(1,2); "
            + "ALTER TABLE application.key_precision_values ADD CONSTRAINT Key_target "
            + (primaryKey ? "PRIMARY KEY(Value); " : "UNIQUE(Value); ")
            + "ALTER TABLE application.key_precision_values ADD CONSTRAINT Key_other "
            + (primaryKey ? "UNIQUE(Other); " : "PRIMARY KEY(Other); ")
            + "CREATE TABLE application.key_target_child(Value int NOT NULL); "
            + "INSERT application.key_target_child VALUES(1); "
            + "ALTER TABLE application.key_target_child ADD CONSTRAINT FK_key_target FOREIGN KEY(Value) "
            + "REFERENCES application.key_precision_values(Value); "
            + "CREATE TABLE application.key_other_child(Other int NOT NULL); "
            + "INSERT application.key_other_child VALUES(2); "
            + "ALTER TABLE application.key_other_child ADD CONSTRAINT FK_key_other FOREIGN KEY(Other) "
            + "REFERENCES application.key_precision_values(Other);");
        if (addProjectedReference)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE application.key_projected_child(Value int NOT NULL); "
                + "INSERT application.key_projected_child VALUES(1);");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var approved = removeTargetForeignKey && !addProjectedReference;
        if (removeTargetForeignKey)
        {
            builder.DropForeignKeyIfExists("FK_key_target", "key_target_child", "application");
        }

        if (addProjectedReference)
        {
            builder.AddForeignKeyIfNotExists("FK_key_projected", "key_projected_child", ["Value"],
                "key_precision_values", ["Value"], "application", "application");
        }

        if (primaryKey)
        {
            builder.DropPrimaryKeyIfExists("Key_target", "key_precision_values", "application");
        }
        else
        {
            builder.DropUniqueConstraintIfExists("Key_target", "key_precision_values", "application");
        }

        builder.AlterColumnIfDifferent("key_precision_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe,
            "application");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-key-index-specific-drop"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        if (approved)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        var targetType = await ScalarIntAsync(connectionString,
            "SELECT system_type_id FROM sys.columns WHERE object_id=OBJECT_ID(N'application.key_precision_values') "
            + "AND name=N'Value';");

        var targetKey = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.key_constraints WHERE parent_object_id="
            + "OBJECT_ID(N'application.key_precision_values') AND name=N'Key_target';");

        var otherForeignKey = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE parent_object_id="
            + "OBJECT_ID(N'application.key_other_child') AND name=N'FK_key_other';");

        var targetReferences = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.foreign_keys WHERE referenced_object_id="
            + "OBJECT_ID(N'application.key_precision_values') AND name IN(N'FK_key_target',N'FK_key_projected');");

        var projectedRows = addProjectedReference ? await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM application.key_projected_child WHERE Value=1;") : 0;

        var rows = await ScalarIntAsync(connectionString,
            "SELECT (SELECT COUNT(*) FROM application.key_precision_values WHERE Value=1 AND Other=2) "
            + "+ (SELECT COUNT(*) FROM application.key_target_child WHERE Value=1) "
            + "+ (SELECT COUNT(*) FROM application.key_other_child WHERE Other=2);");

        // Assert
        Assert.True(report.Status == (approved
                ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked),
            string.Join(Environment.NewLine, report.Assessments.Select(static assessment =>
                $"{assessment.Ordinal}: {assessment.ObservedState}/{assessment.Action}/{assessment.AnalysisCode}")));
        if (approved)
        {
            Assert.Null(failure);
            Assert.Equal(SafeMigrationAction.Repair, report.Assessments[^1].Action);
        }
        else
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
            Assert.Equal(SafeMigrationAction.RejectDifferent, report.Assessments[^1].Action);
        }

        Assert.Equal(approved ? 127 : 56, targetType);
        Assert.Equal(approved ? 0 : 1, targetKey);
        Assert.Equal(1, otherForeignKey);
        Assert.Equal(approved ? 0 : 1, targetReferences);
        Assert.Equal(addProjectedReference ? 1 : 0, projectedRows);
        Assert.Equal(3, rows);
    }

    /// <summary>A NULL row blocks widening plus tightening without replacing NULL or changing boundaries.</summary>
    [SqlServerLiveFact]
    public async Task IntegerWidening_NullTighteningWithNullRowsIsDataBlocked()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.null_widening_values(Value int NULL); "
            + "INSERT dbo.null_widening_values VALUES(NULL),(-2147483648),(2147483647);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("null_widening_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), true, "int"), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-integer-null-data-blocked"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var source = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.null_widening_values') "
            + "AND name=N'Value' AND system_type_id=56 AND is_nullable=1;");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.null_widening_values WHERE Value IS NULL OR Value IN(-2147483648,2147483647);");

        // Assert
        Assert.Equal(SafeMigrationObservedState.DataBlocked, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(1, source);
        Assert.Equal(3, rows);
    }

    /// <summary>Accepted DDL with an enabled trigger cannot reuse pre-DDL NULL or unrelated CHECK row truth.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, false, 51003)]
    [InlineData(true, false, 51001)]
    [InlineData(true, true, 51003)]
    public async Task IntegerWidening_DdlTriggerInvalidatesLaterNullAndCheckProofs(
        bool tightensNullability,
        bool dynamicTrigger,
        int expectedFailure
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.ddl_source_values(Value int NULL); INSERT dbo.ddl_source_values VALUES(1); "
            + "CREATE INDEX IX_ddl_source ON dbo.ddl_source_values(Value); "
            + "CREATE TABLE dbo.ddl_checked_values(Value int NULL); INSERT dbo.ddl_checked_values VALUES(1);");
        var mutation = tightensNullability ? "UPDATE dbo.ddl_source_values SET Value=NULL;"
            : "UPDATE dbo.ddl_checked_values SET Value=-1;";

        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER ddl_row_effect ON DATABASE FOR ALTER_TABLE,DROP_INDEX AS "
            + (dynamicTrigger ? "EXEC sys.sp_executesql N'" + mutation + "';" : mutation));
        var sourceDependencies = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.sql_expression_dependencies "
            + "WHERE referenced_id=OBJECT_ID(N'dbo.ddl_source_values');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_ddl_source", "ddl_source_values");
        builder.AlterColumnIfDifferent("ddl_source_values",
            new ExpectedColumnDefinition("Value", typeof(long), !tightensNullability, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), true, "int"), SafeMigrationPolicy.RepairIfSafe);
        if (!tightensNullability)
        {
            builder.AddCheckConstraintIfNotExists("CK_ddl_checked", "ddl_checked_values", "[Value]>=0");
        }

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-ddl-row-proof-freshness"));

        Exception? failure;
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
            await transaction.RollbackAsync();
        }

        var rows = await ScalarIntAsync(connectionString,
            "SELECT (SELECT COUNT(*) FROM dbo.ddl_source_values WHERE Value=1) "
            + "+ (SELECT COUNT(*) FROM dbo.ddl_checked_values WHERE Value=1);");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("projected_data_state_unknown", report.Assessments[^1].AnalysisCode);
        Assert.Equal(tightensNullability && !dynamicTrigger, sourceDependencies > 0);
        Assert.Equal(expectedFailure, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(2, rows);
    }

    /// <summary>Unproved trigger visibility blocks reused pre-DDL rows without a mandatory execution grant.</summary>
    [SqlServerLiveFact]
    public async Task IntegerWidening_UnknownServerDdlVisibilityBlocksPreflightButRuntimeRechecks()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.visibility_values(Value int NOT NULL); INSERT dbo.visibility_values VALUES(1); "
            + "CREATE USER ddl_database_reader WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION TO ddl_database_reader; "
            + "GRANT SELECT ON OBJECT::sys.sql_expression_dependencies TO ddl_database_reader; "
            + "GRANT ALTER,SELECT,UPDATE ON OBJECT::dbo.visibility_values TO ddl_database_reader;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'ddl_database_reader';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("visibility_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);
        builder.AddCheckConstraintIfNotExists("CK_visibility_values", "visibility_values", "[Value]>=0");
        SafeMigrationRunReport report;

        // Act
        try
        {
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-unproved-server-trigger-visibility"));
            await ExecuteOperationsAsync(context, builder.Operations);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var trusted = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id=OBJECT_ID(N'dbo.visibility_values') "
            + "AND name=N'CK_visibility_values' AND is_disabled=0 AND is_not_trusted=0;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Repair, report.Assessments[0].Action);
        Assert.Equal("projected_data_state_unknown", report.Assessments[1].AnalysisCode);
        Assert.Equal(1, trusted);
    }

    /// <summary>Database metadata visibility alone cannot authorize protected dependency-catalog binding.</summary>
    [SqlServerLiveTheory]
    [InlineData("integer")]
    [InlineData("text")]
    [InlineData("drop-column")]
    [InlineData("rename-column")]
    [InlineData("drop-table")]
    [InlineData("rename-table")]
    public async Task IntegerWidening_DeniedExpressionCatalogSelectFailsBeforeBinding(
        string operation
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.protected_catalog_values(Value int NOT NULL, Caption nvarchar(80) NULL); "
            + "INSERT dbo.protected_catalog_values VALUES(1,N'short'); "
            + "CREATE USER protected_catalog_reader WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION TO protected_catalog_reader; "
            + "GRANT ALTER,SELECT ON OBJECT::dbo.protected_catalog_values TO protected_catalog_reader;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'protected_catalog_reader';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        switch (operation)
        {
            case "integer":
                builder.AlterColumnIfDifferent("protected_catalog_values",
                    new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
                    new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);
                break;
            case "text":
                builder.AlterColumnIfDifferent("protected_catalog_values",
                    new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(20)", maxLength: 20),
                    new ExpectedColumnDefinition("Caption", typeof(string), true, "nvarchar(80)", maxLength: 80),
                    SafeMigrationPolicy.RepairIfSafe);
                break;
            case "drop-column":
                builder.DropColumnIfExists("Caption", "protected_catalog_values");
                break;
            case "rename-column":
                builder.RenameColumnIfExists("Caption", "protected_catalog_values", "ChangedCaption");
                break;
            case "drop-table":
                builder.DropTableIfExists("protected_catalog_values");
                break;
            case "rename-table":
                builder.RenameTableIfExists("protected_catalog_values", "protected_catalog_renamed");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }

        SafeMigrationRunReport report;
        Exception? failure;

        // Act
        try
        {
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-protected-dependency-catalog"));
            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var unchanged = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.protected_catalog_values') "
            + "AND (name=N'Value' AND system_type_id=56 OR name=N'Caption' AND max_length=160);");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.protected_catalog_values WHERE Value=1 AND Caption=N'short';");

        // Assert
        Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
        Assert.Equal("dependency_catalog_permission", Assert.Single(report.Assessments).AnalysisCode);
        Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(2, unchanged);
        Assert.Equal(1, rows);
    }

    /// <summary>Populated ALTER needs UPDATE permission; an exact matching NoOp needs no write permission.</summary>
    [SqlServerLiveTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task IntegerWidening_EffectiveUpdatePermissionIsRequiredOnlyForAlter(
        bool targetMatches,
        bool grantUpdate
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.write_permission_values(Value " + (targetMatches ? "bigint" : "int")
            + " NOT NULL); INSERT dbo.write_permission_values VALUES(1); "
            + "CREATE USER widening_writer WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION TO widening_writer; "
            + "GRANT SELECT ON OBJECT::sys.sql_expression_dependencies TO widening_writer; "
            + "GRANT ALTER,SELECT ON OBJECT::dbo.write_permission_values TO widening_writer; "
            + (grantUpdate ? "GRANT UPDATE ON OBJECT::dbo.write_permission_values TO widening_writer;" : string.Empty));
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'widening_writer';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("write_permission_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);
        var sql = string.Join(Environment.NewLine,
            context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model)
                .Select(static command => command.CommandText));

        SafeMigrationRunReport report;
        Exception? failure;
        Exception? directAlterFailure = null;

        // Act
        try
        {
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-integer-effective-update-permission"));
            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
            if (!targetMatches && !grantUpdate)
            {
                directAlterFailure = await Record.ExceptionAsync(() => context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE dbo.write_permission_values ALTER COLUMN Value bigint NOT NULL;"));
            }
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var physicalType = await ScalarIntAsync(connectionString,
            "SELECT system_type_id FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.write_permission_values') "
            + "AND name=N'Value';");

        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.write_permission_values WHERE Value=1;");

        // Assert
        Assert.DoesNotContain("UPDATE [dbo].[write_permission_values]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE [write_permission_values]", sql, StringComparison.Ordinal);
        if (targetMatches || grantUpdate)
        {
            Assert.Null(failure);
            Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
            Assert.Equal(targetMatches ? SafeMigrationAction.NoOp : SafeMigrationAction.Repair,
                Assert.Single(report.Assessments).Action);
        }
        else
        {
            Assert.Equal(229, Assert.IsType<SqlException>(directAlterFailure).Number);
            Assert.Contains("UPDATE permission", Assert.IsType<SqlException>(directAlterFailure).Message,
                StringComparison.Ordinal);
            Assert.Equal(SafeMigrationObservedState.Unsupported, Assert.Single(report.Assessments).ObservedState);
            Assert.Equal("column_alter_write_permission", Assert.Single(report.Assessments).AnalysisCode);
            Assert.Equal(51002, Assert.IsType<SqlException>(failure).Number);
        }

        Assert.Equal(targetMatches || grantUpdate ? 127 : 56, physicalType);
        Assert.Equal(1, rows);
    }

    /// <summary>An absent owner has no denied-write proof; ordered CREATE then ALTER rechecks and replays.</summary>
    [SqlServerLiveFact]
    public async Task IntegerWidening_AbsentOwnerDoesNotInventWritePermissionFailure()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTableIfNotExists("new_widening_values", columns => new
        {
            Value = columns.Column<int>(type: "int", nullable: false),
        }, mode: SafeMigrationTableMode.ConvergenceContainer);
        builder.AlterColumnIfDifferent("new_widening_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);

        // Act
        var absent = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, [builder.Operations[1]],
            new SafeMigrationRunOptions("sqlserver-absent-write-permission"));

        var ordered = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-created-source-runtime-widening"));

        await ExecuteOperationsAsync(context, builder.Operations);
        await ExecuteOperationsAsync(context, builder.Operations);
        var target = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.new_widening_values') "
            + "AND name=N'Value' AND system_type_id=127;");

        // Assert
        Assert.Equal(SafeMigrationObservedState.PrerequisiteMissing, Assert.Single(absent.Assessments).ObservedState);
        Assert.NotEqual("column_alter_write_permission", Assert.Single(absent.Assessments).AnalysisCode);
        Assert.Equal(SafeMigrationReportStatus.Blocked, ordered.Status);
        Assert.Equal(SafeMigrationObservedState.Different, ordered.Assessments[^1].ObservedState);
        Assert.NotEqual("column_alter_write_permission", ordered.Assessments[^1].AnalysisCode);
        Assert.Equal(1, target);
    }

    /// <summary>Projected definitions cannot override a protected catalog permission failure.</summary>
    [SqlServerLiveTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IntegerWidening_ProjectedDefinitionsRetainDependencyCatalogRejection(
        bool ownerExists
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        if (ownerExists)
        {
            await ExecuteSqlAsync(connectionString,
                "CREATE TABLE dbo.projected_permission_values(Value int NOT NULL, Other int NULL); "
                + "INSERT dbo.projected_permission_values VALUES(1,NULL);");
        }

        await ExecuteSqlAsync(connectionString,
            "CREATE USER projected_permission_reader WITHOUT LOGIN WITH DEFAULT_SCHEMA=dbo; "
            + "GRANT VIEW DEFINITION,CREATE TABLE TO projected_permission_reader; "
            + "GRANT ALTER,SELECT ON SCHEMA::dbo TO projected_permission_reader; "
            + "DENY SELECT ON OBJECT::sys.sql_expression_dependencies TO projected_permission_reader;");
        await using var context = CreateContext(connectionString);
        await context.Database.OpenConnectionAsync();
        await context.Database.ExecuteSqlRawAsync("EXECUTE AS USER=N'projected_permission_reader';");
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var source = new ExpectedColumnDefinition("Value", typeof(int), false, "int");
        if (ownerExists)
        {
            builder.EnsureColumn("projected_permission_values", source, SafeMigrationPolicy.ThrowIfDifferent);
            builder.DropColumnIfExists("Value", "projected_permission_values");
        }
        else
        {
            builder.CreateTableIfNotExists("projected_permission_values", columns => new
            {
                Value = columns.Column<int>(type: "int", nullable: false),
            });
            builder.AlterColumnIfDifferent("projected_permission_values",
                new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"), source,
                SafeMigrationPolicy.RepairIfSafe);
        }

        SafeMigrationRunReport report;

        // Act
        try
        {
            report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("sqlserver-projected-catalog-permission"));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("REVERT;");
        }

        var sourceCount = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.projected_permission_values') "
            + "AND name=N'Value' AND system_type_id=56;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationObservedState.Unsupported, report.Assessments[^1].ObservedState);
        Assert.Equal("dependency_catalog_permission", report.Assessments[^1].AnalysisCode);
        Assert.Equal(ownerExists ? 1 : 0, sourceCount);
    }

    /// <summary>An exact matching integer NoOp preserves row proof even when a DDL trigger is enabled.</summary>
    [SqlServerLiveFact]
    public async Task IntegerWidening_MatchingNoOpDoesNotInvalidateLaterCheckProof()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.noop_source_values(Value bigint NOT NULL); INSERT dbo.noop_source_values VALUES(1); "
            + "CREATE TABLE dbo.noop_checked_values(Value int NOT NULL); INSERT dbo.noop_checked_values VALUES(1);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER noop_ddl_row_effect ON DATABASE FOR ALTER_TABLE AS "
            + "UPDATE dbo.noop_checked_values SET Value=-1;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AlterColumnIfDifferent("noop_source_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);
        builder.AddCheckConstraintIfNotExists("CK_noop_checked", "noop_checked_values", "[Value]>=0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-noop-ddl-freshness"));

        await ExecuteOperationsAsync(context, [builder.Operations[0]]);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.noop_checked_values WHERE Value=1;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationAction.NoOp, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[1].Action);
        Assert.Equal(1, rows);
    }

    /// <summary>A second analysis on the same context recaptures disabled-trigger absence, not stale risk.</summary>
    [SqlServerLiveFact]
    public async Task IntegerWidening_DisabledDdlTriggerClearsRiskOnFreshAnalysis()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.refresh_source_values(Value int NOT NULL); INSERT dbo.refresh_source_values VALUES(1); "
            + "CREATE INDEX IX_refresh_source ON dbo.refresh_source_values(Value); "
            + "CREATE TABLE dbo.refresh_checked_values(Value int NOT NULL); "
            + "INSERT dbo.refresh_checked_values VALUES(1);");
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER refresh_ddl_row_effect ON DATABASE FOR ALTER_TABLE,DROP_INDEX AS "
            + "UPDATE dbo.refresh_checked_values SET Value=-1;");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("IX_refresh_source", "refresh_source_values");
        builder.AlterColumnIfDifferent("refresh_source_values",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe);
        builder.AddCheckConstraintIfNotExists("CK_refresh_checked", "refresh_checked_values", "[Value]>=0");
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var enabled = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-enabled-ddl-trigger-freshness"));

        await context.Database.ExecuteSqlRawAsync("DISABLE TRIGGER refresh_ddl_row_effect ON DATABASE;");
        var disabled = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-disabled-ddl-trigger-freshness"));

        await ExecuteOperationsAsync(context, builder.Operations);
        var rows = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM dbo.refresh_checked_values WHERE Value=1;");

        var trusted = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.check_constraints "
            + "WHERE parent_object_id=OBJECT_ID(N'dbo.refresh_checked_values') "
            + "AND name=N'CK_refresh_checked' AND is_disabled=0 AND is_not_trusted=0;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, enabled.Status);
        Assert.Equal("projected_data_state_unknown", enabled.Assessments[^1].AnalysisCode);
        Assert.Equal(SafeMigrationReportStatus.Ready, disabled.Status);
        Assert.Equal(SafeMigrationAction.Repair, disabled.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, disabled.Assessments[2].Action);
        Assert.Equal(1, rows);
        Assert.Equal(1, trusted);
    }

    /// <summary>An EF CREATE with a row-mutating DDL trigger cannot lend projected emptiness to a CHECK.</summary>
    [SqlServerLiveFact]
    public async Task IntegerWidening_TypedProviderCreateTriggerInvalidatesProjectedEmptyCheck()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "CREATE TRIGGER create_ddl_row_effect ON DATABASE FOR CREATE_TABLE AS "
            + "IF OBJECT_ID(N'dbo.created_values',N'U') IS NOT NULL INSERT dbo.created_values VALUES(-1);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTable("created_values", columns: table => new
        {
            Value = table.Column<int>(type: "int", nullable: false),
        });

        builder.AddCheckConstraintIfNotExists("CK_created_values", "created_values", "[Value]>=0");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-typed-create-ddl-row-freshness"));

        Exception? failure;
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
            await transaction.RollbackAsync();
        }

        var created = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE object_id=OBJECT_ID(N'dbo.created_values');");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal("projected_data_state_unknown", report.Assessments[^1].AnalysisCode);
        Assert.Equal(51003, Assert.IsType<SqlException>(failure).Number);
        Assert.Equal(0, created);
    }

    /// <summary>Accepted drops resolve physical identity under catalog collation rather than CLR case rules.</summary>
    /// <param name="caseSensitive">Whether the catalog distinguishes the authored index name's case.</param>
    [SqlServerLiveTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntegerWidening_AliasedDropUsesExactCatalogIdentity(
        bool caseSensitive
    )
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync();
        await ExecuteSqlAsync(connectionString,
            "ALTER DATABASE CURRENT COLLATE Latin1_General_100_" + (caseSensitive ? "CS" : "CI") + "_AS;");
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE dbo.alias_values(Value int NOT NULL); INSERT dbo.alias_values VALUES(1); "
            + "CREATE INDEX IX_alias_values ON dbo.alias_values(Value);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropIndexIfExists("ix_alias_values", caseSensitive ? "alias_values" : "ALIAS_VALUES",
            caseSensitive ? "dbo" : "DBO");
        builder.AlterColumnIfDifferent(caseSensitive ? "alias_values" : "ALIAS_VALUES",
            new ExpectedColumnDefinition("Value", typeof(long), false, "bigint"),
            new ExpectedColumnDefinition("Value", typeof(int), false, "int"), SafeMigrationPolicy.RepairIfSafe,
            caseSensitive ? "dbo" : "DBO");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sqlserver-catalog-drop-identity"));

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var source = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(N'dbo.alias_values') "
            + "AND name=N'Value' AND system_type_id=" + (caseSensitive ? "56" : "127") + ";");

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM dbo.alias_values WHERE Value=1;");

        // Assert
        Assert.Equal(caseSensitive ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);
        Assert.Equal(caseSensitive ? SafeMigrationAction.NoOp : SafeMigrationAction.Apply,
            report.Assessments[0].Action);
        Assert.Equal(caseSensitive ? SafeMigrationAction.RejectDifferent : SafeMigrationAction.Repair,
            report.Assessments[1].Action);
        if (caseSensitive)
        {
            Assert.Equal(51001, Assert.IsType<SqlException>(failure).Number);
        }
        else
        {
            Assert.Null(failure);
        }

        Assert.Equal(1, source);
        Assert.Equal(1, rows);
    }
}
