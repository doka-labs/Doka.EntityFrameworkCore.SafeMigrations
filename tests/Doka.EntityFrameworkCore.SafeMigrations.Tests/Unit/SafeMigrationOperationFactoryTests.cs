namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Unit;

public sealed class SafeMigrationOperationFactoryTests
{
    [Fact]
    public void AddColumnOperation_IsAnnotatedForIfNotExistsAndStrictMode()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            type: "varchar(200)",
            nullable: true,
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var operation = Assert.Single(migrationBuilder.Operations);

        Assert.Equal(true, operation[SafeMigrationAnnotationNames.IfNotExists]);
        Assert.Equal(SafeMigrationStrictMode.ThrowIfDifferent, operation[SafeMigrationAnnotationNames.StrictMode]);
        Assert.NotNull(operation[SafeMigrationAnnotationNames.ExpectedDefinition]);
    }

    [Fact]
    public void AddColumnOperation_WithExecutionOptions_StoresV12Annotations()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true),
            type: "varchar(200)",
            nullable: true);

        var operation = Assert.IsType<AddColumnOperation>(Assert.Single(migrationBuilder.Operations));

        Assert.Equal(true, operation[SafeMigrationAnnotationNames.IfNotExists]);
        Assert.Equal(SafeMigrationStrictMode.ThrowIfDifferent, operation[SafeMigrationAnnotationNames.StrictMode]);
        Assert.Equal(SafeMigrationConflictMode.RepairIfPossible, operation[SafeMigrationAnnotationNames.ConflictMode]);
        Assert.Equal(true, operation[SafeMigrationAnnotationNames.PreflightOnly]);
        Assert.NotNull(operation[SafeMigrationAnnotationNames.ExpectedDefinition]);
    }

    [Fact]
    public void DropPrimaryKeyOperation_AllowsExplicitConstraintNameWithoutHardcodedPrimary()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.DropPrimaryKeyIfExists(
            table: "Employees",
            name: "PK_Employees");

        var operation = Assert.IsType<DropPrimaryKeyOperation>(Assert.Single(migrationBuilder.Operations));

        Assert.Equal("PK_Employees", operation.Name);
        Assert.Equal(true, operation[SafeMigrationAnnotationNames.IfExists]);
    }

    [Fact]
    public void CreateIndexOperation_WithExecutionOptions_StoresV12Annotations()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.ThrowIfDifferent));

        var operation = Assert.IsType<CreateIndexOperation>(Assert.Single(migrationBuilder.Operations));

        Assert.Equal(true, operation[SafeMigrationAnnotationNames.IfNotExists]);
        Assert.Equal(SafeMigrationStrictMode.ThrowIfDifferent, operation[SafeMigrationAnnotationNames.StrictMode]);
        Assert.Equal(SafeMigrationConflictMode.ThrowIfDifferent, operation[SafeMigrationAnnotationNames.ConflictMode]);
        Assert.Equal(false, operation[SafeMigrationAnnotationNames.PreflightOnly]);
        Assert.NotNull(operation[SafeMigrationAnnotationNames.ExpectedDefinition]);
    }

    [Fact]
    public void CreateIndexOperation_WithRepairAndPreflight_StoresExecutionOptions()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.CreateIndexIfNotExists(
            name: "IX_Employees_DisplayName",
            table: "Employees",
            columns: ["DisplayName"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var operation = Assert.IsType<CreateIndexOperation>(Assert.Single(migrationBuilder.Operations));

        Assert.Equal(SafeMigrationStrictMode.ThrowIfDifferent, operation[SafeMigrationAnnotationNames.StrictMode]);
        Assert.Equal(SafeMigrationConflictMode.RepairIfPossible, operation[SafeMigrationAnnotationNames.ConflictMode]);
        Assert.Equal(true, operation[SafeMigrationAnnotationNames.PreflightOnly]);
    }

    [Fact]
    public void AddUniqueConstraintOperation_WithExecutionOptions_StoresExecutionObject()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.AddUniqueConstraintIfNotExists(
            name: "AK_Employees_Email",
            table: "Employees",
            columns: ["Email"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var operation = Assert.IsType<SafeAddUniqueConstraintOperation>(Assert.Single(migrationBuilder.Operations));

        Assert.Equal(SafeMigrationStrictMode.ThrowIfDifferent, operation.StrictMode);
        Assert.NotNull(operation.Execution);
        Assert.Equal(SafeMigrationConflictMode.RepairIfPossible, operation.Execution!.ConflictMode);
        Assert.True(operation.Execution.PreflightOnly);
    }

    [Fact]
    public void AddPrimaryKeyOperation_WithExecutionOptions_StoresExecutionObject()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.AddPrimaryKeyIfNotExists(
            name: "PK_Employees",
            table: "Employees",
            columns: ["Id"],
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var operation = Assert.IsType<SafeAddPrimaryKeyOperation>(Assert.Single(migrationBuilder.Operations));

        Assert.Equal(SafeMigrationStrictMode.ThrowIfDifferent, operation.StrictMode);
        Assert.NotNull(operation.Execution);
        Assert.Equal(SafeMigrationConflictMode.RepairIfPossible, operation.Execution!.ConflictMode);
        Assert.True(operation.Execution.PreflightOnly);
    }

    [Fact]
    public void AddForeignKeyOperation_WithExecutionOptions_StoresExecutionObject()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.AddForeignKeyIfNotExists(
            name: "FK_Employees_Departments_DepartmentId",
            table: "Employees",
            columns: ["DepartmentId"],
            principalTable: "Departments",
            principalColumns: ["Id"],
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        var operation = Assert.IsType<SafeAddForeignKeyOperation>(Assert.Single(migrationBuilder.Operations));

        Assert.Equal(SafeMigrationStrictMode.ThrowIfDifferent, operation.StrictMode);
        Assert.NotNull(operation.Execution);
        Assert.Equal(SafeMigrationConflictMode.RepairIfPossible, operation.Execution!.ConflictMode);
        Assert.False(operation.Execution.PreflightOnly);
    }

    [Fact]
    public void AddCheckConstraintOperation_WithExecutionOptions_StoresExecutionObject()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Age",
            table: "Employees",
            sql: "Age >= 18",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.RepairIfPossible,
                PreflightOnly: true));

        var operation = Assert.IsType<SafeAddCheckConstraintOperation>(Assert.Single(migrationBuilder.Operations));

        Assert.Equal(SafeMigrationStrictMode.ThrowIfDifferent, operation.StrictMode);
        Assert.NotNull(operation.Execution);
        Assert.Equal(SafeMigrationConflictMode.RepairIfPossible, operation.Execution!.ConflictMode);
        Assert.True(operation.Execution.PreflightOnly);
    }

    [Fact]
    public void AddCheckConstraintOperation_WithRepairMode_NoLongerThrows()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.AddCheckConstraintIfNotExists(
            name: "CK_Employees_Name",
            table: "Employees",
            sql: "`DisplayName` <> ''",
            execution: new SafeMigrationExecutionOptions(SafeMigrationConflictMode.RepairIfPossible));

        var operation = Assert.IsType<SafeAddCheckConstraintOperation>(Assert.Single(migrationBuilder.Operations));

        Assert.NotNull(operation.Execution);
        Assert.Equal(SafeMigrationConflictMode.RepairIfPossible, operation.Execution!.ConflictMode);
        Assert.False(operation.Execution.PreflightOnly);
    }

    [Fact]
    public void AddColumnOperation_WithPreflight_NoLongerThrows()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "DisplayName",
            table: "Employees",
            execution: new SafeMigrationExecutionOptions(
                SafeMigrationConflictMode.ThrowIfDifferent,
                PreflightOnly: true),
            nullable: true);

        var operation = Assert.IsType<AddColumnOperation>(Assert.Single(migrationBuilder.Operations));
        Assert.Equal(true, operation[SafeMigrationAnnotationNames.PreflightOnly]);
    }

    [Fact]
    public void AddColumnOperation_WithLiteralDefault_StoresTypedDefaultMetadata()
    {
        var migrationBuilder = new MigrationBuilder("Microsoft.EntityFrameworkCore.Sqlite");

        migrationBuilder.AddColumnIfNotExists<string>(
            name: "Status",
            table: "Employees",
            type: "text",
            nullable: false,
            defaultValue: "active",
            strictMode: SafeMigrationStrictMode.ThrowIfDifferent);

        var operation = Assert.IsType<AddColumnOperation>(Assert.Single(migrationBuilder.Operations));
        var expectedDefinition = SafeMigrationDefinitionSerializer.Deserialize<ExpectedColumnDefinition>(
            operation[SafeMigrationAnnotationNames.ExpectedDefinition] as string);

        Assert.NotNull(expectedDefinition);
        Assert.Equal("active", expectedDefinition.DefaultValueLiteral);
        Assert.NotNull(expectedDefinition.DefaultValueTypeName);
        Assert.NotNull(expectedDefinition.DefaultValueJson);
        Assert.True(
            SafeMigrationDefaultValueSerializer.TryDeserialize(
                expectedDefinition.DefaultValueTypeName,
                expectedDefinition.DefaultValueJson,
                out var value,
                out var type));
        Assert.Equal(typeof(string), type);
        Assert.Equal("active", Assert.IsType<string>(value));
    }
}
