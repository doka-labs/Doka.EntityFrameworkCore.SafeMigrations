namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies narrow provider column certificates and their lifetime across physical key changes.</summary>
public sealed class MySqlKeyChangeColumnProofTests
{
    /// <summary>Qualified key changes and column DROP retain surviving intrinsic column sources.</summary>
    /// <param name="change">The independently classified structural operation.</param>
    /// <param name="expected">Whether the provider certifies intrinsic column preservation.</param>
    [Theory]
    [InlineData("primary-drop", true)]
    [InlineData("foreign-drop", true)]
    [InlineData("implicit-index", true)]
    [InlineData("btree-index", true)]
    [InlineData("lowercase-btree-index", true)]
    [InlineData("descending-index", true)]
    [InlineData("unique-index", false)]
    [InlineData("filtered-index", false)]
    [InlineData("structured-filter-index", false)]
    [InlineData("functional-index", false)]
    [InlineData("structured-functional-index", false)]
    [InlineData("hash-index", false)]
    [InlineData("fulltext-index", false)]
    [InlineData("spatial-index", false)]
    [InlineData("included-index", false)]
    [InlineData("collated-index", false)]
    [InlineData("operator-index", false)]
    [InlineData("prefix-index", false)]
    [InlineData("null-order-index", false)]
    [InlineData("primary-create", false)]
    [InlineData("foreign-create", false)]
    [InlineData("index-drop", false)]
    [InlineData("column-drop", true)]
    public void IntrinsicColumnSourceCertificateAcceptsOnlyQualifiedKeyChanges(string change, bool expected)
    {
        // Arrange
        using var context = CreateContext();
        var provider = GetColumnAnalyzer(context);
        var intent = KeyChange(change);

        // Act
        var result = provider.PreservesIntrinsicColumnSources(intent);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Ensure requires to be satisfied matching or complete nullability-only non-data repair diagnostics.</summary>
    /// <param name="evidence">The captured provider state, facets or independent blocker.</param>
    /// <param name="expected">Whether the exact captured Ensure certificate is reusable.</param>
    [Theory]
    [InlineData("matching", true)]
    [InlineData("matching-unsatisfied", false)]
    [InlineData("matching-unsupported-target", false)]
    [InlineData("nullability", true)]
    [InlineData("multiple-nullability", true)]
    [InlineData("nullability-row-proof", true)]
    [InlineData("empty", false)]
    [InlineData("column_type", false)]
    [InlineData("column_default", false)]
    [InlineData("column_collation", false)]
    [InlineData("column_comment", false)]
    [InlineData("column_annotation", false)]
    [InlineData("column_nullabilty", false)]
    [InlineData("column_nullability_other", false)]
    [InlineData("mixed-type", false)]
    [InlineData("mutates-data", false)]
    [InlineData("unsafe", false)]
    [InlineData("unsupported", false)]
    [InlineData("data-blocked", false)]
    [InlineData("missing", false)]
    [InlineData("unknown", false)]
    [InlineData("unsupported-target", false)]
    public void EnsureKeyChangeCertificateRequiresCompleteNativeNullabilityEvidence(string evidence, bool expected)
    {
        // Arrange
        using var context = CreateContext();
        var provider = GetColumnAnalyzer(context);
        var intent = new EnsureColumnIntent("items", Column("reference_id", nullable: true,
            drift: evidence.Contains("unsupported-target", StringComparison.Ordinal) ? "annotation" : "none"));
        var state = evidence switch
        {
            "matching" or "matching-unsatisfied" or "matching-unsupported-target"
                => SafeMigrationObservedState.Matching,
            "unsupported" => SafeMigrationObservedState.Unsupported,
            "data-blocked" => SafeMigrationObservedState.DataBlocked,
            "missing" => SafeMigrationObservedState.Missing,
            "unknown" => SafeMigrationObservedState.PrerequisiteMissing,
            _ => SafeMigrationObservedState.Different,
        };

        var differences = evidence switch
        {
            "empty" => Array.Empty<SafeMigrationFacetDifference>(),
            "multiple-nullability" => [Difference("column_nullability"), Difference("column_nullability")],
            "mixed-type" => [Difference("column_nullability"), Difference("column_type")],
            "column_type" or "column_default" or "column_collation" or "column_comment" or "column_annotation"
                or "column_nullabilty" or "column_nullability_other"
                => [Difference(evidence)],
            _ => new[] { Difference("column_nullability"), },
        };

        var live = new SafeMigrationProviderAnalysis(state,
            state == SafeMigrationObservedState.Different && evidence != "unsafe"
                ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            evidence is "matching" or "matching-unsupported-target", "captured_ensure_certificate",
            SafeMigrationOperationalImpact.TableRewritePossible, differences)
        {
            RepairMutatesData = evidence == "mutates-data",
            RequiresLiveDataProof = evidence == "nullability-row-proof",
        };

        // Act
        var result = provider.CanReuseLiveColumnProofAfterKeyChanges(intent, live);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Alter certificates permit exact nullability changes but never widening or data backfill.</summary>
    /// <param name="transition">The source, target or captured provider boundary under examination.</param>
    /// <param name="expected">Whether the independently analyzed Alter certificate is reusable.</param>
    [Theory]
    [InlineData("relaxation", true)]
    [InlineData("tightening", true)]
    [InlineData("matching", true)]
    [InlineData("matching-unsatisfied", false)]
    [InlineData("source-missing", false)]
    [InlineData("same-nullability", false)]
    [InlineData("widening", false)]
    [InlineData("store-type", false)]
    [InlineData("default", false)]
    [InlineData("collation", false)]
    [InlineData("comment", false)]
    [InlineData("annotation", false)]
    [InlineData("mutates-data", false)]
    [InlineData("guid-backfill", false)]
    [InlineData("unsupported", false)]
    [InlineData("data-blocked", false)]
    [InlineData("unsafe", false)]
    public void AlterKeyChangeCertificateRetainsExactNullabilityAndDataBoundaries(string transition, bool expected)
    {
        // Arrange
        using var context = CreateContext();
        var provider = GetColumnAnalyzer(context);
        var nullableSource = transition is "tightening" or "guid-backfill";
        var source = transition == "widening"
            ? new ExpectedColumnDefinition("reference_id", typeof(string), false, "varchar(36)", maxLength: 36)
            : Column("reference_id", nullableSource);
        var target = transition == "widening"
            ? new ExpectedColumnDefinition("reference_id", typeof(string), true, "varchar(72)", maxLength: 72)
            : Column("reference_id", transition == "same-nullability" ? nullableSource : !nullableSource,
                drift: transition == "guid-backfill" ? "default" : transition);
        var intent = new AlterColumnIntent("items", target, transition == "source-missing" ? null : source);
        var state = transition switch
        {
            "matching" or "matching-unsatisfied" => SafeMigrationObservedState.Matching,
            "unsupported" => SafeMigrationObservedState.Unsupported,
            "data-blocked" => SafeMigrationObservedState.DataBlocked,
            _ => SafeMigrationObservedState.Different,
        };

        var live = new SafeMigrationProviderAnalysis(state,
            state == SafeMigrationObservedState.Different && transition != "unsafe"
                ? SafeMigrationRepairCapability.Safe : SafeMigrationRepairCapability.None,
            transition == "matching", "captured_alter_certificate")
        {
            RepairMutatesData = transition is "mutates-data" or "guid-backfill",
            RequiresLiveDataProof = nullableSource,
        };

        // Act
        var result = provider.CanReuseLiveColumnProofAfterKeyChanges(intent, live);

        // Assert
        Assert.Equal(expected, result);
    }

    /// <summary>Index creation and PRIMARY removal retain independently current sibling column certificates.</summary>
    /// <param name="ensure">Whether the first and sibling operations use source-free Ensure.</param>
    /// <param name="matchingSibling">Whether the sibling already satisfies its target in the live catalog.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void QualifiedKeyHistoryPreservesMatchingAndRepairSiblingCertificates(bool ensure, bool matchingSibling)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new EnsureColumnIntent("items", Column("left_id", nullable: false)),
            ColumnProof(matching: true));
        Accept(projection, new EnsureColumnIntent("items", Column("right_id", nullable: matchingSibling)),
            ColumnProof(matching: true));
        AcceptKeyChange(projection, "implicit-index");
        AcceptKeyChange(projection, "primary-drop");
        AcceptKeyChange(projection, "foreign-drop");
        var first = NullabilityOperation("left_id", ensure);
        var firstLive = ColumnProof();
        var firstResult = projection.Project(first, firstLive);
        var firstDecision = SafeMigrationDecisionPlanner.Plan(first.Intent.Kind, firstResult.ObservedState,
            first.Policy, firstResult.RepairCapability);
        var sibling = NullabilityOperation("right_id", ensure);
        var siblingLive = ColumnProof(matchingSibling);

        // Act
        projection.Observe(first, firstLive, firstResult, firstDecision);
        var result = projection.Project(sibling, siblingLive);

        // Assert
        Assert.Equal(SafeMigrationAction.Repair, firstDecision.Action);
        Assert.Equal(matchingSibling ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            result.ObservedState);
        Assert.Equal(matchingSibling ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            result.RepairCapability);
        Assert.Equal(matchingSibling, result.PostconditionSatisfied);
        Assert.Equal(0L, projection.DataMutationVersion);
        Assert.Equal(ensure && !matchingSibling, result.RepairPreservesIntrinsicColumnLayout);
    }

