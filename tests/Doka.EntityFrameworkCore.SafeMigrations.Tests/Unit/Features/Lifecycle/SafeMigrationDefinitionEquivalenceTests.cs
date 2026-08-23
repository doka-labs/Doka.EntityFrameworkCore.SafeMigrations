namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationDefinitionEquivalenceTests
{
    [Fact]
    public void ColumnEquivalence_BindsEveryFieldAndBinaryContent()
    {
        var baseline = Column();
        var variants = new[]
        {
            Column(name: "other"), Column(clrType: typeof(int), defaultValue: SafeMigrationDefaultValue.Literal(1)),
            Column(isNullable: false), Column(storeType: "varchar(41)"), Column(isUnicode: false),
            Column(maxLength: 41), Column(isFixedLength: true), Column(isRowVersion: true), Column(precision: 11),
            Column(scale: 3), Column(collation: "other_collation"), Column(comment: "other"),
            Column(defaultValue: SafeMigrationDefaultValue.Literal("other")),
            Column(defaultValue: SafeMigrationDefaultValue.Sql("CURRENT_TIMESTAMP")),
            Column(defaultValue: SafeMigrationDefaultValue.None, computedColumnSql: "1 + 1", isStored: true),
        };

        Assert.True(SafeMigrationDefinitionEquivalence.Column(baseline, Column()));
        Assert.All(variants, variant => Assert.False(SafeMigrationDefinitionEquivalence.Column(baseline, variant)));
        Assert.True(
            SafeMigrationDefinitionEquivalence.Column(
                Column(
                    clrType: typeof(byte[]),
                    storeType: "varbinary(3)",
                    defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 1, 2, 3 })),
                Column(
                    clrType: typeof(byte[]),
                    storeType: "varbinary(3)",
                    defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 1, 2, 3 }))));
        Assert.False(
            SafeMigrationDefinitionEquivalence.Column(
                Column(
                    clrType: typeof(byte[]),
                    storeType: "varbinary(3)",
                    defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 1, 2, 3 })),
                Column(
                    clrType: typeof(byte[]),
                    storeType: "varbinary(3)",
                    defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 1, 2, 4 }))));
    }

    [Fact]
    public void IndexEquivalence_BindsEveryDefinitionAndKeyField()
    {
        var baseline = Index();
        var variants = new[]
        {
            Index(name: "ix_other"), Index(table: "other"), Index(schema: "app"),
            Index(unique: false, nullsDistinct: null), Index(filter: "value IS NOT NULL"),
            Index(includedColumns: ["payload", "other"]), Index(method: "hash"), Index(nullsDistinct: false),
            Index(keys: [new ExpectedIndexKeyDefinition(column: "other")]),
            Index(keys: [new ExpectedIndexKeyDefinition(expression: "lower(value)")]),
            Index(
                keys:
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        sortOrder: SafeMigrationIndexSortOrder.Descending)
                ]),
            Index(keys: [new ExpectedIndexKeyDefinition(column: "value", prefixLength: 8)]),
            Index(
                keys:
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        collation: new SafeMigrationCollationIdentifier("C"))
                ]),
            Index(keys: [new ExpectedIndexKeyDefinition(column: "value", operatorClass: "text_pattern_ops")]),
        };

        Assert.True(SafeMigrationDefinitionEquivalence.Index(baseline, Index()));
        Assert.All(variants, variant => Assert.False(SafeMigrationDefinitionEquivalence.Index(baseline, variant)));
    }

    [Fact]
    public void ConstraintEquivalence_BindsEveryDefinitionField()
    {
        var primaryKey = new ExpectedPrimaryKeyDefinition("pk_items", "items", ["tenant_id", "id"], "app");
        var unique = new ExpectedUniqueConstraintDefinition("uq_items", "items", ["tenant_id", "code"], "app");
        var check = new ExpectedCheckConstraintDefinition("ck_items", "items", "code <> ''", "app");
        var foreignKey = new ExpectedForeignKeyDefinition(
            "fk_items_parent",
            "items",
            ["tenant_id", "parent_id"],
            "parents",
            ["tenant_id", "id"],
            "app",
            "canonical",
            ReferentialAction.Cascade,
            ReferentialAction.Restrict);

        Assert.True(
            SafeMigrationDefinitionEquivalence.PrimaryKey(
                primaryKey,
                new ExpectedPrimaryKeyDefinition("pk_items", "items", ["tenant_id", "id"], "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.PrimaryKey(
                primaryKey,
                new ExpectedPrimaryKeyDefinition("pk_other", "items", ["tenant_id", "id"], "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.PrimaryKey(
                primaryKey,
                new ExpectedPrimaryKeyDefinition("pk_items", "other", ["tenant_id", "id"], "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.PrimaryKey(
                primaryKey,
                new ExpectedPrimaryKeyDefinition("pk_items", "items", ["id", "tenant_id"], "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.PrimaryKey(
                primaryKey,
                new ExpectedPrimaryKeyDefinition("pk_items", "items", ["tenant_id", "id"], "other")));

        Assert.True(
            SafeMigrationDefinitionEquivalence.UniqueConstraint(
                unique,
                new ExpectedUniqueConstraintDefinition("uq_items", "items", ["tenant_id", "code"], "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.UniqueConstraint(
                unique,
                new ExpectedUniqueConstraintDefinition("uq_other", "items", ["tenant_id", "code"], "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.UniqueConstraint(
                unique,
                new ExpectedUniqueConstraintDefinition("uq_items", "other", ["tenant_id", "code"], "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.UniqueConstraint(
                unique,
                new ExpectedUniqueConstraintDefinition("uq_items", "items", ["code", "tenant_id"], "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.UniqueConstraint(
                unique,
                new ExpectedUniqueConstraintDefinition("uq_items", "items", ["tenant_id", "code"], "other")));

        Assert.True(
            SafeMigrationDefinitionEquivalence.CheckConstraint(
                check,
                new ExpectedCheckConstraintDefinition("ck_items", "items", "code <> ''", "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.CheckConstraint(
                check,
                new ExpectedCheckConstraintDefinition("ck_other", "items", "code <> ''", "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.CheckConstraint(
                check,
                new ExpectedCheckConstraintDefinition("ck_items", "other", "code <> ''", "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.CheckConstraint(
                check,
                new ExpectedCheckConstraintDefinition("ck_items", "items", "code IS NOT NULL", "app")));
        Assert.False(
            SafeMigrationDefinitionEquivalence.CheckConstraint(
                check,
                new ExpectedCheckConstraintDefinition("ck_items", "items", "code <> ''", "other")));

        Assert.True(SafeMigrationDefinitionEquivalence.ForeignKey(foreignKey, ForeignKey()));
        Assert.All(
            new[]
            {
                ForeignKey(name: "fk_other"), ForeignKey(table: "other"), ForeignKey(schema: "other"),
                ForeignKey(columns: ["parent_id", "tenant_id"]), ForeignKey(principalTable: "other"),
                ForeignKey(principalSchema: "other"), ForeignKey(principalColumns: ["id", "tenant_id"]),
                ForeignKey(onUpdate: ReferentialAction.NoAction), ForeignKey(onDelete: ReferentialAction.NoAction),
            },
            variant => Assert.False(SafeMigrationDefinitionEquivalence.ForeignKey(foreignKey, variant)));
    }

    private static ExpectedColumnDefinition Column(
        string name = "value",
        Type? clrType = null,
        bool isNullable = true,
        string? storeType = "varchar(40)",
        bool? isUnicode = true,
        int? maxLength = 40,
        bool? isFixedLength = false,
        bool isRowVersion = false,
        int? precision = 10,
        int? scale = 2,
        string? collation = "canonical_collation",
        string? comment = "canonical",
        SafeMigrationDefaultValue? defaultValue = null,
        string? computedColumnSql = null,
        bool? isStored = null
    ) => new(
        name,
        clrType ?? typeof(string),
        isNullable,
        storeType,
        isUnicode,
        maxLength,
        isFixedLength,
        isRowVersion,
        precision,
        scale,
        collation is null ? null : new SafeMigrationCollationIdentifier(collation),
        comment,
        defaultValue ?? SafeMigrationDefaultValue.Literal("canonical"),
        computedColumnSql,
        isStored);

    private static ExpectedIndexDefinition Index(
        string name = "ix_items_value",
        string table = "items",
        string? schema = null,
        IReadOnlyList<ExpectedIndexKeyDefinition>? keys = null,
        bool unique = true,
        string? filter = null,
        IReadOnlyList<string>? includedColumns = null,
        string? method = "btree",
        bool? nullsDistinct = true
    ) => new(
        name,
        table,
        keys ?? [new ExpectedIndexKeyDefinition(column: "value")],
        schema,
        unique,
        filter,
        includedColumns ?? ["payload"],
        method,
        nullsDistinct);

    private static ExpectedForeignKeyDefinition ForeignKey(
        string name = "fk_items_parent",
        string table = "items",
        string? schema = "app",
        IReadOnlyList<string>? columns = null,
        string principalTable = "parents",
        string? principalSchema = "canonical",
        IReadOnlyList<string>? principalColumns = null,
        ReferentialAction onUpdate = ReferentialAction.Cascade,
        ReferentialAction onDelete = ReferentialAction.Restrict
    ) => new(
        name,
        table,
        columns ?? ["tenant_id", "parent_id"],
        principalTable,
        principalColumns ?? ["tenant_id", "id"],
        schema,
        principalSchema,
        onUpdate,
        onDelete);
}
