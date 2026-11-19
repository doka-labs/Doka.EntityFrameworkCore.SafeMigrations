namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Checks exact definition and ordered-member equality, including schema roles, referential actions and binary
/// contents.
/// </summary>
public sealed class SafeMigrationDefinitionEquivalenceTests
{
    /// <summary>Compares each independent column facet without merging literal or expression identity.</summary>
    /// <param name="variant">The changed facet ordinal, or minus one for an exact matching shape.</param>
    /// <param name="expected">Whether the independently constructed definition is equivalent.</param>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    [InlineData(12, false)]
    [InlineData(13, false)]
    [InlineData(14, false)]
    public void ColumnEquivalence_BindsEveryFieldAndBinaryContent(
        int variant,
        bool expected
    )
    {
        // Arrange
        var baseline = Column();
        var variants = new[]
        {
            Column(name: "other"),
            Column(clrType: typeof(int), defaultValue: SafeMigrationDefaultValue.Literal(1)),
            Column(isNullable: false),
            Column(storeType: "varchar(41)"),
            Column(isUnicode: false),
            Column(maxLength: 41),
            Column(isFixedLength: true),
            Column(isRowVersion: true),
            Column(precision: 11),
            Column(scale: 3),
            Column(collation: "other_collation"),
            Column(comment: "other"),
            Column(defaultValue: SafeMigrationDefaultValue.Literal("other")),
            Column(defaultValue: SafeMigrationDefaultValue.Sql("CURRENT_TIMESTAMP")),
            Column(defaultValue: SafeMigrationDefaultValue.None, computedColumnSql: "1 + 1", isStored: true),
        };

        var compared = variant < 0
            ? Column()
            : variants[variant];

        // Act
        var equivalent = SafeMigrationDefinitionEquivalence.Column(baseline, compared);

        // Assert
        Assert.Equal(expected, equivalent);
    }

