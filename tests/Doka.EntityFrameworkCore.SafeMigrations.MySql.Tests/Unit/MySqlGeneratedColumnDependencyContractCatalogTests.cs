namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies conditional generated candidates without trusting live or future schema guesses.</summary>
public sealed class MySqlGeneratedColumnDependencyContractCatalogTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    /// <summary>Receives informational allocation evidence without introducing a performance gate.</summary>
    /// <param name="output">The test-owned evidence sink.</param>
    public MySqlGeneratedColumnDependencyContractCatalogTests(
        Xunit.Abstractions.ITestOutputHelper output
    ) => _output = output;

    /// <summary>Sharing empty immutable values cannot merge operation keys or ambiguous states.</summary>
    [Fact]
    public void ParsedEmptySnapshotsShareImmutableContractWithoutMergingOperationKeys()
    {
        // Arrange
        const int dropCount = 1000;
        var operations = new List<MigrationOperation>(dropCount + 3) { Table(), };
        for (var ordinal = 0; ordinal < dropCount; ordinal++)
        {
            operations.Add(Safe(new DropColumnIntent($"retired_{ordinal}", "rows")));
        }

        operations.Add(Safe(new EnsureTableIntent(new ExpectedTableDefinition("ambiguous",
            [Ordinary("VALUE"), Ordinary("value")]), SafeMigrationTableMode.StrictDefinition)));
        var ambiguous = Safe(new DropColumnIntent("retired", "ambiguous"));
        operations.Add(ambiguous);
        for (var warmup = 0; warmup < 4; warmup++)
        {
            _ = MySqlGeneratedColumnDependencyContractCatalog.Create(operations);
        }

        var first = (SafeMigrationOperation)operations[1];
        var last = (SafeMigrationOperation)operations[dropCount];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create(operations);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // Assert
        _output.WriteLine($"Informational: drops={dropCount}; allocated-bytes={allocatedBytes}");
        var empty = Assert.IsType<MySqlGeneratedColumnDependencyContract>(catalog.Get(first));
        Assert.Same(empty, catalog.Get(last));
        Assert.Null(catalog.Get(Safe(new DropColumnIntent("retired_0", "rows"))));
        Assert.NotSame(empty, catalog.Get(ambiguous));
        Assert.False(Assert.IsType<MySqlGeneratedColumnDependencyContract>(catalog.Get(ambiguous))
            .HasOnlyParsedExpressions);
    }

    /// <summary>Identifier roles, not matching text in literals, establish a generated dependency.</summary>
    /// <param name="identifierReference">Whether the source occurs as an identifier rather than literal text.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotDistinguishesIdentifierAndLiteralRoles(bool identifierReference)
    {
        // Arrange
        var expression = SafeMigrationSql.Binary(
            identifierReference ? SafeMigrationSql.Identifier("VALUE") : SafeMigrationSql.Identifier("other"),
            SafeMigrationSqlBinaryOperator.Equal, SafeMigrationSql.Literal("value"));

        var rename = Rename("value", "renamed");
        MigrationOperation[] operations = [Table(Generated("derived", expression)), rename];

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create(operations).Get(rename);

        // Assert
        Assert.NotNull(contract);
        Assert.True(contract.HasOnlyParsedExpressions);
        Assert.Equal(identifierReference, contract.ReferencesSource);
        Assert.Equal("derived", Assert.Single(contract.GeneratedColumns).Name);
    }

    /// <summary>Unknown syntax or role binding cannot certify absence of a generated dependency.</summary>
    /// <param name="syntax">The unsupported or ambiguous expression representation.</param>
    [Theory]
    [InlineData("raw")]
    [InlineData("nested_opaque")]
    [InlineData("provider_fragment")]
    [InlineData("qualified")]
    public void UnknownGeneratedMembersDoNotBecomeParsedNondependencyProof(string syntax)
    {
        // Arrange
        var column = syntax == "raw"
            ? new ExpectedColumnDefinition("derived", typeof(int), true, "int", computedColumnSql: "opaque(value)")
            : Generated("derived", syntax switch
            {
                "nested_opaque" => SafeMigrationSql.Binary(SafeMigrationSql.Identifier("other"),
                    SafeMigrationSqlBinaryOperator.Add, SafeMigrationSql.Opaque("opaque(value)")),
                "provider_fragment" => SafeMigrationSql.ProviderFragment("mysql", "opaque(value)"),
                _ => SafeMigrationSql.Identifier("external", "value"),
            });

        var rename = Rename("value", "renamed");

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create([Table(column), rename]).Get(rename);

        // Assert
        Assert.NotNull(contract);
        Assert.False(contract.HasOnlyParsedExpressions);
        Assert.Single(contract.GeneratedColumns);
    }

    /// <summary>Only exact earlier owner declarations seed a complete conditional generated set.</summary>
    /// <param name="boundary">The operation that removes exact prefix authority.</param>
    [Theory]
    [InlineData("raw")]
    [InlineData("provider")]
    [InlineData("existence_table")]
    [InlineData("existence_column")]
    [InlineData("convergence")]
    [InlineData("annotated")]
    [InlineData("annotated_data")]
    [InlineData("drop_owner")]
    public void UnknownOrInexactBoundaryExpiresCompleteOwnerPrefix(string boundary)
    {
        // Arrange
        var generated = Generated("derived", SafeMigrationSql.Identifier("other"));
        var rename = Rename("value", "renamed");
        var annotated = Safe(new EnsureColumnIntent("rows", Ordinary("added")));
        annotated.AddAnnotation("Test:UnknownOperationAnnotation", true);
        var annotatedData = new InsertDataOperation
        {
            Table = "rows", Columns = ["value"], Values = new object?[,] { { 1 }, },
        };

        annotatedData.AddAnnotation("Test:UnknownDataAnnotation", true);
        MigrationOperation mutation = boundary switch
        {
            "raw" => new SqlOperation { Sql = "SELECT 1;", },
            "provider" => new AddColumnOperation { Name = "outside", Table = "other_rows", ClrType = typeof(int), },
            "existence_table" => Table(generated, policy: SafeMigrationPolicy.ExistenceOnly),
            "existence_column" => Safe(new EnsureColumnIntent("rows", Ordinary("added")),
                SafeMigrationPolicy.ExistenceOnly),
            "convergence" => Table(generated, mode: SafeMigrationTableMode.ConvergenceContainer),
            "annotated" => annotated,
            "annotated_data" => annotatedData,
            _ => Safe(new DropTableIntent("rows")),
        };

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create([Table(generated), mutation, rename])
            .Get(rename);

        // Assert
        Assert.Null(contract);
    }

    /// <summary>Typed row changes retain candidates but confer no native generated-member authority.</summary>
    /// <param name="change">The safe or ordinary typed data operation.</param>
    [Theory]
    [InlineData("managed_insert")]
    [InlineData("insert")]
    [InlineData("update")]
    [InlineData("delete")]
    public void TypedDataChangesPreserveConditionalGeneratedMemberCandidates(string change)
    {
        // Arrange
        var generated = Generated("derived", SafeMigrationSql.Identifier("other"));
        var rename = Rename("value", "renamed");
        MigrationOperation data = change switch
        {
            "managed_insert" => Safe(new EnsureModelManagedDataIntent("rows", ["value"], ["int"],
                ["value", "other"], ["int", "int"], new object?[,] { { 1, 2 }, }, null, null)),
            "insert" => new InsertDataOperation
            {
                Table = "rows", Columns = ["value", "other"], Values = new object?[,] { { 1, 2 }, },
            },
            "update" => new UpdateDataOperation
            {
                Table = "rows", KeyColumns = ["value"], KeyValues = new object?[,] { { 1 }, },
                Columns = ["other"], Values = new object?[,] { { 3 }, },
            },
            _ => new DeleteDataOperation
            {
                Table = "rows", KeyColumns = ["value"], KeyValues = new object?[,] { { 1 }, },
            },
        };

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create([Table(generated), data, rename])
            .Get(rename);

        // Assert
        Assert.NotNull(contract);
        Assert.Same(generated, Assert.Single(contract.GeneratedColumns));
        Assert.True(contract.HasOnlyParsedExpressions);
        Assert.False(contract.ReferencesSource);
    }

    /// <summary>A DROP certificate describes only generated members surviving that exact removal.</summary>
    /// <param name="shape">The surviving member dependency or removed generated member.</param>
    /// <param name="generatedCount">The expected number of surviving generated declarations.</param>
    /// <param name="references">Whether a surviving parsed expression references the dropped member.</param>
    [Theory]
    [InlineData("dependent", 1, true)]
    [InlineData("unrelated", 1, false)]
    [InlineData("literal", 1, false)]
    [InlineData("self", 0, false)]
    [InlineData("opaque_self", 0, false)]
    public void DropSnapshotExcludesOnlyTheRemovedGeneratedMember(string shape, int generatedCount, bool references)
    {
        // Arrange
        var generated = shape == "opaque_self"
            ? new ExpectedColumnDefinition("derived", typeof(int), true, "int", computedColumnSql: "opaque(value)")
            : Generated("derived", shape == "literal" ? SafeMigrationSql.Literal("value")
                : SafeMigrationSql.Identifier(shape is "dependent" or "self" ? "value" : "other"));

        var drop = Safe(new DropColumnIntent(shape is "self" or "opaque_self" ? "derived" : "value", "rows"));

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create([Table(generated), drop]).Get(drop);

        // Assert
        Assert.NotNull(contract);
        Assert.Equal(generatedCount, contract.GeneratedColumns.Count);
        Assert.Equal(references, contract.ReferencesSource);
        Assert.True(contract.HasOnlyParsedExpressions);
    }

    /// <summary>Sparse declarations are candidates only; a fresh native complete-set fence must qualify them.</summary>
    /// <param name="references">Whether the declared generated expression references the DROP source.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparseGeneratedDeclarationCanSupplyConditionalDropCandidate(bool references)
    {
        // Arrange
        var generated = Generated("derived", SafeMigrationSql.Identifier(references ? "value" : "other"));
        var drop = Safe(new DropColumnIntent("value", "rows"));
        MigrationOperation[] operations = [Safe(new EnsureColumnIntent("rows", generated)), drop];

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create(operations).Get(drop);

        // Assert
        Assert.NotNull(contract);
        Assert.Same(generated, Assert.Single(contract.GeneratedColumns));
        Assert.Equal(references, contract.ReferencesSource);
    }

    /// <summary>A known removal can propose an empty set without claiming that unobserved members are absent.</summary>
    [Fact]
    public void UnknownOwnerDropSuppliesOnlyAnUntrustedEmptyCandidate()
    {
        // Arrange
        var first = Safe(new DropColumnIntent("derived", "rows"));
        var source = Safe(new DropColumnIntent("value", "rows"));

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create([first, source]);

        // Assert
        Assert.Empty(Assert.IsType<MySqlGeneratedColumnDependencyContract>(catalog.Get(first)).GeneratedColumns);
        Assert.Empty(Assert.IsType<MySqlGeneratedColumnDependencyContract>(catalog.Get(source)).GeneratedColumns);
    }

    /// <summary>Model bytes need fresh native proof and cannot become an authored prefix across raw SQL.</summary>
    /// <param name="opaque">Whether raw SQL precedes the independently bound model candidate.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OperationBoundModelDoesNotBecomePrefixAcrossOpaqueBoundary(bool opaque)
    {
        // Arrange
        var generated = Generated("derived", SafeMigrationSql.Identifier("value"));
        var model = new ExpectedTableDefinition("rows", [generated]);
        var rename = Rename("value", "renamed");
        MigrationOperation[] operations = opaque ? [new SqlOperation { Sql = "SELECT 1;", }, rename] : [rename];

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create(operations,
            targetModelCandidates: ModelCandidates(rename, model));

        // Assert
        Assert.Null(catalog.Get(rename));
        var contract = Assert.IsType<MySqlGeneratedColumnDependencyContract>(catalog.GetModelCandidate(rename));
        Assert.Same(generated, Assert.Single(contract.GeneratedColumns));
        Assert.True(contract.ReferencesSource);
    }

    /// <summary>A target-model fallback cannot become an accepted authored-prefix declaration.</summary>
    [Fact]
    public void ModelFallbackDoesNotSeedAuthoredPrefix()
    {
        // Arrange
        var generated = Generated("derived", SafeMigrationSql.Identifier("other"));
        var rename = Rename("value", "renamed");
        var model = new ExpectedTableDefinition("rows", [generated]);

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create([rename],
            targetModelCandidates: ModelCandidates(rename, model));

        // Assert
        Assert.Null(catalog.Get(rename));
    }

    /// <summary>Authored and model declarations remain separate whole candidates, never a merged member set.</summary>
    [Fact]
    public void AuthoredOwnerAndTargetModelRemainIndependentWholeCandidates()
    {
        // Arrange
        var modeled = Generated("derived", SafeMigrationSql.Identifier("value"));
        var authored = Generated("derived", SafeMigrationSql.Identifier("other"));
        var drop = Safe(new DropColumnIntent("value", "rows"));

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create([Table(authored), drop],
            targetModelCandidates: ModelCandidates(drop, new ExpectedTableDefinition("rows", [modeled])));

        // Assert
        var prefix = Assert.IsType<MySqlGeneratedColumnDependencyContract>(catalog.Get(drop));
        var model = Assert.IsType<MySqlGeneratedColumnDependencyContract>(catalog.GetModelCandidate(drop));
        Assert.Same(authored, Assert.Single(prefix.GeneratedColumns));
        Assert.False(prefix.ReferencesSource);
        Assert.Same(modeled, Assert.Single(model.GeneratedColumns));
        Assert.True(model.ReferencesSource);
    }

    /// <summary>
    /// Each original operation keeps its own historical target even when the native owner is identical.
    /// </summary>
    [Fact]
    public void TargetModelsBindToOriginalOperationsWithoutBorrowingAnotherMigration()
    {
        // Arrange
        var first = Safe(new DropColumnIntent("value", "rows"));
        var second = Safe(new DropColumnIntent("value", "rows"));
        var firstColumn = Generated("derived", SafeMigrationSql.Identifier("value"));
        var secondColumn = Generated("derived", SafeMigrationSql.Identifier("other"));
        var models = new Dictionary<SafeMigrationOperation, IReadOnlyList<ExpectedTableDefinition>>(
            ReferenceEqualityComparer.Instance)
        {
            [first] = [new ExpectedTableDefinition("rows", [firstColumn])],
            [second] = [new ExpectedTableDefinition("rows", [secondColumn])],
        };

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create([first, second],
            targetModelCandidates: models);

        // Assert
        var firstModel = Assert.IsType<MySqlGeneratedColumnDependencyContract>(catalog.GetModelCandidate(first));
        var secondModel = Assert.IsType<MySqlGeneratedColumnDependencyContract>(catalog.GetModelCandidate(second));
        Assert.Same(firstColumn, Assert.Single(firstModel.GeneratedColumns));
        Assert.True(firstModel.ReferencesSource);
        Assert.Same(secondColumn, Assert.Single(secondModel.GeneratedColumns));
        Assert.False(secondModel.ReferencesSource);
    }

    /// <summary>Only a unique complete model owner can supply the selected native owner profile.</summary>
    /// <param name="foldOwners">Whether the native owner profile folds physical table case.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModelOwnerCollisionIsProfileSpecificAndCannotUnionCandidates(bool foldOwners)
    {
        // Arrange
        var rename = Safe(new RenameColumnIntent("value", "Rows", "renamed"));
        var exactColumn = Generated("exact_derived", SafeMigrationSql.Identifier("other"));
        var foldedColumn = Generated("folded_derived", SafeMigrationSql.Identifier("value"));
        var models = ModelCandidates(rename, new ExpectedTableDefinition("Rows", [exactColumn]),
            new ExpectedTableDefinition("rows", [foldedColumn]));

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create([rename],
            tableComparer: foldOwners ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal,
            targetModelCandidates: models).GetModelCandidate(rename);

        // Assert
        if (foldOwners)
        {
            Assert.Null(contract);
        }
        else
        {
            Assert.NotNull(contract);
            Assert.Same(exactColumn, Assert.Single(contract.GeneratedColumns));
            Assert.False(contract.ReferencesSource);
        }
    }

    /// <summary>A repeated reference has no single prefix or model even if the first prefix was unknown.</summary>
    [Fact]
    public void RepeatedUnknownThenDeclaredOperationCannotAcquireEitherCandidate()
    {
        // Arrange
        var rename = Rename("value", "renamed");
        var generated = Generated("derived", SafeMigrationSql.Identifier("other"));
        var models = ModelCandidates(rename, new ExpectedTableDefinition("rows", [generated]));

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create([rename, Table(generated), rename],
            targetModelCandidates: models);

        // Assert
        Assert.Null(catalog.Get(rename));
        Assert.Null(catalog.GetModelCandidate(rename));
    }

    /// <summary>Equal-looking keys absent from the original stream cannot donate or receive model candidates.</summary>
    [Fact]
    public void ForeignOperationReferenceCannotBindModelCandidate()
    {
        // Arrange
        var actual = Rename("value", "renamed");
        var foreign = Rename("value", "renamed");
        var generated = Generated("derived", SafeMigrationSql.Identifier("other"));
        var models = ModelCandidates(foreign, new ExpectedTableDefinition("rows", [generated]));

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create([actual], targetModelCandidates: models);

        // Assert
        Assert.Null(catalog.GetModelCandidate(actual));
        Assert.Null(catalog.GetModelCandidate(foreign));
    }

    /// <summary>The exact model snapshot keeps all forwarded facets rather than rebuilding a partial column.</summary>
    [Fact]
    public void ModelCandidatePreservesOriginalGeneratedFacetObject()
    {
        // Arrange
        var rename = Rename("value", "renamed");
        var generated = new ExpectedColumnDefinition("derived", typeof(string), true, "varchar(32)",
            maxLength: 32, collation: new SafeMigrationCollationIdentifier("utf8mb4_bin"),
            comment: "generated contract",
            computedExpression: SafeMigrationSql.Identifier("other"), isStored: true);

        var model = new ExpectedTableDefinition("rows", [generated]);

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create([rename],
            targetModelCandidates: ModelCandidates(rename, model)).GetModelCandidate(rename);

        // Assert
        Assert.NotNull(contract);
        Assert.Same(generated, Assert.Single(contract.GeneratedColumns));
    }

    /// <summary>Repeated DROP operation objects cannot borrow a different occurrence's source lifetime.</summary>
    [Fact]
    public void RepeatedDropReferenceHasNoSingleCandidate()
    {
        // Arrange
        var drop = Safe(new DropColumnIntent("value", "rows"));

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create([Table(), drop, drop]).Get(drop);

        // Assert
        Assert.Null(contract);
    }

    /// <summary>Future generated additions cannot contaminate an earlier rename's complete prefix.</summary>
    [Fact]
    public void PrefixSnapshotExcludesFutureGeneratedDeclarations()
    {
        // Arrange
        var rename = Rename("value", "renamed");
        var generated = Generated("derived", SafeMigrationSql.Identifier("renamed"));
        MigrationOperation[] operations = [Table(), rename, Safe(new EnsureColumnIntent("rows", generated))];

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create(operations).Get(rename);

        // Assert
        Assert.NotNull(contract);
        Assert.Empty(contract.GeneratedColumns);
        Assert.True(contract.HasOnlyParsedExpressions);
        Assert.False(contract.ReferencesSource);
    }

    /// <summary>Known generated ADD, ALTER and DROP operations update the next complete conditional set.</summary>
    /// <param name="change">The known generated-member transition.</param>
    /// <param name="count">The expected number of generated members at the rename.</param>
    /// <param name="references">Whether the remaining generated expression depends on the source.</param>
    [Theory]
    [InlineData("add", 1, true)]
    [InlineData("alter", 1, true)]
    [InlineData("drop", 0, false)]
    public void PrefixAppliesKnownGeneratedMemberTransitions(string change, int count, bool references)
    {
        // Arrange
        var old = Generated("derived", SafeMigrationSql.Identifier("other"));
        var target = Generated("derived", SafeMigrationSql.Identifier("value"));
        var rename = Rename("value", "renamed");
        var initial = change == "add" ? Table() : Table(old);
        var mutation = change switch
        {
            "add" => Safe(new EnsureColumnIntent("rows", target)),
            "alter" => Safe(new AlterColumnIntent("rows", target, old), SafeMigrationPolicy.RepairIfSafe),
            _ => Safe(new DropColumnIntent("derived", "rows")),
        };

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create([initial, mutation, rename]).Get(rename);

        // Assert
        Assert.NotNull(contract);
        Assert.Equal(count, contract.GeneratedColumns.Count);
        Assert.Equal(references, contract.ReferencesSource);
    }

    /// <summary>Removing and recreating an owner discards the prior generated-member lifetime.</summary>
    [Fact]
    public void DropAndRecreateUsesOnlyNewOwnerGeneration()
    {
        // Arrange
        var old = Generated("old_derived", SafeMigrationSql.Identifier("value"));
        var rename = Rename("value", "renamed");
        MigrationOperation[] operations = [Table(old), Safe(new DropTableIntent("rows")), Table(), rename];

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create(operations).Get(rename);

        // Assert
        Assert.NotNull(contract);
        Assert.Empty(contract.GeneratedColumns);
        Assert.False(contract.ReferencesSource);
    }

    /// <summary>A refused dependent rename supplies no complete prefix for another later rename.</summary>
    [Fact]
    public void DependentRenameDoesNotAuthorizeLaterOwnerPrefix()
    {
        // Arrange
        var first = Rename("value", "renamed");
        var later = Rename("other", "renamed_other");
        MigrationOperation[] operations =
            [Table(Generated("derived", SafeMigrationSql.Identifier("value"))), first, later];

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create(operations);

        // Assert
        Assert.True(Assert.IsType<MySqlGeneratedColumnDependencyContract>(catalog.Get(first)).ReferencesSource);
        Assert.Null(catalog.Get(later));
    }

    /// <summary>Native column case remains independent of platform-sensitive physical owner identity.</summary>
    /// <param name="foldOwners">Whether the executing server certifies case-insensitive owner identity.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ColumnCaseDoesNotSilentlyFoldTableOwners(bool foldOwners)
    {
        // Arrange
        var generated = Generated("derived", SafeMigrationSql.Identifier("VALUE"));
        var table = Table(generated, table: "Rows", schema: "selected");
        var rename = Safe(new RenameColumnIntent("value", "rows", "renamed"));

        // Act
        var contract = MySqlGeneratedColumnDependencyContractCatalog.Create([table, rename], "selected",
            foldOwners ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Get(rename);

        // Assert
        Assert.Equal(foldOwners, contract is not null);
        if (contract is not null)
        {
            Assert.True(contract.ReferencesSource);
        }
    }

    /// <summary>Legal owner/member renames retain immutable snapshots, not mutable dictionary aliases.</summary>
    [Fact]
    public void RenamedOwnerAndGeneratedMemberRetainIndependentPrefixSnapshots()
    {
        // Arrange
        var generated = Generated("derived", SafeMigrationSql.Identifier("other"));
        var first = Rename("value", "renamed");
        var generatedRename = Rename("derived", "renamed_derived");
        var last = Safe(new RenameColumnIntent("renamed", "new_rows", "final"));
        MigrationOperation[] operations =
        [
            Table(generated), first, generatedRename,
            Safe(new RenameTableIntent("rows", "new_rows")), last,
        ];

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create(operations);
        var original = catalog.Get(first)!;
        var final = catalog.Get(last)!;
        var mutation = Record.Exception(() => ((IList<ExpectedColumnDefinition>)original.GeneratedColumns)[0] =
            Ordinary("unreviewed"));

        // Assert
        Assert.Same(generated, Assert.Single(original.GeneratedColumns));
        Assert.Equal("renamed_derived", Assert.Single(final.GeneratedColumns).Name);
        Assert.IsType<NotSupportedException>(mutation);
    }

    /// <summary>Equal-looking or repeated operation references cannot acquire another occurrence's prefix.</summary>
    [Fact]
    public void PrefixUsesExactUniqueRenameOperationIdentity()
    {
        // Arrange
        var first = Rename("value", "renamed");
        var equalLooking = Rename("value", "renamed");
        var repeated = Rename("other", "renamed_other");
        MigrationOperation[] operations = [Table(), first, repeated, repeated];

        // Act
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create(operations);

        // Assert
        Assert.NotNull(catalog.Get(first));
        Assert.Null(catalog.Get(equalLooking));
        Assert.Null(catalog.Get(repeated));
    }

    /// <summary>Capture windows use a supplied complete stream rather than their truncated local prefix.</summary>
    [Fact]
    public void AnalysisCaptureRetainsCompleteContractAcrossWindowBoundary()
    {
        // Arrange
        var rename = Rename("value", "renamed");
        SafeMigrationOperation[] operations =
            [Table(Generated("derived", SafeMigrationSql.Identifier("other"))), rename];
        var catalog = MySqlGeneratedColumnDependencyContractCatalog.Create(operations);
        var capture = new MySqlSafeMigrationPlanCapture();
        var indexes = MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes(operations);
        var constraints = SafeMigrationExpectedTableConstraints.FromOperations(operations);
        using var lease = capture.Begin([rename], indexes, constraints, generatedColumnDependencies: catalog);

        // Act
        var exact = capture.GetGenerationGeneratedColumnDependencyContract(rename);
        var folded = capture.GetGenerationFoldedGeneratedColumnDependencyContract(rename);

        // Assert
        Assert.Same(catalog.Get(rename), exact);
        Assert.Null(folded);
        Assert.Single(exact!.GeneratedColumns);
    }

    /// <summary>Connection-free generation keeps both verified native owner profiles without guessing one.</summary>
    [Fact]
    public void GenerationSelectsSeparateExactAndFoldedOwnerCandidates()
    {
        // Arrange
        var rename = Safe(new RenameColumnIntent("value", "rows", "renamed"));
        MigrationOperation[] operations = [Table(table: "Rows"), rename];
        var capture = new MySqlSafeMigrationPlanCapture();
        using var lease = capture.BeginGeneration(operations);

        // Act
        var exact = capture.GetGenerationGeneratedColumnDependencyContract(rename);
        var folded = capture.GetGenerationFoldedGeneratedColumnDependencyContract(rename);

        // Assert
        Assert.Null(exact);
        Assert.NotNull(folded);
        Assert.Empty(folded.GeneratedColumns);
    }

    /// <summary>
    /// A folded owner collision cannot borrow a valid candidate from the distinct exact-owner profile.
    /// </summary>
    [Fact]
    public void FoldedOwnerCollisionDoesNotFallbackToExactCandidate()
    {
        // Arrange
        var rename = Safe(new RenameColumnIntent("value", "OTHER", "renamed"));
        MigrationOperation[] operations =
        [
            Table(table: "Rows"), Table(table: "other"),
            Safe(new RenameTableIntent("Rows", "OTHER")), rename,
        ];

        // Act
        var exact = MySqlGeneratedColumnDependencyContractCatalog.Create(operations).Get(rename);
        var folded = MySqlGeneratedColumnDependencyContractCatalog.Create(operations,
            tableComparer: StringComparer.OrdinalIgnoreCase).Get(rename);

        // Assert
        Assert.NotNull(exact);
        Assert.Null(folded);
    }

    /// <summary>Scope disposal removes the prior migration's conditional generated contract.</summary>
    [Fact]
    public void GenerationLeaseExpiresCapturedPrefix()
    {
        // Arrange
        var rename = Rename("value", "renamed");
        var capture = new MySqlSafeMigrationPlanCapture();
        using (capture.BeginGeneration([Table(), rename]))
        {
        }

        // Act
        var failure = Record.Exception(() => capture.GetGenerationGeneratedColumnDependencyContract(rename));
        var folded = capture.GetGenerationFoldedGeneratedColumnDependencyContract(rename);

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Null(folded);
    }

    /// <summary>Disposal expires both operation-bound model profiles without retaining a prior migration.</summary>
    [Fact]
    public void GenerationLeaseExpiresOperationBoundModelCandidates()
    {
        // Arrange
        var rename = Rename("value", "renamed");
        var capture = new MySqlSafeMigrationPlanCapture();
        var model = new ExpectedTableDefinition("rows", [Generated("derived", SafeMigrationSql.Identifier("other"))]);
        using (capture.BeginGeneration([rename], ModelCandidates(rename, model)))
        {
        }

        // Act
        var failure = Record.Exception(() => capture.GetGenerationGeneratedColumnModelCandidate(rename));
        var folded = capture.GetGenerationFoldedGeneratedColumnModelCandidate(rename);

        // Assert
        Assert.IsType<InvalidOperationException>(failure);
        Assert.Null(folded);
    }

    private static Dictionary<SafeMigrationOperation, IReadOnlyList<ExpectedTableDefinition>> ModelCandidates(
        SafeMigrationOperation operation,
        params ExpectedTableDefinition[] definitions
    ) => new Dictionary<SafeMigrationOperation, IReadOnlyList<ExpectedTableDefinition>>(
        ReferenceEqualityComparer.Instance)
    {
        [operation] = definitions,
    };

    private static ExpectedColumnDefinition Ordinary(string name) => new(name, typeof(int), true, "int");

    private static ExpectedColumnDefinition Generated(string name, SafeMigrationSqlExpression expression) =>
        new(name, typeof(int), true, "int", computedExpression: expression);

    private static SafeMigrationOperation Rename(string source, string target) =>
        Safe(new RenameColumnIntent(source, "rows", target));

    private static SafeMigrationOperation Table(
        ExpectedColumnDefinition? generated = null,
        string table = "rows",
        string? schema = null,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent,
        SafeMigrationTableMode mode = SafeMigrationTableMode.StrictDefinition
    ) => Safe(new EnsureTableIntent(new ExpectedTableDefinition(table,
        generated is null ? [Ordinary("value"), Ordinary("other")] : [Ordinary("value"), Ordinary("other"), generated],
        schema), mode), policy);

    private static SafeMigrationOperation Safe(SafeMigrationIntent intent,
        SafeMigrationPolicy policy = SafeMigrationPolicy.ThrowIfDifferent) => new(intent, policy);
}
