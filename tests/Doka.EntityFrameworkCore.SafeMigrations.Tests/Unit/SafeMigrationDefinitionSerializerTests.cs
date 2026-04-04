namespace Doka.EntityFrameworkCore.SafeMigrations.Tests.Unit;

public sealed class SafeMigrationDefinitionSerializerTests
{
    [Fact]
    public void SerializeAndDeserialize_RoundTripsExpectedIndexDefinition()
    {
        var definition = new ExpectedIndexDefinition(
            "IX_Employees_Name",
            "Employees",
            null,
            ["Name"],
            false,
            null,
            [false]);

        var json = SafeMigrationDefinitionSerializer.Serialize(definition);
        var restored = SafeMigrationDefinitionSerializer.Deserialize<ExpectedIndexDefinition>(json);

        Assert.NotNull(restored);
        Assert.Equal(definition.Name, restored.Name);
        Assert.Equal(definition.Table, restored.Table);
        Assert.Equal(definition.Schema, restored.Schema);
        Assert.Equal(definition.Unique, restored.Unique);
        Assert.Equal(definition.Columns, restored.Columns);
        Assert.Equal(definition.Descending, restored.Descending);
    }

    [Fact]
    public void SerializeAndDeserialize_RoundTripsExpectedColumnDefinition()
    {
        var definition = new ExpectedColumnDefinition(
            "created_at",
            "datetime",
            IsNullable: false,
            DefaultValueLiteral: "2020-01-01",
            DefaultValueSql: "CURRENT_TIMESTAMP",
            DefaultValueTypeName: "System.DateTime",
            DefaultValueJson: "\"2020-01-01T00:00:00\"",
            ComputedColumnSql: null,
            Precision: 6,
            Scale: null,
            Collation: null,
            IsStored: null);

        var json = SafeMigrationDefinitionSerializer.Serialize(definition);
        var restored = SafeMigrationDefinitionSerializer.Deserialize<ExpectedColumnDefinition>(json);

        Assert.NotNull(restored);
        Assert.Equal(definition.Name, restored.Name);
        Assert.Equal(definition.StoreType, restored.StoreType);
        Assert.Equal(definition.IsNullable, restored.IsNullable);
        Assert.Equal(definition.DefaultValueLiteral, restored.DefaultValueLiteral);
        Assert.Equal(definition.DefaultValueSql, restored.DefaultValueSql);
        Assert.Equal(definition.DefaultValueTypeName, restored.DefaultValueTypeName);
        Assert.Equal(definition.DefaultValueJson, restored.DefaultValueJson);
        Assert.Equal(definition.Precision, restored.Precision);
        Assert.Null(restored.Scale);
        Assert.Null(restored.Collation);
        Assert.Null(restored.IsStored);
    }

    [Fact]
    public void SerializeAndDeserialize_RoundTripsExpectedTableDefinition()
    {
        var column = new ExpectedColumnDefinition("id", "int", IsNullable: false);
        var pk = new ExpectedPrimaryKeyDefinition("PK_Orders", "Orders", "dbo", ["id"]);
        var definition = new ExpectedTableDefinition("Orders", "dbo", [column], pk);

        var json = SafeMigrationDefinitionSerializer.Serialize(definition);
        var restored = SafeMigrationDefinitionSerializer.Deserialize<ExpectedTableDefinition>(json);

        Assert.NotNull(restored);
        Assert.Equal(definition.Table, restored.Table);
        Assert.Equal(definition.Schema, restored.Schema);
        Assert.Single(restored.Columns);
        Assert.Equal("id", restored.Columns[0].Name);
        Assert.NotNull(restored.PrimaryKey);
        Assert.Equal("PK_Orders", restored.PrimaryKey.Name);
    }

    [Fact]
    public void SerializeAndDeserialize_RoundTripsExpectedTableDefinition_NullableOptionals()
    {
        var definition = new ExpectedTableDefinition("Logs", null, []);

        var json = SafeMigrationDefinitionSerializer.Serialize(definition);
        var restored = SafeMigrationDefinitionSerializer.Deserialize<ExpectedTableDefinition>(json);

        Assert.NotNull(restored);
        Assert.Null(restored.Schema);
        Assert.Empty(restored.Columns);
        Assert.Null(restored.PrimaryKey);
    }

    [Fact]
    public void SerializeAndDeserialize_RoundTripsExpectedForeignKeyDefinition()
    {
        var definition = new ExpectedForeignKeyDefinition(
            "FK_Orders_Customers",
            "Orders",
            "dbo",
            ["customer_id"],
            "Customers",
            "dbo",
            ["id"],
            ReferentialAction.NoAction,
            ReferentialAction.Cascade);

        var json = SafeMigrationDefinitionSerializer.Serialize(definition);
        var restored = SafeMigrationDefinitionSerializer.Deserialize<ExpectedForeignKeyDefinition>(json);

        Assert.NotNull(restored);
        Assert.Equal(definition.Name, restored.Name);
        Assert.Equal(definition.Table, restored.Table);
        Assert.Equal(definition.Schema, restored.Schema);
        Assert.Equal(definition.Columns, restored.Columns);
        Assert.Equal(definition.PrincipalTable, restored.PrincipalTable);
        Assert.Equal(definition.PrincipalSchema, restored.PrincipalSchema);
        Assert.Equal(definition.PrincipalColumns, restored.PrincipalColumns);
        Assert.Equal(definition.OnUpdate, restored.OnUpdate);
        Assert.Equal(definition.OnDelete, restored.OnDelete);
    }

    [Fact]
    public void SerializeAndDeserialize_RoundTripsExpectedCheckConstraintDefinition()
    {
        var definition = new ExpectedCheckConstraintDefinition("CK_Products_Price", "Products", null, "\"price\" > 0");

        var json = SafeMigrationDefinitionSerializer.Serialize(definition);
        var restored = SafeMigrationDefinitionSerializer.Deserialize<ExpectedCheckConstraintDefinition>(json);

        Assert.NotNull(restored);
        Assert.Equal(definition.Name, restored.Name);
        Assert.Equal(definition.Table, restored.Table);
        Assert.Null(restored.Schema);
        Assert.Equal(definition.Sql, restored.Sql);
    }

    [Fact]
    public void SerializeAndDeserialize_RoundTripsExpectedPrimaryKeyDefinition()
    {
        var definition = new ExpectedPrimaryKeyDefinition(
            "PK_Users",
            "Users",
            "auth",
            [
                "tenant_id",
                "user_id"
            ]);

        var json = SafeMigrationDefinitionSerializer.Serialize(definition);
        var restored = SafeMigrationDefinitionSerializer.Deserialize<ExpectedPrimaryKeyDefinition>(json);

        Assert.NotNull(restored);
        Assert.Equal(definition.Name, restored.Name);
        Assert.Equal(definition.Table, restored.Table);
        Assert.Equal(definition.Schema, restored.Schema);
        Assert.Equal(definition.Columns, restored.Columns);
    }

    [Fact]
    public void SerializeAndDeserialize_RoundTripsExpectedUniqueConstraintDefinition()
    {
        var definition = new ExpectedUniqueConstraintDefinition("AK_Users_Email", "Users", null, ["email"]);

        var json = SafeMigrationDefinitionSerializer.Serialize(definition);
        var restored = SafeMigrationDefinitionSerializer.Deserialize<ExpectedUniqueConstraintDefinition>(json);

        Assert.NotNull(restored);
        Assert.Equal(definition.Name, restored.Name);
        Assert.Equal(definition.Table, restored.Table);
        Assert.Null(restored.Schema);
        Assert.Equal(definition.Columns, restored.Columns);
    }
}
