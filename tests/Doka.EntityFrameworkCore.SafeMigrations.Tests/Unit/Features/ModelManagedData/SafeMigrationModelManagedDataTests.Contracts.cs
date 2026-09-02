namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationModelManagedDataTests
{
    [Fact]
    public void DefinitionsSnapshotEveryMutableValueAndMetadataInput()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var keyColumns = new[] { "id" };
        var keyTypes = new[] { "int" };
        var columns = new[] { "id", "payload" };
        var columnTypes = new[] { "int", "varbinary(32)" };
        var uniqueColumns = new[] { "payload" };
        var uniqueKeys = new[] { new ExpectedModelManagedDataUniqueKeyDefinition(uniqueColumns), };
        var values = new object?[,] { { 1, bytes } };

        var intent = new EnsureModelManagedDataIntent(
            "roles",
            keyColumns,
            keyTypes,
            columns,
            columnTypes,
            values,
            "identity",
            uniqueKeys);

        keyColumns[0] = "changed_key";
        keyTypes[0] = "bigint";
        columns[1] = "changed_payload";
        columnTypes[1] = "longblob";
        uniqueColumns[0] = "changed_payload";
        bytes[0] = 9;
        ((byte[])values[0, 1]!)[1] = 9;

        var returnedBytes = Assert.IsType<byte[]>(intent.Values.GetValue(0, 1));
        returnedBytes[2] = 9;

        Assert.Equal(["id"], intent.KeyColumns);
        Assert.Equal(["int"], intent.KeyColumnTypes);
        Assert.Equal(["id", "payload"], intent.Columns);
        Assert.Equal(["int", "varbinary(32)"], intent.ColumnTypes);
        Assert.Equal(["payload"], Assert.Single(intent.UniqueKeys).Columns);
        Assert.Equal(new byte[] { 1, 2, 3 }, intent.Values.GetValue(0, 1));
    }

    [Fact]
    public void CanonicalValueContractSupportsProviderValuesAndRejectsUnknownObjects()
    {
        var guid = Guid.Parse("e720f9aa-4743-4c10-a40d-48265f281c0d");
        var values = new object?[,]
        {
            {
                1,
                null,
                true,
                (byte)2,
                (sbyte)-3,
                (short)-4,
                (ushort)5,
                6U,
                7L,
                8UL,
                9.10m,
                11.12f,
                13.14d,
                "text",
                'x',
                new byte[] { 15, 16 },
                guid,
                new DateOnly(2026, 9, 2),
                new TimeOnly(12, 34, 56),
                new DateTime(2026, 9, 2, 12, 34, 56, DateTimeKind.Utc),
                new DateTimeOffset(2026, 9, 2, 12, 34, 56, TimeSpan.FromHours(2)),
                TimeSpan.FromMinutes(17),
                ModelManagedDataTestValue.Second,
            },
        };

        var columns = Enumerable.Range(0, values.GetLength(1)).Select(index => $"c{index}").ToArray();
        var types = Enumerable.Repeat("provider_type", values.GetLength(1)).ToArray();

        var intent = new EnsureModelManagedDataIntent(
            "value_contract",
            ["c0"],
            ["provider_type"],
            columns,
            types,
            values,
            schema: null,
            uniqueKeys: null);

        var fingerprint = SafeMigrationContractFingerprint.Create([Operation(intent)]);

        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(guid, intent.Values.GetValue(0, 16));

        var unsupported = new object?[,] { { 1, new Uri("https://example.test/secret-value") } };

        var exception = Assert.Throws<ArgumentException>(() => new EnsureModelManagedDataIntent(
            "value_contract",
            ["id"],
            ["int"],
            ["id", "unsupported"],
            ["int", "text"],
            unsupported,
            schema: null,
            uniqueKeys: null));

        Assert.Contains(typeof(Uri).FullName!, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FingerprintBindsEveryModelManagedContractDimensionWithoutExposingValues()
    {
        var operations = new[]
        {
            EnsureOperation("identity", "roles", "int", 1, "varchar(64)", "private-administrator", reverseRows: false),
            EnsureOperation(
                "other", "roles", "int", 1, "varchar(64)", "private-administrator", reverseRows: false),
            EnsureOperation(
                "identity", "other_roles", "int", 1, "varchar(64)", "private-administrator", reverseRows: false),
            EnsureOperation(
                "identity", "roles", "bigint", 1L, "varchar(64)", "private-administrator", reverseRows: false),
            EnsureOperation("identity", "roles", "int", 3, "varchar(64)", "private-administrator", reverseRows: false),
            EnsureOperation("identity", "roles", "int", 1, "varchar(128)", "private-administrator", reverseRows: false),
            EnsureOperation("identity", "roles", "int", 1, "varchar(64)", "private-owner", reverseRows: false),
            EnsureOperation("identity", "roles", "int", 1, "varchar(64)", "private-administrator", reverseRows: true),
        };

        var fingerprints = operations
            .Select(operation => SafeMigrationContractFingerprint.Create([operation]))
            .ToArray();

        Assert.Equal(fingerprints.Length, fingerprints.Distinct(StringComparer.Ordinal).Count());
        Assert.All(fingerprints, fingerprint =>
        {
            Assert.DoesNotContain("private-administrator", fingerprint, StringComparison.Ordinal);
            Assert.DoesNotContain("private-owner", fingerprint, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void InvalidShapesFailClosedWithoutIncludingManagedValues()
    {
        var secret = "private-role-value";

        Assert.Throws<ArgumentException>(() => new EnsureModelManagedDataIntent(
            "roles",
            [],
            [],
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            schema: null,
            uniqueKeys: null));
        Assert.Throws<ArgumentException>(() => new EnsureModelManagedDataIntent(
            "roles",
            ["id"],
            ["bigint"],
            ["id", "name"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, secret } },
            schema: null,
            uniqueKeys: null));
        Assert.Throws<ArgumentException>(() => new UpdateModelManagedDataIntent(
            "roles",
            ["id"],
            ["int"],
            new object?[,] { { 1 }, { 2 } },
            ["name"],
            ["varchar(64)"],
            new object?[,] { { secret } },
            new object?[,] { { "target" }, { "target-2" } },
            schema: null,
            uniqueKeys: null));

        var duplicate = Operation(
            new EnsureModelManagedDataIntent(
                "roles",
                ["id"],
                ["int"],
                ["id", "name"],
                ["int", "varchar(64)"],
                new object?[,] { { 1, secret } },
                schema: null,
                uniqueKeys: null));

        var duplicateException = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationModelManagedDataContractValidator.Validate([duplicate, duplicate]));

        Assert.DoesNotContain(secret, duplicateException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PairerPartitionsAtRowAndCellBoundsAndRejectsAnOversizedRow()
    {
        const int rowCount = 257;
        var values = new object?[rowCount, 2];
        var inverseKeys = new object?[rowCount, 1];
        for (var row = 0; row < rowCount; row++)
        {
            values[row, 0] = row;
            values[row, 1] = $"role-{row.ToString(CultureInfo.InvariantCulture)}";
            inverseKeys[row, 0] = row;
        }

        var rowBoundResult = SafeMigrationModelManagedDataPairer.Pair(
            [Insert("roles", ["id", "name"], ["int", "varchar(64)"], values)],
            [Delete("roles", ["id"], ["int"], inverseKeys)]);

        Assert.Equal([128, 128, 1], rowBoundResult
            .Cast<EnsureModelManagedDataScaffoldingOperation>()
            .Select(operation => operation.Intent.RowCount));

        const int cellBoundRows = 200;
        const int managedColumnCount = 40;
        var cellColumns = Enumerable.Range(0, managedColumnCount).Select(index => $"c{index}").ToArray();
        var cellTypes = Enumerable.Repeat("int", managedColumnCount).ToArray();
        var cellValues = new object?[cellBoundRows, managedColumnCount];
        var cellKeys = new object?[cellBoundRows, 1];
        for (var row = 0; row < cellBoundRows; row++)
        {
            cellKeys[row, 0] = row;
            for (var column = 0; column < managedColumnCount; column++)
            {
                cellValues[row, column] = column == 0 ? row : column;
            }
        }

        var cellBoundResult = SafeMigrationModelManagedDataPairer.Pair(
            [Insert("cell_bound", cellColumns, cellTypes, cellValues)],
            [Delete("cell_bound", ["c0"], ["int"], cellKeys)]);

        Assert.Equal([99, 99, 2], cellBoundResult
            .Cast<EnsureModelManagedDataScaffoldingOperation>()
            .Select(operation => operation.Intent.RowCount));

        var oversizedColumnCount = SafeMigrationModelManagedDataLimits.MaximumCellsPerOperation;
        var oversizedColumns = Enumerable.Range(0, oversizedColumnCount).Select(index => $"c{index}").ToArray();
        var oversizedTypes = Enumerable.Repeat("int", oversizedColumnCount).ToArray();
        var oversizedValues = new object?[1, oversizedColumnCount];
        oversizedValues[0, 0] = 0;

        Assert.Throws<InvalidOperationException>(() => SafeMigrationModelManagedDataPairer.Pair(
            [Insert("oversized", oversizedColumns, oversizedTypes, oversizedValues)],
            [Delete("oversized", ["c0"], ["int"], new object?[,] { { 0 } })]));
    }

    [Fact]
    public void ScaffoldingCarriersPopulateTheInheritedEfOperationContract()
    {
        var ensureIntent = new EnsureModelManagedDataIntent(
            "roles",
            ["id"],
            ["int"],
            ["id", "name"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, "administrator" } },
            "identity",
            uniqueKeys: null);

        var updateIntent = new UpdateModelManagedDataIntent(
            "roles",
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            ["name"],
            ["varchar(64)"],
            new object?[,] { { "administrator" } },
            new object?[,] { { "owner" } },
            "identity",
            uniqueKeys: null);

        var deleteIntent = new DeleteModelManagedDataIntent(
            "roles",
            ["id"],
            ["int"],
            new object?[,] { { 1 } },
            ["id", "name"],
            ["int", "varchar(64)"],
            new object?[,] { { 1, "administrator" } },
            "identity",
            foreignKeys: null);

        var ensure = new EnsureModelManagedDataScaffoldingOperation(ensureIntent);
        var update = new UpdateModelManagedDataScaffoldingOperation(updateIntent);
        var delete = new DeleteModelManagedDataScaffoldingOperation(deleteIntent);

        Assert.Equal("roles", ensure.Table);
        Assert.Equal(["int", "varchar(64)"], ensure.ColumnTypes!);
        Assert.Equal("owner", update.Values[0, 0]);
        Assert.Equal(["int"], update.KeyColumnTypes!);
        Assert.Equal(1, delete.KeyValues[0, 0]);
        Assert.Equal(["int"], delete.KeyColumnTypes!);
    }

    private static SafeMigrationOperation EnsureOperation(
        string? schema,
        string table,
        string keyType,
        object key,
        string valueType,
        string value,
        bool reverseRows
    )
    {
        var rows = reverseRows
            ? new object?[,] { { 2, "second" }, { key, value } }
            : new object?[,] { { key, value }, { 2, "second" } };

        return Operation(
            new EnsureModelManagedDataIntent(
                table,
                ["id"],
                [keyType],
                ["id", "name"],
                [keyType, valueType],
                rows,
                schema,
                uniqueKeys: null));
    }

    private static InsertDataOperation Insert(
        string table,
        string[] columns,
        string[] columnTypes,
        object?[,] values
    ) => new()
    {
        Table = table,
        Columns = columns,
        ColumnTypes = columnTypes,
        Values = values,
    };

    private static DeleteDataOperation Delete(
        string table,
        string[] keyColumns,
        string[] keyColumnTypes,
        object?[,] keyValues
    ) => new()
    {
        Table = table,
        KeyColumns = keyColumns,
        KeyColumnTypes = keyColumnTypes,
        KeyValues = keyValues,
    };

    private enum ModelManagedDataTestValue
    {
        First = 1,
        Second = 2,
    }
}
