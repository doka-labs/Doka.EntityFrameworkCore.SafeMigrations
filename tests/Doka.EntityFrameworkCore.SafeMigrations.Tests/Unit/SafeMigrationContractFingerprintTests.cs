namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationContractFingerprintTests
{
    private static void AssertDifferent(
        SafeMigrationIntent first,
        SafeMigrationIntent second
    ) => Assert.NotEqual(Fingerprint(first), Fingerprint(second));

    private static string Fingerprint(
        SafeMigrationIntent intent
    ) => SafeMigrationContractFingerprint.Create(Operations(intent));

    private static IReadOnlyList<MigrationOperation> Operations(
        SafeMigrationIntent intent
    ) => [new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent)];

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
        string? collation = "default",
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
        collation,
        comment,
        defaultValue
        ?? ((clrType ?? typeof(string)) == typeof(string)
            ? SafeMigrationDefaultValue.Literal("value")
            : SafeMigrationDefaultValue.None),
        computedColumnSql,
        isStored);

    private static ExpectedIndexDefinition Index(
        string name = "ix_items_value",
        string table = "items",
        string? schema = null,
        bool unique = true,
        string? filter = "value <> ''",
        IEnumerable<string>? includedColumns = null,
        string? method = "btree",
        bool? nullsDistinct = true,
        IEnumerable<ExpectedIndexKeyDefinition>? keys = null
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

    private static EnsureForeignKeyIntent ForeignKey(
        string principalTable = "parents",
        ReferentialAction onUpdate = ReferentialAction.NoAction,
        ReferentialAction onDelete = ReferentialAction.NoAction
    ) => new(
        new ExpectedForeignKeyDefinition(
            "fk_items_parent",
            "items",
            ["parent_id"],
            principalTable,
            ["id"],
            onUpdate: onUpdate,
            onDelete: onDelete));
}
