namespace Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests;

public sealed partial class SqliteSafeMigrationCatalogIntegrationTests
{
    /// <summary>Snapshot commands are constant for empty, unindexed, and heavily indexed main schemas.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 7)]
    public async Task CatalogStreaming_UsesFiveCommandsRegardlessOfTableAndIndexCount(
        int tableCount,
        int indexesPerTable
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        for (var table = 0; table < tableCount; table++)
        {
            await ExecuteSqlAsync(connection, $"CREATE TABLE counted_{table} (Id INTEGER, Code TEXT);");
            for (var index = 0; index < indexesPerTable; index++)
            {
                await ExecuteSqlAsync(connection, $"CREATE INDEX ix_counted_{table}_{index} ON counted_{table}(Code);");
            }
        }

        using var counter = new SnapshotCountingConnection(connection);

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(counter, transaction: null);

        // Assert
        Assert.Equal(tableCount, snapshot.Tables.Count);
        Assert.Equal(tableCount * indexesPerTable, snapshot.Tables.Values.Sum(table => table.Indexes.Count));
        Assert.Equal(5, counter.ExecutionCount);
        Assert.Equal(5, counter.DisposedCommandCount);
        Assert.All(counter.Readers, reader => Assert.True(reader.IsClosed));
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>The combined settings query preserves live engine and connection settings.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task CatalogStreaming_PreservesConnectionSettings(
        int foreignKeys,
        int legacyAlterTable
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            $"PRAGMA foreign_keys = {foreignKeys}; PRAGMA legacy_alter_table = {legacyAlterTable};");
        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT sqlite_version();";
        var version = versionCommand.ExecuteScalar();

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        Assert.Equal(version, snapshot.Version);
        Assert.Equal(foreignKeys != 0, snapshot.ForeignKeysEnabled);
        Assert.Equal(legacyAlterTable != 0, snapshot.LegacyAlterTableEnabled);
    }

