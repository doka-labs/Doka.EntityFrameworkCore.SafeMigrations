using System.Diagnostics.CodeAnalysis;

namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>A table-metadata correction must not add public migration-factory overloads.</summary>
    [Fact]
    public void TableMetadataCaptureRetainsTheReleasedPublicBuilderSurface()
    {
        // Arrange
        var methods = typeof(SafeMigrationBuilderExtensions)
            .GetMethods()
            .Where(static method => method.Name is "CreateTableIfNotExists"
                or "CreateTableIfNotExistsFromModel"
                or "ConvergeTableFromModel")
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ToArray();

        // Act
        var names = methods
            .Select(static method => method.Name)
            .ToArray();

        // Assert
        Assert.Equal(["ConvergeTableFromModel", "CreateTableIfNotExists"], names);
        Assert.All(methods, static method => Assert.True(method.IsGenericMethodDefinition));
    }

    /// <summary>Gets source-shape cases whose quoted contents cannot identify callback boundaries.</summary>
    public static IEnumerable<object?[]> TableMetadataSourceCases()
    {
        string?[] comments =
        [
            null,
            string.Empty,
            "A normal table comment.",
            "first\r\n}\nconstraints: table =>\n{\ncomment: \"\"\n.CreateTable(\n.Annotation(",
            "quoted \\\" ) } .CreateTable( constraints: table => and \\path\\",
        ];

        SafeMigrationScaffoldingMode[] modes =
        [
            SafeMigrationScaffoldingMode.Strict,
            SafeMigrationScaffoldingMode.LegacyConvergence,
        ];

        bool[] constraintStates = [false, true];

        foreach (var mode in modes)
        {
            SafeMigrationPolicy[] policies = mode == SafeMigrationScaffoldingMode.Strict
                ? [SafeMigrationPolicy.ThrowIfDifferent]
                : [SafeMigrationPolicy.ThrowIfDifferent, SafeMigrationPolicy.RepairIfSafe];

            foreach (var policy in policies)
            {
                foreach (var populated in constraintStates)
                {
                    foreach (var comment in comments)
                    {
                        yield return [mode, policy, populated, comment];
                    }
                }
            }
        }
    }

    /// <summary>
    /// Moves only table metadata into existing typed callbacks without changing their complete contracts.
    /// </summary>
    /// <param name="mode">The selected strict or legacy authoring mode.</param>
    /// <param name="policy">The frozen convergence policy.</param>
    /// <param name="populated">Whether the table contains every supported nested constraint family.</param>
    /// <param name="comment">The native table comment, including syntax-looking counterprobes.</param>
    [Theory]
    [MemberData(nameof(TableMetadataSourceCases))]
    public void TableMetadataUsesExistingTypedCallbacksAndPreservesLiteralBoundaries(
        SafeMigrationScaffoldingMode mode,
        SafeMigrationPolicy policy,
        bool populated,
        string? comment
    )
    {
        // Arrange
        var original = CreateTableMetadataSource(populated, comment);
        var generator = CreateOperationGenerator(
            mode,
            legacyConvergencePolicy: policy,
            providerOperationAdapters: [new TableMetadataOperationAdapter()]);

        var sourceBuilder = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [original], sourceBuilder);
        var source = sourceBuilder.ToString();
        var reloaded = CompileAndExecuteGeneratedOperations(source);
        var tableOperation = reloaded
            .OfType<SafeMigrationOperation>()
            .Single(static operation => operation.Intent is EnsureTableIntent);

        var expected = ((EnsureTableIntent)tableOperation.Intent).Definition;
        var reconstructed = (CreateTableOperation)SafeMigrationStandardOperationFactory.Create(tableOperation.Intent);

        // Assert
        Assert.Contains(mode == SafeMigrationScaffoldingMode.Strict
                ? ".CreateTableIfNotExists("
                : ".ConvergeTableFromModel(",
            source,
            StringComparison.Ordinal);

        Assert.Contains("constraints: table =>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("operationFactory:", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTableIfNotExistsFromModel", source, StringComparison.Ordinal);
        Assert.Empty(tableOperation.GetAnnotations());
        Assert.True(SafeMigrationDefinitionEquivalence.Table(
            SafeMigrationExpectedDefinitionFactory.From(original),
            expected));

        Assert.Equal(comment, reconstructed.Comment);
        Assert.Equal(original["Test:TableMetadata"], reconstructed["Test:TableMetadata"]);
        Assert.Equal(original.Columns[1].DefaultValue, reconstructed.Columns[1].DefaultValue);
        Assert.Equal(original.Columns.Select(static column => column.Name),
            reconstructed.Columns.Select(static column => column.Name));

        Assert.Equal(populated
                ? 1
                : 0,
            reconstructed.UniqueConstraints.Count);

        Assert.Equal(populated
                ? 1
                : 0,
            reconstructed.CheckConstraints.Count);

        Assert.Equal(populated
                ? 1
                : 0,
            reconstructed.ForeignKeys.Count);

        Assert.Equal(populated, reconstructed.PrimaryKey is not null);
        Assert.Single(original.GetAnnotations());

        if (mode == SafeMigrationScaffoldingMode.LegacyConvergence)
        {
            Assert.Equal(SafeMigrationPolicy.ExistenceOnly, tableOperation.Policy);
            Assert.All(reloaded
                    .OfType<SafeMigrationOperation>()
                    .Where(operation => operation != tableOperation),
                operation => Assert.Equal(policy, operation.Policy));
        }
        else
        {
            Assert.Single(reloaded);
            Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent, tableOperation.Policy);
        }
    }

    /// <summary>
    /// Annotated composite foreign keys retain their ordered endpoints and complete compiled contract.
    /// </summary>
    /// <param name="mode">The selected strict or legacy authoring mode.</param>
    /// <param name="policy">The frozen convergence policy.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, SafeMigrationPolicy.RepairIfSafe)]
    public void TableMetadataCompositeForeignKeyRoundTripPreservesEndpointOrderAndFingerprint(
        SafeMigrationScaffoldingMode mode,
        SafeMigrationPolicy policy
    )
    {
        // Arrange
        var original = CreateTableMetadataSource(populated: true, comment: "composite key ) .CreateTable(");
        original.ForeignKeys[0].Columns = ["class", "id"];
        original.ForeignKeys[0].PrincipalColumns = ["parent_code", "parent_id"];
        var definition = SafeMigrationExpectedDefinitionFactory.From(original);
        var expected = new MigrationBuilder("probe");
        if (mode == SafeMigrationScaffoldingMode.Strict)
        {
            expected.EnsureTable(definition, SafeMigrationTableMode.StrictDefinition, policy);
        }
        else
        {
            expected.ConvergeTable(definition, policy: policy);
        }

        var generator = CreateOperationGenerator(
            mode,
            legacyConvergencePolicy: policy,
            providerOperationAdapters: [new TableMetadataOperationAdapter()]);

        var source = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [original], source);
        var operations = CompileAndExecuteGeneratedOperations(source.ToString());
        var table = operations
            .OfType<SafeMigrationOperation>()
            .Single(static operation => operation.Intent is EnsureTableIntent);

        var capturedDefinition = ((EnsureTableIntent)table.Intent).Definition;
        var captured = (CreateTableOperation)SafeMigrationStandardOperationFactory.Create(table.Intent);
        var fingerprint = SafeMigrationContractFingerprint.Create(operations);
        var expectedFingerprint = SafeMigrationContractFingerprint.Create(expected.Operations);

        // Assert
        var foreignKey = Assert.Single(captured.ForeignKeys);

        Assert.Equal(["class", "id"], foreignKey.Columns);
        Assert.Equal(["parent_code", "parent_id"], Assert.IsType<string[]>(foreignKey.PrincipalColumns));
        Assert.Equal(original.ForeignKeys[0].Name, foreignKey.Name);
        Assert.Equal(original.ForeignKeys[0].PrincipalTable, foreignKey.PrincipalTable);
        Assert.Equal(ReferentialAction.Restrict, foreignKey.OnDelete);
        Assert.Equal(original["Test:TableMetadata"], captured["Test:TableMetadata"]);
        Assert.Empty(table.GetAnnotations());
        Assert.True(SafeMigrationDefinitionEquivalence.Table(definition, capturedDefinition));
        Assert.Equal(expectedFingerprint, fingerprint);
    }

    /// <summary>
    /// Malformed or foreign metadata cannot enter a callback merely because a table is otherwise valid.
    /// </summary>
    /// <param name="mode">The selected strict or legacy authoring mode.</param>
    /// <param name="nested">Whether the rejected metadata is attached to a nested constraint.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, false)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, false)]
    [InlineData(SafeMigrationScaffoldingMode.Strict, true)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, true)]
    public void TableMetadataCallbackRejectsUncertifiedCoordinatesWithoutPublishingSource(
        SafeMigrationScaffoldingMode mode,
        bool nested
    )
    {
        // Arrange
        var original = CreateTableMetadataSource(populated: true, comment: null);
        if (nested)
        {
            original.PrimaryKey!.AddAnnotation("Unknown:ConstraintMetadata", true);
        }
        else
        {
            original.AddAnnotation("Unknown:TableMetadata", true);
        }

        var generator = CreateOperationGenerator(
            mode,
            providerOperationAdapters: [new TableMetadataOperationAdapter()]);

        var output = new IndentedStringBuilder();

        // Act
        var exception = Record.Exception(() => generator.Generate("migrationBuilder", [original], output));

        // Assert
        Assert.IsType<NotSupportedException>(exception);
        Assert.Empty(output.ToString());
    }

    /// <summary>Method-looking literal data also remains ordinary data on the annotation-free table path.</summary>
    /// <param name="mode">The selected strict or legacy authoring mode.</param>
    /// <param name="policy">The frozen legacy convergence policy.</param>
    [Theory]
    [InlineData(SafeMigrationScaffoldingMode.Strict, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, SafeMigrationPolicy.ThrowIfDifferent)]
    [InlineData(SafeMigrationScaffoldingMode.LegacyConvergence, SafeMigrationPolicy.RepairIfSafe)]
    public void UnannotatedTableLiteralContentCannotBecomeAMethodBoundary(
        SafeMigrationScaffoldingMode mode,
        SafeMigrationPolicy policy
    )
    {
        // Arrange
        var original = CreateTableMetadataSource(populated: true, comment: ") .CreateTable( }");
        original.RemoveAnnotation("Test:TableMetadata");
        var generator = CreateOperationGenerator(mode, legacyConvergencePolicy: policy);
        var output = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [original], output);
        var reloaded = CompileAndExecuteGeneratedOperations(output.ToString());
        var table = reloaded
            .OfType<SafeMigrationOperation>()
            .Single(static operation => operation.Intent is EnsureTableIntent);

        var reconstructed = (CreateTableOperation)SafeMigrationStandardOperationFactory.Create(table.Intent);

        // Assert
        Assert.Equal(original.Comment, reconstructed.Comment);
        Assert.Equal(original.Columns[1].DefaultValue, reconstructed.Columns[1].DefaultValue);
        Assert.Empty(reconstructed.GetAnnotations());
        Assert.Empty(table.GetAnnotations());
        Assert.True(SafeMigrationDefinitionEquivalence.Table(
            SafeMigrationExpectedDefinitionFactory.From(original),
            ((EnsureTableIntent)table.Intent).Definition));
    }

    /// <summary>
    /// Native line endings and indentation do not change the frozen table metadata or literal values.
    /// </summary>
    /// <param name="newline">The native generated-source line ending.</param>
    /// <param name="outerIndent">The operation stream's existing indentation depth.</param>
    /// <param name="comment">The exact optional native comment after the constraints body.</param>
    [Theory]
    [InlineData("\n", 0, null)]
    [InlineData("\r\n", 0, "")]
    [InlineData("\n", 2, "ordinary comment")]
    [InlineData("\r\n", 3, "ordinary comment")]
    [InlineData("\n", 1, "}\nconstraints: table =>\n{\n.CreateTable( ) .Annotation(\"fake\", 1)")]
    [InlineData("\r\n", 2, "}\r\nconstraints: table =>\r\n{\r\n.CreateTable( ) .Annotation(\"fake\", 1)")]
    public void TableMetadataRelocationPreservesNativeNewlinesAndIndentation(
        string newline,
        int outerIndent,
        string? comment
    )
    {
        // Arrange
        var original = CreateTableMetadataSource(populated: true, comment);
        var nativeSource = RenderNativeTableSource(original, outerIndent)
            .Replace(Environment.NewLine, newline, StringComparison.Ordinal);

        var generator = CreateOperationGenerator(
            SafeMigrationScaffoldingMode.Strict,
            providerOperationAdapters: [new TableMetadataOperationAdapter()]);

        // Act
        var moved = RelocateTableMetadata(generator, original, nativeSource, outerIndent);
        var invocation = moved.IndexOf(".CreateTable(", StringComparison.Ordinal);
        var safeSource = string.Concat(
            moved.AsSpan(0, invocation),
            ".CreateTableIfNotExists(",
            moved.AsSpan(invocation + ".CreateTable(".Length));

        var operations = CompileAndExecuteGeneratedOperations("migrationBuilder" + safeSource.TrimStart() + ";");
        var safe = (SafeMigrationOperation)operations.Single();
        var captured = (CreateTableOperation)SafeMigrationStandardOperationFactory.Create(safe.Intent);

        // Assert
        Assert.Equal(newline, SafeMigrationGeneratedSource.GetConsistentNewLine(moved));
        Assert.Contains(new string(' ', (outerIndent + 2) * 4) + "table" + newline,
            moved,
            StringComparison.Ordinal);

        Assert.Empty(safe.GetAnnotations());
        Assert.Equal(original["Test:TableMetadata"], captured["Test:TableMetadata"]);
        Assert.Equal(comment, captured.Comment);
        Assert.Equal(original.Columns[1].DefaultValue, captured.Columns[1].DefaultValue);
    }

    /// <summary>
    /// Unexpected native suffixes and callback boundaries reject rather than guessing an insertion point.
    /// </summary>
    /// <param name="corruption">The independently corrupted source-shape boundary.</param>
    /// <param name="newline">The native syntax line ending.</param>
    [Theory]
    [InlineData("annotation_method", "\n")]
    [InlineData("missing_suffix", "\r\n")]
    [InlineData("comment_mismatch", "\n")]
    [InlineData("missing_brace", "\r\n")]
    [InlineData("brace_indent", "\n")]
    [InlineData("duplicate_suffix", "\r\n")]
    [InlineData("root_method", "\n")]
    [InlineData("trailing_statement", "\r\n")]
    public void TableMetadataRelocationRejectsMalformedNativeSource(
        string corruption,
        string newline
    )
    {
        // Arrange
        var original = CreateTableMetadataSource(populated: false, comment: "ordinary comment");
        var nativeSource = RenderNativeTableSource(original, outerIndent: 1)
            .Replace(Environment.NewLine, newline, StringComparison.Ordinal);

        var brace = nativeSource.LastIndexOf("},", StringComparison.Ordinal);
        var annotation = nativeSource.LastIndexOf(".Annotation(", StringComparison.Ordinal);
        var suffix = nativeSource.LastIndexOf(newline, annotation, StringComparison.Ordinal);
        var root = nativeSource.IndexOf(".CreateTable(", StringComparison.Ordinal);
        var damaged = corruption switch
        {
            "annotation_method" => nativeSource
                .Remove(annotation, ".Annotation".Length)
                .Insert(annotation, ".OtherAnnotation"),
            "missing_suffix" => nativeSource[..suffix],
            "comment_mismatch" => nativeSource,
            "missing_brace" => nativeSource.Remove(brace, 1),
            "brace_indent" => nativeSource.Insert(brace, " "),
            "duplicate_suffix" => nativeSource + nativeSource[suffix..],
            "root_method" => nativeSource
                .Remove(root, ".CreateTable".Length)
                .Insert(root, ".OtherTable"),
            "trailing_statement" => nativeSource + ";",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };

        if (corruption == "comment_mismatch")
        {
            original.Comment = "a different comment";
        }

        var generator = CreateOperationGenerator(
            SafeMigrationScaffoldingMode.Strict,
            providerOperationAdapters: [new TableMetadataOperationAdapter()]);

        // Act
        var exception = Record.Exception(() => RelocateTableMetadata(generator, original, damaged, outerIndent: 1));

        // Assert
        var invocation = Assert.IsType<TargetInvocationException>(exception);
        Assert.IsType<InvalidOperationException>(invocation.InnerException);
    }

    /// <summary>Snapshots annotation arrays and restores independent copies after both caller mutations.</summary>
    [Fact]
    public void ProviderColumnAnnotationsAreSnapshottedAndRestoredOnStandardOperations()
    {
        // Arrange
        var prefixLengths = new[] { new[] { 12, 24 } };
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "users",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        };

        operation["Test:ValueGenerationStrategy"] = TestValueGenerationStrategy.Identity;
        operation["Test:PrefixLengths"] = prefixLengths;

        // Act
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);
        prefixLengths[0][0] = 99;
        var standard = SafeMigrationStandardOperationFactory.Create(
            new EnsureColumnIntent("users", definition),
            renderExpression: null,
            renderCollation: null);

        var restoredPrefixLengths = (int[][])standard["Test:PrefixLengths"]!;
        restoredPrefixLengths[0][0] = 48;
        var secondStandard = SafeMigrationStandardOperationFactory.Create(
            new EnsureColumnIntent("users", definition),
            renderExpression: null,
            renderCollation: null);

        // Assert
        Assert.IsType<AddColumnOperation>(standard);
        Assert.IsType<AddColumnOperation>(secondStandard);
        Assert.Equal(TestValueGenerationStrategy.Identity, standard["Test:ValueGenerationStrategy"]);
        Assert.Equal([48, 24], restoredPrefixLengths[0]);
        Assert.Equal([12, 24],
            Assert
                .IsType<int[][]>(secondStandard["Test:PrefixLengths"])[0]);
    }

    /// <summary>Rejects mutable annotation values that cannot be snapshotted safely.</summary>
    [Fact]
    public void MutableProviderAnnotationValue_IsRejectedDuringCapture()
    {
        // Arrange
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "users",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        };

        operation["Test:Mutable"] = new List<int> { 1 };

        // Act
        var exception = Assert.Throws<NotSupportedException>(() =>
            SafeMigrationExpectedDefinitionFactory.From(operation));

        // Assert
        Assert.Contains("cannot be captured immutably", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Fingerprints every supported immutable annotation value deterministically.</summary>
    /// <param name="valueCase">The serializable annotation-value case identifier.</param>
    [Theory]
    [InlineData("string")]
    [InlineData("char")]
    [InlineData("boolean")]
    [InlineData("byte")]
    [InlineData("sbyte")]
    [InlineData("short")]
    [InlineData("ushort")]
    [InlineData("int")]
    [InlineData("uint")]
    [InlineData("long")]
    [InlineData("ulong")]
    [InlineData("decimal")]
    [InlineData("float")]
    [InlineData("double")]
    [InlineData("date")]
    [InlineData("time")]
    [InlineData("datetime")]
    [InlineData("datetimeoffset")]
    [InlineData("timespan")]
    [InlineData("guid")]
    [InlineData("bytes")]
    [InlineData("type")]
    [InlineData("enum")]
    [InlineData("array")]
    public void SupportedProviderAnnotationValueKindsHaveDeterministicFingerprints(
        string valueCase
    )
    {
        // Arrange
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "users",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        };

        operation["Test:Value"] = CreateAnnotationValue(valueCase);

        // Act
        var firstCapture = SafeMigrationProviderAnnotation.Capture(operation);
        var secondCapture = SafeMigrationProviderAnnotation.Capture(operation);

        // Assert
        var first = Assert.Single(firstCapture);
        var second = Assert.Single(secondCapture);

        Assert.Equal(64, first.Fingerprint.Length);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    /// <summary>Rejects each unsupported array shape before any annotation can be captured.</summary>
    /// <param name="nonZeroBased">Whether the array has a nonzero lower bound rather than multiple dimensions.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidProviderAnnotationArrayShapesAreRejected(
        bool nonZeroBased
    )
    {
        // Arrange
        var value = nonZeroBased
            ? Array.CreateInstance(typeof(int), [1], [1])
            : new int[1, 1];

        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "users",
            ClrType = typeof(int),
            ColumnType = "integer",
            IsNullable = false,
        };

        operation["Test:Array"] = value;

        // Act
        var exception = Record.Exception(() => SafeMigrationProviderAnnotation.Capture(operation));

        // Assert
        var unsupported = Assert.IsType<NotSupportedException>(exception);

        Assert.Contains("one-dimensional and zero-based", unsupported.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects a null annotation source at the capture boundary.</summary>
    [Fact]
    public void NullProviderAnnotationSourceIsRejected()
    {
        // Arrange
        MigrationOperation? operation = null;

        // Act
        var exception = Record.Exception(() => SafeMigrationProviderAnnotation.Capture(operation!));

        // Assert
        Assert.IsType<ArgumentNullException>(exception);
    }

    /// <summary>
    /// Creates a native table whose literal payloads cannot be confused with generated method syntax.
    /// </summary>
    private static CreateTableOperation CreateTableMetadataSource(
        bool populated,
        string? comment
    )
    {
        var operation = CreateTable();
        operation.Comment = comment;
        operation.AddAnnotation("Test:TableMetadata", "metadata ) } .CreateTable( \"quoted\" \\value");
        operation.Columns.Add(new AddColumnOperation
        {
            Name = "class",
            Table = operation.Name,
            ClrType = typeof(string),
            ColumnType = "varchar(255)",
            IsNullable = false,
            DefaultValue = "literal ) } .CreateTable( .Annotation( constraints: table => \\value\"",
        });

        if (populated)
        {
            operation.PrimaryKey = new AddPrimaryKeyOperation
            {
                Name = "PK_users",
                Table = operation.Name,
                Columns = ["id"],
            };

            operation.UniqueConstraints.Add(new AddUniqueConstraintOperation
            {
                Name = "UQ_users_class",
                Table = operation.Name,
                Columns = ["class"],
            });

            operation.CheckConstraints.Add(new AddCheckConstraintOperation
            {
                Name = "CK_users_id",
                Table = operation.Name,
                Sql = "id >= 0",
            });

            operation.ForeignKeys.Add(new AddForeignKeyOperation
            {
                Name = "FK_users_parent",
                Table = operation.Name,
                Columns = ["id"],
                PrincipalTable = "parents",
                PrincipalColumns = ["id"],
                OnDelete = ReferentialAction.Restrict,
            });
        }

        return operation;
    }

    /// <summary>Renders the genuine protected EF operation shape without routing through the safe override.</summary>
    private static string RenderNativeTableSource(
        CreateTableOperation operation,
        int outerIndent
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITypeMappingSource, NullTypeMappingSource>();
        services.AddEntityFrameworkDesignTimeServices();
        using var provider = services.BuildServiceProvider();
        var renderer = new NativeTableSourceRenderer(
            provider.GetRequiredService<CSharpMigrationOperationGeneratorDependencies>());

        var output = new IndentedStringBuilder();

        // WHY: Reproduce the native operation stream's actual indentation without changing its source payload.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < outerIndent; index++)
        {
            output.IncrementIndent();
        }

        renderer.Render(operation, output);

        return output.ToString();
    }

    /// <summary>Exercises the private native-shape guard without adding a public production testing seam.</summary>
    private static string RelocateTableMetadata(
        SafeMigrationCSharpMigrationOperationGenerator generator,
        CreateTableOperation operation,
        string source,
        int outerIndent
    )
    {
        var method = typeof(SafeMigrationCSharpMigrationOperationGenerator).GetMethod(
                "MoveTableAnnotationsIntoConstraints",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The internal table-annotation relocation guard is missing.");

        return (string)method.Invoke(generator, [operation, source, outerIndent])!;
    }

    /// <summary>Creates the original value for each serializable immutable-annotation theory case.</summary>
    private static object CreateAnnotationValue(
        string valueCase
    ) => valueCase switch
    {
        "string" => "value",
        "char" => 'v',
        "boolean" => true,
        "byte" => (byte)1,
        "sbyte" => (sbyte)-1,
        "short" => (short)-2,
        "ushort" => (ushort)2,
        "int" => -3,
        "uint" => (uint)3,
        "long" => -4L,
        "ulong" => 4UL,
        "decimal" => 1.25m,
        "float" => 1.5f,
        "double" => 1.75d,
        "date" => new DateOnly(2026, 8, 27),
        "time" => new TimeOnly(12, 34, 56),
        "datetime" => new DateTime(
            2026,
            8,
            27,
            12,
            34,
            56,
            DateTimeKind.Utc),
        "datetimeoffset" => new DateTimeOffset(
            2026,
            8,
            27,
            12,
            34,
            56,
            TimeSpan.FromHours(2)),
        "timespan" => TimeSpan.FromMinutes(5),
        "guid" => Guid.Parse("1ea905b8-9114-46f1-811d-9db2ba394f31"),
        "bytes" => new byte[] { 1, 2, 3 },
        "type" => typeof(string),
        "enum" => TestValueGenerationStrategy.Identity,
        "array" => new[] { 1, 2, 3 },
        _ => throw new ArgumentOutOfRangeException(nameof(valueCase), valueCase, "Unknown annotation value."),
    };

    /// <summary>Certifies only the immutable metadata coordinate owned by the source-generation probe.</summary>
    private sealed class TableMetadataOperationAdapter : ISafeMigrationProviderOperationAdapter
    {
        /// <summary>Creates the stateless adapter for the single probe-owned metadata coordinate.</summary>
        public TableMetadataOperationAdapter() { }

        /// <inheritdoc />
        public bool TryNormalize(
            MigrationOperation operation,
            [NotNullWhen(true)] out SafeMigrationOperation? safeOperation
        )
        {
            safeOperation = operation is CreateTableOperation table
                && table["Test:TableMetadata"] is string
                && table
                    .GetAnnotations()
                    .All(static annotation => annotation.Name == "Test:TableMetadata")
                    ? new SafeMigrationOperation(
                        new EnsureTableIntent(
                            SafeMigrationExpectedDefinitionFactory.From(table),
                            SafeMigrationTableMode.StrictDefinition),
                        SafeMigrationPolicy.ThrowIfDifferent)
                    : null;

            return safeOperation is not null;
        }

        /// <inheritdoc />
        public bool IsCertifiedPassthrough(
            MigrationOperation operation
        ) => false;
    }

    /// <summary>Exposes only the native EF table renderer to the source-shape counterprobes.</summary>
    private sealed class NativeTableSourceRenderer : CSharpMigrationOperationGenerator
    {
        /// <summary>Uses the same EF literal helpers and type mapping as the surrounding scaffolding probes.</summary>
        /// <param name="dependencies">The owned native generation dependencies.</param>
        public NativeTableSourceRenderer(
            CSharpMigrationOperationGeneratorDependencies dependencies
        ) : base(dependencies) { }

        /// <summary>Writes one native EF table call, preserving its normal indentation and annotation suffix.</summary>
        /// <param name="operation">The complete native table operation.</param>
        /// <param name="output">The native operation output buffer.</param>
        public void Render(
            CreateTableOperation operation,
            IndentedStringBuilder output
        ) => Generate(operation, output);
    }

    private enum TestValueGenerationStrategy
    {
        Identity = 1,
    }
}