    /// <summary>Compares binary literal contents rather than backing-array reference identity.</summary>
    /// <param name="lastByte">The independently changed final literal byte.</param>
    /// <param name="expected">Whether every binary literal byte matches.</param>
    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void BinaryColumnEquivalenceBindsEveryLiteralByte(
        byte lastByte,
        bool expected
    )
    {
        // Arrange
        var baseline = Column(
            clrType: typeof(byte[]),
            storeType: "varbinary(3)",
            defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 1, 2, 3 }));

        var compared = Column(
            clrType: typeof(byte[]),
            storeType: "varbinary(3)",
            defaultValue: SafeMigrationDefaultValue.Literal(new byte[] { 1, 2, lastByte }));

        // Act
        var equivalent = SafeMigrationDefinitionEquivalence.Column(baseline, compared);

        // Assert
        Assert.Equal(expected, equivalent);
    }

    /// <summary>Compares each independent index and ordered-key facet.</summary>
    /// <param name="variant">The changed facet ordinal, or minus one for an exact matching shape.</param>
    /// <param name="expected">Whether the complete index definitions are equivalent.</param>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(10, false)]
    [InlineData(11, false)]
    [InlineData(12, false)]
    [InlineData(13, false)]
    public void IndexEquivalence_BindsEveryDefinitionAndKeyField(
        int variant,
        bool expected
    )
    {
        // Arrange
        var baseline = Index();
        var variants = new[]
        {
            Index(name: "ix_other"),
            Index(table: "other"),
            Index(schema: "app"),
            Index(unique: false, nullsDistinct: null),
            Index(filter: "value IS NOT NULL"),
            Index(includedColumns: ["payload", "other"]),
            Index(method: "hash"),
            Index(nullsDistinct: false),
            Index(keys: [new ExpectedIndexKeyDefinition(column: "other")]),
            Index(keys: [new ExpectedIndexKeyDefinition(expression: "lower(value)")]),
            Index(
                keys:
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        sortOrder: SafeMigrationIndexSortOrder.Descending),
                ]),
            Index(keys: [new ExpectedIndexKeyDefinition(column: "value", prefixLength: 8)]),
            Index(
                keys:
                [
                    new ExpectedIndexKeyDefinition(
                        column: "value",
                        collation: new SafeMigrationCollationIdentifier("C")),
                ]),
            Index(keys: [new ExpectedIndexKeyDefinition(column: "value", operatorClass: "text_pattern_ops")]),
        };

        var compared = variant < 0
            ? Index()
            : variants[variant];

        // Act
        var equivalent = SafeMigrationDefinitionEquivalence.Index(baseline, compared);

        // Assert
        Assert.Equal(expected, equivalent);
    }

    /// <summary>Compares primary-key names, owners, schemas and ordered member identities.</summary>
    /// <param name="variant">The changed facet ordinal, or minus one for an exact matching shape.</param>
    /// <param name="expected">Whether the complete primary-key definitions are equivalent.</param>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void ConstraintEquivalence_BindsEveryDefinitionField(
        int variant,
        bool expected
    )
    {
        // Arrange
        var baseline = new ExpectedPrimaryKeyDefinition("pk_items", "items", ["tenant_id", "id"], "app");
        ExpectedPrimaryKeyDefinition[] variants =
        [
            new("pk_other", "items", ["tenant_id", "id"], "app"),
            new("pk_items", "other", ["tenant_id", "id"], "app"),
            new("pk_items", "items", ["id", "tenant_id"], "app"),
            new("pk_items", "items", ["tenant_id", "id"], "other"),
        ];

        var compared = variant < 0
            ? new ExpectedPrimaryKeyDefinition("pk_items", "items", ["tenant_id", "id"], "app")
            : variants[variant];

        // Act
        var equivalent = SafeMigrationDefinitionEquivalence.PrimaryKey(baseline, compared);

        // Assert
        Assert.Equal(expected, equivalent);
    }

    /// <summary>Compares unique-key names, owners, schemas and ordered member identities.</summary>
    /// <param name="variant">The changed facet ordinal, or minus one for an exact matching shape.</param>
    /// <param name="expected">Whether the complete unique-key definitions are equivalent.</param>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void UniqueConstraintEquivalenceBindsEveryDefinitionField(
        int variant,
        bool expected
    )
    {
        // Arrange
        var baseline = new ExpectedUniqueConstraintDefinition("uq_items", "items", ["tenant_id", "code"], "app");
        ExpectedUniqueConstraintDefinition[] variants =
        [
            new("uq_other", "items", ["tenant_id", "code"], "app"),
            new("uq_items", "other", ["tenant_id", "code"], "app"),
            new("uq_items", "items", ["code", "tenant_id"], "app"),
            new("uq_items", "items", ["tenant_id", "code"], "other"),
        ];

        var compared = variant < 0
            ? new ExpectedUniqueConstraintDefinition("uq_items", "items", ["tenant_id", "code"], "app")
            : variants[variant];

        // Act
        var equivalent = SafeMigrationDefinitionEquivalence.UniqueConstraint(baseline, compared);

        // Assert
        Assert.Equal(expected, equivalent);
    }

    /// <summary>Compares CHECK names, owners, schemas and predicate identity.</summary>
    /// <param name="variant">The changed facet ordinal, or minus one for an exact matching shape.</param>
    /// <param name="expected">Whether the complete CHECK definitions are equivalent.</param>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void CheckConstraintEquivalenceBindsEveryDefinitionField(
        int variant,
        bool expected
    )
    {
        // Arrange
        var baseline = new ExpectedCheckConstraintDefinition("ck_items", "items", "code <> ''", "app");
        ExpectedCheckConstraintDefinition[] variants =
        [
            new("ck_other", "items", "code <> ''", "app"),
            new("ck_items", "other", "code <> ''", "app"),
            new("ck_items", "items", "code IS NOT NULL", "app"),
            new("ck_items", "items", "code <> ''", "other"),
        ];

        var compared = variant < 0
            ? new ExpectedCheckConstraintDefinition("ck_items", "items", "code <> ''", "app")
            : variants[variant];

        // Act
        var equivalent = SafeMigrationDefinitionEquivalence.CheckConstraint(baseline, compared);

        // Assert
        Assert.Equal(expected, equivalent);
    }

    /// <summary>Compares both FK endpoints, ordered member roles and referential actions.</summary>
    /// <param name="variant">The changed facet ordinal, or minus one for an exact matching shape.</param>
    /// <param name="expected">Whether the complete FK definitions are equivalent.</param>
    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    public void ForeignKeyEquivalenceBindsEveryDefinitionField(
        int variant,
        bool expected
    )
    {
        // Arrange
        var baseline = ForeignKey();
        ExpectedForeignKeyDefinition[] variants =
        [
            ForeignKey(name: "fk_other"),
            ForeignKey(table: "other"),
            ForeignKey(schema: "other"),
            ForeignKey(columns: ["parent_id", "tenant_id"]),
            ForeignKey(principalTable: "other"),
            ForeignKey(principalSchema: "other"),
            ForeignKey(principalColumns: ["id", "tenant_id"]),
            ForeignKey(onUpdate: ReferentialAction.NoAction),
            ForeignKey(onDelete: ReferentialAction.NoAction),
        ];

        var compared = variant < 0
            ? ForeignKey()
            : variants[variant];

        // Act
        var equivalent = SafeMigrationDefinitionEquivalence.ForeignKey(baseline, compared);

        // Assert
        Assert.Equal(expected, equivalent);
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
        collation is null
            ? null
            : new SafeMigrationCollationIdentifier(collation),
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