    /// <summary>Set-based metadata matches independent per-object PRAGMAs and preserves parsed SQL facets.</summary>
    [Fact]
    public async Task CatalogStreaming_MatchesLegacyPragmasForCompleteMetadata()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        const string parent = "p\"ar\u00e9nt";
        const string child = "child\u00f6";
        await ExecuteSqlAsync(
            connection,
            $"CREATE TABLE {QuoteCatalogIdentifier(parent)} (A TEXT COLLATE NOCASE NOT NULL, B INTEGER NOT NULL, "
            + "Payload TEXT DEFAULT '/*literal*/', CONSTRAINT \"pk parent\" PRIMARY KEY(A DESC, B), "
            + "CONSTRAINT uq_parent UNIQUE(B, A), CONSTRAINT ck_parent CHECK(length(A) > 0)) "
            + "/* table option */ WITHOUT ROWID, STRICT; "
            + $"CREATE TABLE {QuoteCatalogIdentifier(child)} (Id INTEGER CONSTRAINT \"pk child\" "
            + "PRIMARY KEY AUTOINCREMENT, Code TEXT COLLATE RTRIM NOT NULL DEFAULT '--literal', "
            + "ParentA TEXT, ParentB INTEGER, Generated TEXT GENERATED ALWAYS AS (Code || '/*literal*/') VIRTUAL, "
            + "Stored INTEGER GENERATED ALWAYS AS (length(Code)) STORED, "
            + "CONSTRAINT uq_child UNIQUE(Code) ON CONFLICT IGNORE, "
            + "CONSTRAINT \"fk implicit\" FOREIGN KEY(ParentA, ParentB) "
            + $"REFERENCES {QuoteCatalogIdentifier(parent)} ON UPDATE CASCADE ON DELETE SET NULL, "
            + "CONSTRAINT \"fk explicit\" FOREIGN KEY(ParentB, ParentA) "
            + $"REFERENCES {QuoteCatalogIdentifier(parent)}(B, A) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED); "
            + $"CREATE INDEX \"ix expression\" ON {QuoteCatalogIdentifier(child)} "
            + "(lower(Code) /* key trivia */ COLLATE NOCASE DESC, ParentB ASC) "
            + "WHERE ParentB IS NOT NULL /* filter trivia */; "
            + $"CREATE INDEX \"ix inherited\" ON {QuoteCatalogIdentifier(child)} (Code); "
            + $"CREATE VIEW \"v quoted\" AS SELECT Code FROM {QuoteCatalogIdentifier(child)}; "
            + $"CREATE TRIGGER \"tr quoted\" AFTER INSERT ON {QuoteCatalogIdentifier(child)} BEGIN SELECT 1; END;");

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        Assert.Equal(2, snapshot.Tables.Count);
        Assert.All(snapshot.Tables.Values, table => AssertLegacyCatalogParity(connection, table));
        var parentTable = snapshot.Tables[parent];
        var childTable = snapshot.Tables[child];
        Assert.True(parentTable.IsStrict);
        Assert.True(parentTable.WithoutRowId);
        Assert.Equal("pk parent", parentTable.PrimaryKeyName);
        Assert.Collection(
            parentTable.PrimaryKeyColumns,
            column => Assert.Equal("A", column),
            column => Assert.Equal("B", column));
        Assert.True(parentTable.PrimaryKeyKeys[0].Descending);
        Assert.Equal("NOCASE", parentTable.PrimaryKeyKeys[0].Collation);
        Assert.Equal("uq_parent", Assert.Single(parentTable.UniqueConstraints).Name);
        Assert.Equal("ck_parent", Assert.Single(parentTable.Checks).Name);
        Assert.True(childTable.Columns["Id"].AutoIncrement);
        Assert.Equal("pk child", childTable.PrimaryKeyName);
        Assert.Equal("RTRIM", childTable.Columns["Code"].Collation);
        Assert.Equal("'--literal'", childTable.Columns["Code"].DefaultSql);
        Assert.Equal("Code || '/*literal*/'", childTable.Columns["Generated"].GeneratedSql);
        Assert.Equal(2, childTable.Columns["Generated"].HiddenKind);
        Assert.False(childTable.Columns["Generated"].IsStored);
        Assert.Equal("length(Code)", childTable.Columns["Stored"].GeneratedSql);
        Assert.Equal(3, childTable.Columns["Stored"].HiddenKind);
        Assert.True(childTable.Columns["Stored"].IsStored);
        Assert.True(childTable.HasUnmodeledConstraintOptions);
        var expressionIndex = Assert.Single(childTable.Indexes, index => index.Name == "ix expression");
        Assert.Equal("lower(Code)", expressionIndex.Keys[0].Expression);
        Assert.True(expressionIndex.Keys[0].Descending);
        Assert.Equal("NOCASE", expressionIndex.Keys[0].Collation);
        Assert.True(expressionIndex.Partial);
        Assert.Equal("ParentB IS NOT NULL", expressionIndex.Filter);
        Assert.Equal(
            "RTRIM",
            Assert.Single(childTable.Indexes, index => index.Name == "ix inherited").Keys[0].Collation);
        var implicitForeignKey = Assert.Single(childTable.ForeignKeys, key => key.Name == "fk implicit");
        Assert.Collection(
            implicitForeignKey.Columns,
            column => Assert.Equal("ParentA", column),
            column => Assert.Equal("ParentB", column));
        Assert.Collection(
            implicitForeignKey.PrincipalColumns,
            column => Assert.Equal(string.Empty, column),
            column => Assert.Equal(string.Empty, column));
        Assert.True(SqliteCatalogEquivalence.ForeignKeyMatches(
            snapshot, implicitForeignKey, parent, ["ParentA", "ParentB"], ["A", "B"],
            ReferentialAction.Cascade, ReferentialAction.SetNull));
        Assert.Collection(
            Assert.Single(childTable.ForeignKeys, key => key.Name == "fk explicit").PrincipalColumns,
            column => Assert.Equal("B", column),
            column => Assert.Equal("A", column));
        Assert.Contains(snapshot.OtherObjects, value => value.Type == "view" && value.Name == "v quoted");
        Assert.Contains(snapshot.OtherObjects, value => value.Type == "trigger" && value.Name == "tr quoted");
    }

    /// <summary>Virtual-table hidden columns remain in the snapshot rather than being dropped by table_info.</summary>
    [Fact]
    public async Task CatalogStreaming_PreservesVirtualTableHiddenColumns()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE VIRTUAL TABLE search_items USING fts5(Value);");

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        var table = snapshot.Tables["search_items"];
        AssertLegacyCatalogParity(connection, table);
        Assert.Equal(1, table.Columns["search_items"].HiddenKind);
        Assert.Equal(1, table.Columns["rank"].HiddenKind);
        Assert.Equal(0, table.Columns["Value"].HiddenKind);
    }

    /// <summary>Virtual-module arguments are not ordinary table constraint declarations.</summary>
    [Theory]
    [InlineData("references")]
    [InlineData("foreign")]
    [InlineData("constraint")]
    [InlineData("references, foreign, constraint")]
    public async Task CatalogStreaming_AcceptsVirtualModuleConstraintKeywords(
        string arguments
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, $"CREATE VIRTUAL TABLE keyword_module USING fts5({arguments});");
        using var counter = new SnapshotCountingConnection(connection);

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(counter, transaction: null);

        // Assert
        var table = snapshot.Tables["keyword_module"];
        AssertLegacyCatalogParity(connection, table);
        Assert.All(snapshot.Tables.Values, value => Assert.Empty(value.ForeignKeys));
        Assert.Equal(1, table.Columns["keyword_module"].HiddenKind);
        Assert.Equal(1, table.Columns["rank"].HiddenKind);
        Assert.Equal(5, counter.ExecutionCount);
    }

    /// <summary>Every joined metadata function ignores same-named temporary and attached objects.</summary>
    [Fact]
    public async Task CatalogStreaming_IsolatesMainFromTempAndAttachedSchemas()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "ATTACH DATABASE ':memory:' AS other; "
            + "CREATE TABLE main.isolated (MainColumn TEXT COLLATE NOCASE); "
            + "CREATE INDEX main.shared_index ON isolated(MainColumn); "
            + "CREATE TEMP TABLE isolated (TempColumn INTEGER REFERENCES missing(Id)); "
            + "CREATE INDEX temp.shared_index ON isolated(TempColumn); "
            + "CREATE TABLE other.isolated (AttachedColumn INTEGER); "
            + "CREATE INDEX other.shared_index ON isolated(AttachedColumn); "
            + "CREATE TABLE other.attached_only (Id INTEGER); "
            + "CREATE TEMP TABLE temporary_only (Id INTEGER);");

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(connection, transaction: null);

        // Assert
        var table = Assert.Single(snapshot.Tables).Value;
        Assert.Equal("isolated", table.Name);
        Assert.Equal("MainColumn", Assert.Single(table.Columns).Key);
        var index = Assert.Single(table.Indexes);
        Assert.Equal("shared_index", index.Name);
        Assert.Equal("MainColumn", index.Keys[0].Column);
        Assert.Equal("NOCASE", index.Keys[0].Collation);
        Assert.Empty(table.ForeignKeys);
        Assert.DoesNotContain(
            snapshot.OtherObjects,
            value => value.Sql.Contains("TempColumn", StringComparison.Ordinal));
        Assert.DoesNotContain(
            snapshot.OtherObjects,
            value => value.Sql.Contains("AttachedColumn", StringComparison.Ordinal));
    }

    /// <summary>Captures use their supplied transaction and never retain metadata across invocations.</summary>
    [Fact]
    public async Task CatalogStreaming_PreservesTransactionLifetimeAndFreshness()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE changing (Id INTEGER);");
        using var transaction = connection.BeginTransaction();
        using var counter = new SnapshotCountingConnection(connection);
        var first = SqliteSafeMigrationCatalog.Read(counter, transaction);
        using var change = connection.CreateCommand();
        change.Transaction = transaction;
        change.CommandText = "ALTER TABLE changing ADD COLUMN Later TEXT; CREATE INDEX ix_later ON changing(Later);";
        _ = change.ExecuteNonQuery();

        // Act
        var second = SqliteSafeMigrationCatalog.Read(counter, transaction);

        // Assert
        Assert.Equal(10, counter.ExecutionCount);
        Assert.Equal(10, counter.DisposedCommandCount);
        Assert.All(counter.Transactions, actual => Assert.Same(transaction, actual));
        Assert.All(counter.Readers, reader => Assert.True(reader.IsClosed));
        Assert.False(first.Tables["changing"].Columns.ContainsKey("Later"));
        Assert.Empty(first.Tables["changing"].Indexes);
        Assert.True(second.Tables["changing"].Columns.ContainsKey("Later"));
        Assert.Equal("ix_later", Assert.Single(second.Tables["changing"].Indexes).Name);
        transaction.Rollback();
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>A command exception releases owned readers and commands without disposing caller state.</summary>
    [Fact]
    public async Task CatalogStreaming_LeavesTransactionUsableAfterCommandFailure()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, "CREATE TABLE failure_items (Id INTEGER);");
        using var transaction = connection.BeginTransaction();
        using var counter = new SnapshotCountingConnection(connection, "pragma_index_list", "execute");

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqliteSafeMigrationCatalog.Read(counter, transaction));

        // Assert
        Assert.Equal("Injected catalog command failure.", exception.Message);
        Assert.Equal(4, counter.ExecutionCount);
        Assert.Equal(4, counter.DisposedCommandCount);
        Assert.All(counter.Readers, reader => Assert.True(reader.IsClosed));
        Assert.All(counter.Transactions, actual => Assert.Same(transaction, actual));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM failure_items;";
        Assert.Equal(0L, command.ExecuteScalar());
        transaction.Rollback();
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Missing streams, unowned objects, and broken ordinals fail closed.</summary>
    [Theory]
    [InlineData("pragma_table_xinfo", "missing")]
    [InlineData("pragma_index_list", "missing")]
    [InlineData("pragma_foreign_key_list", "missing")]
    [InlineData("pragma_table_xinfo", "owner")]
    [InlineData("pragma_index_list", "owner")]
    [InlineData("pragma_foreign_key_list", "owner")]
    [InlineData("pragma_index_list", "ordinal")]
    [InlineData("pragma_foreign_key_list", "ordinal")]
    [InlineData("pragma_index_list", "empty_collection")]
    [InlineData("pragma_foreign_key_list", "empty_collection")]
    public async Task CatalogStreaming_RejectsIncompleteOrUnownedMetadata(
        string segment,
        string fault
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE owner_parent (Id INTEGER PRIMARY KEY); "
            + "CREATE TABLE owner_child (Id INTEGER, "
            + "CONSTRAINT fk_owner_child FOREIGN KEY(Id) REFERENCES owner_parent(Id)); "
            + "CREATE INDEX ix_owner_child ON owner_child(Id);");
        using var counter = new SnapshotCountingConnection(connection, segment, fault);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqliteSafeMigrationCatalog.Read(counter, transaction: null));

        // Assert
        Assert.Equal("SQLite returned missing, inconsistent, or unowned catalog metadata.", exception.Message);
        Assert.Equal(counter.ExecutionCount, counter.DisposedCommandCount);
        Assert.All(counter.Readers, reader => Assert.True(reader.IsClosed));
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Automatic UNIQUE and primary-key indexes cannot be replaced with empty-collection evidence.</summary>
    [Theory]
    [InlineData("CREATE TABLE erased_keys (Code TEXT UNIQUE);")]
    [InlineData("CREATE TABLE erased_keys (Code TEXT, UNIQUE(Code));")]
    [InlineData("CREATE TABLE erased_keys (Code TEXT PRIMARY KEY);")]
    [InlineData("CREATE TABLE erased_keys (Code TEXT, Id INTEGER, PRIMARY KEY(Code, Id));")]
    [InlineData("CREATE TABLE erased_keys (Code TEXT PRIMARY KEY) WITHOUT ROWID;")]
    public async Task CatalogStreaming_RejectsErasedAutomaticIndexes(
        string sql
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, sql);
        using var counter = new SnapshotCountingConnection(connection, "pragma_index_list", "empty_collection");

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqliteSafeMigrationCatalog.Read(counter, transaction: null));

        // Assert
        Assert.Equal("SQLite returned missing, inconsistent, or unowned catalog metadata.", exception.Message);
        Assert.Equal(counter.ExecutionCount, counter.DisposedCommandCount);
        Assert.All(counter.Readers, reader => Assert.True(reader.IsClosed));
        Assert.Equal(ConnectionState.Open, connection.State);
    }

    /// <summary>Anonymous table-level and inline REFERENCES declarations require physical foreign-key rows.</summary>
    [Theory]
    [InlineData("ParentId INTEGER REFERENCES declared_parent")]
    [InlineData("ParentId INTEGER REFERENCES declared_parent(Id)")]
    [InlineData("ParentId INTEGER CONSTRAINT fk_inline REFERENCES declared_parent(Id)")]
    [InlineData("ParentId INTEGER, FOREIGN KEY(ParentId) REFERENCES declared_parent")]
    [InlineData("ParentId INTEGER, FOREIGN KEY(ParentId) REFERENCES declared_parent(Id)")]
    public async Task CatalogStreaming_RejectsErasedAnonymousAndInlineForeignKeys(
        string declaration
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE declared_parent (Id INTEGER PRIMARY KEY); "
            + $"CREATE TABLE declared_child ({declaration});");
        using var counter = new SnapshotCountingConnection(connection, "pragma_foreign_key_list", "empty_collection");

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SqliteSafeMigrationCatalog.Read(counter, transaction: null));

        // Assert
        Assert.Equal("SQLite returned missing, inconsistent, or unowned catalog metadata.", exception.Message);
        Assert.Equal(5, counter.ExecutionCount);
        Assert.Equal(5, counter.DisposedCommandCount);
        Assert.All(counter.Readers, reader => Assert.True(reader.IsClosed));
    }

    /// <summary>Real empty collections and integer rowid primary keys remain accepted in five commands.</summary>
    [Theory]
    [InlineData("Id INTEGER")]
    [InlineData("Id INTEGER PRIMARY KEY")]
    [InlineData("Id INTEGER PRIMARY KEY, Value TEXT DEFAULT 'REFERENCES absent(Code) UNIQUE PRIMARY KEY'")]
    public async Task CatalogStreaming_AcceptsGenuinelyEmptyIndexAndForeignKeyCollections(
        string columns
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(connection, $"CREATE TABLE empty_collections ({columns});");
        using var counter = new SnapshotCountingConnection(connection);

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(counter, transaction: null);

        // Assert
        var table = Assert.Single(snapshot.Tables).Value;
        Assert.Empty(table.Indexes);
        Assert.Empty(table.ForeignKeys);
        Assert.Equal(5, counter.ExecutionCount);
        Assert.Equal(5, counter.DisposedCommandCount);
        Assert.All(counter.Readers, reader => Assert.True(reader.IsClosed));
    }

    /// <summary>Anonymous, inline, and repeated foreign keys preserve physical ordinals and declared names.</summary>
    [Fact]
    public async Task CatalogStreaming_PreservesAllForeignKeyDeclarationForms()
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE all_parent (Id INTEGER PRIMARY KEY); "
            + "CREATE TABLE all_child (Id INTEGER PRIMARY KEY, "
            + "Anonymous INTEGER REFERENCES all_parent ON DELETE CASCADE, "
            + "Named INTEGER CONSTRAINT fk_inline REFERENCES all_parent(Id) ON UPDATE RESTRICT, "
            + "Repeated INTEGER CONSTRAINT fk_first REFERENCES all_parent(Id) "
            + "CONSTRAINT fk_second REFERENCES all_parent(Id), "
            + "TableReference INTEGER, FOREIGN KEY(TableReference) REFERENCES all_parent(Id));");
        using var counter = new SnapshotCountingConnection(connection);

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(counter, transaction: null);

        // Assert
        var table = snapshot.Tables["all_child"];
        AssertLegacyCatalogParity(connection, table);
        Assert.Equal(5, counter.ExecutionCount);
        Assert.Equal(5, table.ForeignKeys.Count);
        var anonymous = Assert.Single(table.ForeignKeys, value => value.Columns[0] == "Anonymous");
        Assert.Null(anonymous.Name);
        Assert.Equal(string.Empty, Assert.Single(anonymous.PrincipalColumns));
        Assert.True(SqliteCatalogEquivalence.ForeignKeyMatches(
            snapshot, anonymous, "all_parent", ["Anonymous"], ["Id"],
            ReferentialAction.NoAction, ReferentialAction.Cascade));
        Assert.Equal("fk_inline", Assert.Single(table.ForeignKeys, value => value.Columns[0] == "Named").Name);
        Assert.Collection(
            table.ForeignKeys.Where(value => value.Columns[0] == "Repeated"),
            key => Assert.Equal("fk_second", key.Name),
            key => Assert.Equal("fk_first", key.Name));
        Assert.Null(Assert.Single(table.ForeignKeys, value => value.Columns[0] == "TableReference").Name);
    }

    /// <summary>Declaration validation accepts SQLite identifier quoting and unquoted non-ASCII names.</summary>
    [Theory]
    [InlineData("'ParentId' INTEGER REFERENCES 'quoted parent'", "quoted parent")]
    [InlineData("\"ParentId\" INTEGER REFERENCES \"quoted parent\"", "quoted parent")]
    [InlineData("`ParentId` INTEGER REFERENCES `quoted parent`", "quoted parent")]
    [InlineData("[ParentId] INTEGER REFERENCES [quoted parent]", "quoted parent")]
    [InlineData("ParentId INTEGER REFERENCES par\u00e9nt", "par\u00e9nt")]
    [InlineData("ParentId INTEGER REFERENCES \"quoted (parent)\"", "quoted (parent)")]
    public async Task CatalogStreaming_PreservesForeignKeyIdentifierForms(
        string declaration,
        string parent
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            $"CREATE TABLE {QuoteCatalogIdentifier(parent)} (Id INTEGER CONSTRAINT pk_parent PRIMARY KEY); "
            + $"CREATE TABLE quoted_child ({declaration});");
        using var counter = new SnapshotCountingConnection(connection);

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(counter, transaction: null);

        // Assert
        var table = snapshot.Tables["quoted_child"];
        AssertLegacyCatalogParity(connection, table);
        Assert.Equal("pk_parent", snapshot.Tables[parent].PrimaryKeyName);
        var foreignKey = Assert.Single(table.ForeignKeys);
        Assert.Equal(parent, foreignKey.PrincipalTable);
        Assert.Equal("ParentId", Assert.Single(foreignKey.Columns));
        Assert.Equal(string.Empty, Assert.Single(foreignKey.PrincipalColumns));
        Assert.Equal(5, counter.ExecutionCount);
    }

    /// <summary>Single-quoted inline constraint names are decoded only at their identifier position.</summary>
    [Theory]
    [InlineData("'fk quoted'", "fk quoted")]
    [InlineData("'fk''quoted'", "fk'quoted")]
    public async Task CatalogStreaming_PreservesSingleQuotedInlineForeignKeyNames(
        string quotedName,
        string expectedName
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE quoted_name_parent (Id INTEGER PRIMARY KEY); "
            + "CREATE TABLE quoted_name_child (ParentId INTEGER "
            + $"CONSTRAINT {quotedName} REFERENCES quoted_name_parent(Id), "
            + "Literal TEXT DEFAULT 'CONSTRAINT phantom REFERENCES missing(Id)');");
        using var counter = new SnapshotCountingConnection(connection);

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(counter, transaction: null);

        // Assert
        var table = snapshot.Tables["quoted_name_child"];
        AssertLegacyCatalogParity(connection, table);
        Assert.Equal(expectedName, Assert.Single(table.ForeignKeys).Name);
        Assert.Equal("'CONSTRAINT phantom REFERENCES missing(Id)'", table.Columns["Literal"].DefaultSql);
        Assert.Equal(5, counter.ExecutionCount);
    }

    /// <summary>Delimited identifiers terminate grammar tokens without requiring separating whitespace.</summary>
    [Theory]
    [InlineData("ParentId INTEGER REFERENCES[parent](Id)", null)]
    [InlineData("ParentId INTEGER, CONSTRAINT[fk]FOREIGN KEY(ParentId)REFERENCES[parent](Id)", "fk")]
    [InlineData("ParentId INTEGER CONSTRAINT[fk]REFERENCES[parent](Id)", "fk")]
    public async Task CatalogStreaming_PreservesDelimiterSeparatedForeignKeyTokens(
        string declaration,
        string? expectedName
    )
    {
        // Arrange
        await using var connection = await OpenConnectionAsync();
        await ExecuteSqlAsync(
            connection,
            "CREATE TABLE parent (Id INTEGER PRIMARY KEY); "
            + $"CREATE TABLE token_child ({declaration});");
        using var counter = new SnapshotCountingConnection(connection);

        // Act
        var snapshot = SqliteSafeMigrationCatalog.Read(counter, transaction: null);

        // Assert
        var table = snapshot.Tables["token_child"];
        AssertLegacyCatalogParity(connection, table);
        var foreignKey = Assert.Single(table.ForeignKeys);
        Assert.Equal(expectedName, foreignKey.Name);
        Assert.Equal("parent", foreignKey.PrincipalTable);
        Assert.Equal("ParentId", Assert.Single(foreignKey.Columns));
        Assert.Equal("Id", Assert.Single(foreignKey.PrincipalColumns));
        Assert.Equal(5, counter.ExecutionCount);
    }

    private static void AssertLegacyCatalogParity(
        SqliteConnection connection,
        SqliteTableSnapshot table
    )
    {
        var columns = ReadLegacyRows(connection, $"PRAGMA main.table_xinfo({QuoteCatalogIdentifier(table.Name)});");
        Assert.Equal(
            columns,
            table.Columns.Values.Select(column => CatalogRow(
                column.Ordinal, column.Name, column.StoreType, column.IsNullable ? 0 : 1,
                column.DefaultSql, column.PrimaryKeyOrdinal, column.HiddenKind)).ToArray());
        var indexes = ReadLegacyRows(connection, $"PRAGMA main.index_list({QuoteCatalogIdentifier(table.Name)});");
        Assert.Equal(
            indexes.Select(row => row[(row.IndexOf('|') + 1)..]).ToArray(),
            table.Indexes.Select(index => CatalogRow(
                    index.Name, index.Unique ? 1 : 0, index.Origin, index.Partial ? 1 : 0))
                .ToArray());
        foreach (var index in table.Indexes)
        {
            Assert.Equal(
                ReadLegacyRows(connection, $"PRAGMA main.index_xinfo({QuoteCatalogIdentifier(index.Name)});"),
                index.Keys.Select(key => CatalogRow(
                    key.Ordinal, key.ColumnId, key.Column, key.Descending ? 1 : 0, key.Collation, key.IsKey ? 1 : 0))
                    .ToArray());
        }

        Assert.Equal(
            ReadLegacyRows(connection, $"PRAGMA main.foreign_key_list({QuoteCatalogIdentifier(table.Name)});"),
            table.ForeignKeys.SelectMany(key => key.Columns.Select((column, ordinal) => CatalogRow(
                key.Id, ordinal, key.PrincipalTable, column,
                key.PrincipalColumns[ordinal].Length == 0 ? null : key.PrincipalColumns[ordinal],
                CatalogReferentialAction(key.OnUpdate), CatalogReferentialAction(key.OnDelete), key.Match))).ToArray());
    }

    private static string CatalogReferentialAction(
        ReferentialAction action
    ) => action switch
    {
        ReferentialAction.NoAction => "NO ACTION",
        ReferentialAction.Restrict => "RESTRICT",
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => throw new InvalidOperationException("Unexpected fixture referential action."),
    };

    private static string[] ReadLegacyRows(
        SqliteConnection connection,
        string sql
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            _ = reader.GetValues(values);
            result.Add(CatalogRow(values));
        }

        return result.ToArray();
    }

    private static string CatalogRow(
        params object?[] values
    ) => string.Join('|', values.Select(value => value is null or DBNull
        ? "<null>"
        : Convert.ToString(value, CultureInfo.InvariantCulture)));

    private static string QuoteCatalogIdentifier(
        string value
    ) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed class SnapshotCountingConnection : DbConnection
    {
        private readonly SqliteConnection _inner;
        private readonly string? _segment;
        private readonly string? _fault;

        public SnapshotCountingConnection(
            SqliteConnection inner,
            string? segment = null,
            string? fault = null
        )
        {
            _inner = inner;
            _segment = segment;
            _fault = fault;
        }

        public int ExecutionCount { get; private set; }

        public int DisposedCommandCount { get; private set; }

        public List<DbDataReader> Readers { get; } = [];

        public List<DbTransaction?> Transactions { get; } = [];

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString
        {
            get => _inner.ConnectionString;
            set => _inner.ConnectionString = value ?? string.Empty;
        }

        public override string Database => _inner.Database;

        public override string DataSource => _inner.DataSource;

        public override string ServerVersion => _inner.ServerVersion;

        public override ConnectionState State => _inner.State;

        public override void ChangeDatabase(
            string databaseName
        ) => _inner.ChangeDatabase(databaseName);

        public override void Close() => _inner.Close();

        public override void Open() => _inner.Open();

        protected override DbTransaction BeginDbTransaction(
            IsolationLevel isolationLevel
        ) => _inner.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() => new SnapshotCountingCommand(this, _inner.CreateCommand());

        private DbDataReader ExecuteReader(
            DbCommand command,
            CommandBehavior behavior
        )
        {
            ExecutionCount++;
            Transactions.Add(command.Transaction);
            var inject = _segment is not null && command.CommandText.Contains(_segment, StringComparison.Ordinal);
            if (inject && _fault == "execute")
            {
                throw new InvalidOperationException("Injected catalog command failure.");
            }

            var reader = command.ExecuteReader(behavior);
            Readers.Add(reader);
            if (inject)
            {
                using (reader)
                {
                    var faultReader = CreateFaultReader(reader);
                    Readers.Add(faultReader);

                    return faultReader;
                }
            }

            return reader;
        }

        private DataTableReader CreateFaultReader(
            DbDataReader reader
        )
        {
            var table = new DataTable { Locale = CultureInfo.InvariantCulture };
            for (var index = 0; index < reader.FieldCount; index++)
            {
                table.Columns.Add($"facet_{index}", typeof(object));
            }

            while (reader.Read())
            {
                if (_fault == "missing")
                {
                    continue;
                }

                var values = new object[reader.FieldCount];
                _ = reader.GetValues(values);
                for (var index = 0; index < values.Length; index++)
                {
                    if (values[index] is long integer)
                    {
                        values[index] = checked((int)integer);
                    }
                }

                if (_fault == "owner")
                {
                    values[0] = "unowned_table";
                }
                else if (_fault == "ordinal")
                {
                    values[_segment == "pragma_index_list" ? 9 : 2] = 100;
                }
                else if (_fault == "empty_collection")
                {
                    if (values[1] is not DBNull)
                    {
                        for (var index = 1; index < values.Length; index++)
                        {
                            values[index] = DBNull.Value;
                        }
                    }
                }

                table.Rows.Add(values);
            }

            return table.CreateDataReader();
        }

        private sealed class SnapshotCountingCommand : DbCommand
        {
            private readonly SnapshotCountingConnection _owner;
            private readonly DbCommand _inner;
            private bool _disposed;

            public SnapshotCountingCommand(
                SnapshotCountingConnection owner,
                DbCommand inner
            )
            {
                _owner = owner;
                _inner = inner;
            }

            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string CommandText
            {
                get => _inner.CommandText;
                set => _inner.CommandText = value ?? string.Empty;
            }

            public override int CommandTimeout
            {
                get => _inner.CommandTimeout;
                set => _inner.CommandTimeout = value;
            }

            public override CommandType CommandType
            {
                get => _inner.CommandType;
                set => _inner.CommandType = value;
            }

            public override bool DesignTimeVisible
            {
                get => _inner.DesignTimeVisible;
                set => _inner.DesignTimeVisible = value;
            }

            public override UpdateRowSource UpdatedRowSource
            {
                get => _inner.UpdatedRowSource;
                set => _inner.UpdatedRowSource = value;
            }

            protected override DbConnection? DbConnection
            {
                get => _owner;
                set => _inner.Connection = value is SnapshotCountingConnection counter ? counter._inner : value;
            }

            protected override DbTransaction? DbTransaction
            {
                get => _inner.Transaction;
                set => _inner.Transaction = value;
            }

            protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

            protected override DbParameter CreateDbParameter() => _inner.CreateParameter();

            public override void Cancel() => _inner.Cancel();

            public override void Prepare() => _inner.Prepare();

            public override int ExecuteNonQuery() => _inner.ExecuteNonQuery();

            public override object? ExecuteScalar() => _inner.ExecuteScalar();

            protected override DbDataReader ExecuteDbDataReader(
                CommandBehavior behavior
            ) => _owner.ExecuteReader(_inner, behavior);

            protected override void Dispose(
                bool disposing
            )
            {
                if (disposing && !_disposed)
                {
                    _disposed = true;
                    _inner.Dispose();
                    _owner.DisposedCommandCount++;
                }

                base.Dispose(disposing);
            }
        }
    }
}