    /// <summary>
    /// Known source facets cannot be replaced by authored old metadata after column-preserving key changes.
    /// </summary>
    /// <param name="drift">The accepted source facet that differs from the authored old contract.</param>
    [Theory]
    [InlineData("store-type")]
    [InlineData("default")]
    [InlineData("collation")]
    [InlineData("comment")]
    [InlineData("annotation")]
    public void KeyHistoryDoesNotReplaceAcceptedSourceFacetsWithAuthoredOldMetadata(string drift)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        Accept(projection, new EnsureColumnIntent("items", Column("reference_id", nullable: false, drift: drift)),
            ColumnProof(matching: true));
        AcceptKeyChange(projection, "implicit-index");
        AcceptKeyChange(projection, "primary-drop");
        var operation = NullabilityOperation("reference_id", ensure: false);

        // Act
        var result = projection.Project(operation, ColumnProof());

        // Assert
        Assert.NotEqual(SafeMigrationRepairCapability.Safe, result.RepairCapability);
        Assert.False(result.PostconditionSatisfied);
    }

    /// <summary>
    /// Later FK or PRIMARY removals never revive source certificates invalidated earlier in the stream.
    /// </summary>
    /// <param name="mutation">The preceding row, structure or source invalidation.</param>
    /// <param name="removal">The later accepted removal that must not reset the invalidated lifetime.</param>
    [Theory]
    [InlineData("data", "foreign-drop")]
    [InlineData("data", "primary-drop")]
    [InlineData("raw", "foreign-drop")]
    [InlineData("raw", "primary-drop")]
    [InlineData("table", "foreign-drop")]
    [InlineData("table", "primary-drop")]
    [InlineData("column", "foreign-drop")]
    [InlineData("column", "primary-drop")]
    [InlineData("unique-index", "foreign-drop")]
    [InlineData("unique-index", "primary-drop")]
    [InlineData("index-drop", "foreign-drop")]
    [InlineData("index-drop", "primary-drop")]
    [InlineData("widening", "foreign-drop")]
    [InlineData("widening", "primary-drop")]
    public void LaterKeyRemovalCannotReviveInvalidatedColumnSources(string mutation, string removal)
    {
        // Arrange
        using var context = CreateContext();
        var projection = CreateProjection(context);
        AcceptKeyChange(projection, "implicit-index");
        Invalidate(projection, mutation);
        var operation = NullabilityOperation("reference_id", ensure: false);
        var live = ColumnProof();

        // Act
        AcceptKeyChange(projection, removal);
        var result = projection.Project(operation, live);

        // Assert
        Assert.NotEqual(SafeMigrationRepairCapability.Safe, result.RepairCapability);
        Assert.False(result.PostconditionSatisfied);
        Assert.NotSame(live, result);
    }

    /// <summary>Reads the real registered provider callback without opening a database connection.</summary>
    /// <param name="context">The offline provider service scope.</param>
    /// <returns>The native projected-column analyzer.</returns>
    private static ISafeMigrationProjectedColumnAnalyzer GetColumnAnalyzer(DbContext context)
        => (ISafeMigrationProjectedColumnAnalyzer)context.GetService<ISafeMigrationProviderAnalyzer>();

    /// <summary>Builds Core projection with the real provider's physical identity and column callbacks.</summary>
    /// <param name="context">The offline provider service scope.</param>
    /// <returns>The invocation-local ordered projection.</returns>
    private static SafeMigrationPreflightProjection CreateProjection(DbContext context)
    {
        var analyzer = context.GetService<ISafeMigrationProviderAnalyzer>();

        return new SafeMigrationPreflightProjection(
            providerOperationProjection: (ISafeMigrationProviderOperationProjection)analyzer,
            objectIdentityNormalizer: (ISafeMigrationProviderObjectIdentityNormalizer)analyzer,
            projectedColumnAnalyzer: (ISafeMigrationProjectedColumnAnalyzer)analyzer);
    }

    /// <summary>Creates the independently classified removal, index or dependency contract.</summary>
    /// <param name="change">The exact contract variant exercised by the certificate boundary.</param>
    /// <returns>The immutable operation intent, with no fabricated table convergence.</returns>
    private static SafeMigrationIntent KeyChange(string change)
        => change switch
        {
            "primary-drop" => new DropPrimaryKeyIntent("pk_items", "items"),
            "foreign-drop" => new DropForeignKeyIntent("fk_items_parent", "items"),
            "primary-create" => new EnsurePrimaryKeyIntent(
                new ExpectedPrimaryKeyDefinition("pk_items", "items", ["reference_id"])),
            "foreign-create" => new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                "fk_items_parent", "items", ["reference_id"], "parents", ["id"])),
            "index-drop" => new DropIndexIntent("ix_reference", "items"),
            "column-drop" => new DropColumnIntent("other_id", "items"),
            _ => new EnsureIndexIntent(Index(change)),
        };

    /// <summary>Builds index contracts whose independently controlled facets narrow provider certification.</summary>
    /// <param name="shape">The ordinary or disallowed native index shape.</param>
    /// <returns>The immutable index definition.</returns>
    private static ExpectedIndexDefinition Index(string shape)
    {
        var key = shape switch
        {
            "functional-index" => new ExpectedIndexKeyDefinition(expression: "LOWER(`reference_id`)"),
            "structured-functional-index" => new ExpectedIndexKeyDefinition(
                structuredExpression: SafeMigrationSql.Function("LOWER", SafeMigrationSql.Identifier("reference_id"))),
            "descending-index" => new ExpectedIndexKeyDefinition("reference_id",
                sortOrder: SafeMigrationIndexSortOrder.Descending),
            "collated-index" => new ExpectedIndexKeyDefinition("reference_id",
                collation: new SafeMigrationCollationIdentifier("ascii_bin")),
            "operator-index" => new ExpectedIndexKeyDefinition("reference_id", operatorClass: "text_ops"),
            "prefix-index" => new ExpectedIndexKeyDefinition("reference_id", prefixLength: 12),
            "null-order-index" => new ExpectedIndexKeyDefinition("reference_id",
                nullOrder: SafeMigrationIndexNullOrder.First),
            _ => new ExpectedIndexKeyDefinition("reference_id"),
        };

        var method = shape switch
        {
            "btree-index" => "BTREE",
            "lowercase-btree-index" => "btree",
            "hash-index" => "HASH",
            "fulltext-index" => "FULLTEXT",
            "spatial-index" => "SPATIAL",
            _ => null,
        };

        return new ExpectedIndexDefinition("ix_reference", "items", [key], unique: shape == "unique-index",
            filter: shape == "filtered-index" ? "`reference_id` IS NOT NULL" : null,
            includedColumns: shape == "included-index" ? ["other_id"] : null, method: method,
            structuredFilter: shape == "structured-filter-index" ? SafeMigrationSql.Literal(true) : null);
    }

    /// <summary>Records an accepted immutable provider observation through the production decision planner.</summary>
    /// <param name="projection">The ordered projection receiving the accepted result.</param>
    /// <param name="intent">The exact accepted operation intent.</param>
    /// <param name="live">The independently captured provider evidence.</param>
    private static void Accept(
        SafeMigrationPreflightProjection projection,
        SafeMigrationIntent intent,
        SafeMigrationProviderAnalysis live
    )
    {
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var decision = SafeMigrationDecisionPlanner.Plan(intent.Kind, live.ObservedState,
            operation.Policy, live.RepairCapability);

        projection.Observe(operation, live, live, decision);
    }

    /// <summary>Accepts a real key change while binding removals to their observed physical object names.</summary>
    /// <param name="projection">The ordered projection receiving the key change.</param>
    /// <param name="change">The exact removal or creation contract.</param>
    private static void AcceptKeyChange(SafeMigrationPreflightProjection projection, string change)
    {
        var intent = KeyChange(change);
        var live = new SafeMigrationProviderAnalysis(
            intent is DropPrimaryKeyIntent or DropForeignKeyIntent or DropIndexIntent
                ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing,
            SafeMigrationRepairCapability.None, false, "accepted_key_change")
        {
            MatchedObjectName = intent.ObjectName,
        };

        Accept(projection, intent, live);
    }

    /// <summary>Introduces an uncertified source or row effect before a later bounded key removal.</summary>
    /// <param name="projection">The ordered projection whose evidence lifetime is invalidated.</param>
    /// <param name="mutation">The exact invalidating effect.</param>
    private static void Invalidate(SafeMigrationPreflightProjection projection, string mutation)
    {
        switch (mutation)
        {
            case "data":
                projection.ObserveProviderPostcondition(new InsertDataOperation
                {
                    Table = "other_items",
                    Columns = ["id"],
                    Values = new object?[,] { { 1 } },
                });

                break;
            case "raw":
                projection.ObserveProviderPostcondition(new SqlOperation { Sql = "SELECT 1;" });
                break;
            case "table":
                projection.ObserveProviderPostcondition(new AlterTableOperation { Name = "items" });
                break;
            case "column":
                projection.ObserveProviderPostcondition(new AlterColumnOperation
                {
                    Table = "items",
                    Name = "other_id",
                    ClrType = typeof(int),
                    ColumnType = "int",
                    IsNullable = true,
                });

                break;
            case "unique-index":
            case "index-drop":
                AcceptKeyChange(projection, mutation);
                break;
            case "widening":
                Accept(projection, new AlterColumnIntent("items",
                    new ExpectedColumnDefinition("other_id", typeof(string), false, "varchar(72)", maxLength: 72),
                    new ExpectedColumnDefinition("other_id", typeof(string), false, "varchar(36)", maxLength: 36)),
                    ColumnProof());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "Unknown source invalidation.");
        }
    }

    /// <summary>Creates an exact same-layout nullable operation for a generic column.</summary>
    /// <param name="name">The column whose own source lifetime is tested.</param>
    /// <param name="ensure">Whether to use Ensure without an authored old definition.</param>
    /// <returns>The immutable repair operation.</returns>
    private static SafeMigrationOperation NullabilityOperation(string name, bool ensure)
        => new(ensure ? new EnsureColumnIntent("items", Column(name, nullable: true))
                : new AlterColumnIntent("items", Column(name, nullable: true), Column(name, nullable: false)),
            SafeMigrationPolicy.RepairIfSafe);

    /// <summary>Creates supported generic GUID storage or one explicit independent source-facet drift.</summary>
    /// <param name="name">The generic column identifier.</param>
    /// <param name="nullable">The exact reviewed nullability.</param>
    /// <param name="drift">The optional independently changed physical or annotation facet.</param>
    /// <returns>The immutable provider-neutral column definition.</returns>
    private static ExpectedColumnDefinition Column(string name, bool nullable, string drift = "none")
        => new(name, typeof(Guid), nullable, drift == "store-type" ? "binary(16)" : "char(36)",
            collation: drift == "collation" ? new SafeMigrationCollationIdentifier("utf8mb4_bin") : null,
            comment: drift == "comment" ? "changed" : null,
            defaultValue: drift == "default" ? SafeMigrationDefaultValue.Literal(Guid.Empty) : null)
        {
            ProviderAnnotations = drift == "annotation" ? UnsupportedAnnotations() : [],
        };

    /// <summary>Captures unsupported metadata through the real provider-annotation immutability boundary.</summary>
    /// <returns>The independently unsupported immutable metadata.</returns>
    private static IReadOnlyList<SafeMigrationProviderAnnotation> UnsupportedAnnotations()
    {
        var operation = new AddColumnOperation();
        operation.AddAnnotation("Test:Unknown", true);

        return SafeMigrationProviderAnnotation.Capture(operation);
    }

    /// <summary>Creates one bounded facet diagnostic with metadata-only expected and actual values.</summary>
    /// <param name="facet">The exact diagnostic facet under examination.</param>
    /// <returns>The immutable native facet difference.</returns>
    private static SafeMigrationFacetDifference Difference(string facet) => new(facet, "expected", "actual");

    /// <summary>Represents independently captured matching or safe non-data nullability evidence.</summary>
    /// <param name="matching">Whether the exact target already matches the immutable live catalog.</param>
    /// <returns>The provider certificate, not an authored source declaration.</returns>
    private static SafeMigrationProviderAnalysis ColumnProof(bool matching = false)
        => new(matching ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            matching ? SafeMigrationRepairCapability.None : SafeMigrationRepairCapability.Safe,
            matching, "captured_column_certificate", SafeMigrationOperationalImpact.TableRewritePossible,
            matching ? [] : [Difference("column_nullability")]);

    /// <summary>Creates the standard offline provider service scope without a live server or container.</summary>
    /// <returns>The disposable context carrying production provider services.</returns>
    private static DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
                MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations()
            .Options;

        return new DbContext(options);
    }
}
