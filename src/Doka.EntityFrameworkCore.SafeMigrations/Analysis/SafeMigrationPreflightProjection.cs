namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private readonly Dictionary<TableKey, ProjectedTable> _tables = [];

    // Strict table projections retain complete definitions. This second view
    // records only prerequisites proven by earlier convergence operations, so
    // a later operation cannot infer safety from an object that was rejected.
    private readonly Dictionary<TableKey, ProjectedPrerequisites> _prerequisites = [];

    public SafeMigrationProviderAnalysis Project(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis liveAnalysis
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(liveAnalysis);

        return operation.Intent switch
        {
            EnsureSchemaIntent value => Project(value, liveAnalysis),
            DropSchemaIntent value => Project(value, liveAnalysis),
            EnsureTableIntent value => Project(value, liveAnalysis),
            DropTableIntent value => Project(value, liveAnalysis),
            RenameTableIntent value => Project(value, liveAnalysis),
            EnsureColumnIntent value => Project(value, liveAnalysis),
            AlterColumnIntent value => Project(value, liveAnalysis),
            DropColumnIntent value => Project(value, liveAnalysis),
            RenameColumnIntent value => Project(value, liveAnalysis),
            EnsureIndexIntent value => Project(value, liveAnalysis),
            DropIndexIntent value => Project(value, liveAnalysis),
            RenameIndexIntent value => Project(value, liveAnalysis),
            EnsurePrimaryKeyIntent value => Project(value, liveAnalysis),
            DropPrimaryKeyIntent value => Project(value, liveAnalysis),
            EnsureUniqueConstraintIntent value => Project(value, liveAnalysis),
            DropUniqueConstraintIntent value => Project(value, liveAnalysis),
            EnsureCheckConstraintIntent value => Project(value, liveAnalysis),
            DropCheckConstraintIntent value => Project(value, liveAnalysis),
            EnsureForeignKeyIntent value => Project(value, liveAnalysis),
            DropForeignKeyIntent value => Project(value, liveAnalysis),
            _ => liveAnalysis,
        };
    }

    public void Observe(
        SafeMigrationOperation operation,
        SafeMigrationProviderAnalysis analysis,
        SafeMigrationDecision decision
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(decision);

        if (decision.Action is SafeMigrationAction.RejectDifferent
            or SafeMigrationAction.RejectUnsupported
            or SafeMigrationAction.RejectDataBlocked
            or SafeMigrationAction.RejectPrerequisiteMissing)
        {
            return;
        }

        // Preflight never mutates the database. Accepted operations instead
        // update this in-memory catalog so later operations observe prior ones.
        switch (operation.Intent)
        {
            case EnsureSchemaIntent value:
                Observe(value, decision);
                break;
            case DropSchemaIntent value:
                Observe(value, decision);
                break;
            case EnsureTableIntent value:
                Observe(value, analysis, decision);
                break;
            case DropTableIntent value:
                Observe(value, decision);
                break;
            case RenameTableIntent value:
                Observe(value, decision);
                break;
            case EnsureColumnIntent value:
                Observe(value, decision);
                break;
            case AlterColumnIntent value:
                Observe(value, decision);
                break;
            case DropColumnIntent value:
                Observe(value, decision);
                break;
            case RenameColumnIntent value:
                Observe(value, decision);
                break;
            case EnsureIndexIntent value:
                Observe(value, decision);
                break;
            case DropIndexIntent value:
                Observe(value, decision);
                break;
            case RenameIndexIntent value:
                Observe(value, decision);
                break;
            case EnsurePrimaryKeyIntent value:
                Observe(value, decision);
                break;
            case DropPrimaryKeyIntent value:
                Observe(value, decision);
                break;
            case EnsureUniqueConstraintIntent value:
                Observe(value, decision);
                break;
            case DropUniqueConstraintIntent value:
                Observe(value, decision);
                break;
            case EnsureCheckConstraintIntent value:
                Observe(value, decision);
                break;
            case DropCheckConstraintIntent value:
                Observe(value, decision);
                break;
            case EnsureForeignKeyIntent value:
                Observe(value, decision);
                break;
            case DropForeignKeyIntent value:
                Observe(value, decision);
                break;
        }
    }

    private bool Contains(
        string table,
        string? schema
    ) => _tables.ContainsKey(new TableKey(table, schema));

    private bool TryGet(
        string table,
        string? schema,
        [NotNullWhen(true)] out ProjectedTable? projection
    ) => _tables.TryGetValue(new TableKey(table, schema), out projection);

    private static SafeMigrationProviderAnalysis AnalyzeDefinition<T>(
        IReadOnlyDictionary<string, T> definitions,
        string name,
        T expected,
        Func<T, T, bool> equals,
        SafeMigrationRepairCapability repairCapability = SafeMigrationRepairCapability.None
    )
        where T : class
    {
        if (!definitions.TryGetValue(name, out var actual))
        {
            return Analysis(SafeMigrationObservedState.Missing, repairCapability);
        }

        return Analysis(
            equals(actual, expected) ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different,
            repairCapability);
    }

    private static SafeMigrationProviderAnalysis AnalyzeOptional<T>(
        T? actual,
        T expected,
        Func<T, T, bool> equals
    )
        where T : class
    {
        if (actual is null)
        {
            return Analysis(SafeMigrationObservedState.Missing);
        }

        return Analysis(
            equals(actual, expected) ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Different);
    }

    private static SafeMigrationProviderAnalysis Analysis(
        SafeMigrationObservedState state,
        SafeMigrationRepairCapability repairCapability = SafeMigrationRepairCapability.None
    ) => new(state, repairCapability, state == SafeMigrationObservedState.Matching, $"projected_{StateCode(state)}");

    private static string StateCode(
        SafeMigrationObservedState state
    ) => state switch
    {
        SafeMigrationObservedState.Missing => "missing",
        SafeMigrationObservedState.Matching => "matching",
        SafeMigrationObservedState.Different => "different",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private readonly record struct TableKey(
        string Table,
        string? Schema
    );

    private sealed class ProjectedPrerequisites(
        bool newlyCreated
    )
    {
        public Dictionary<string, ProjectedColumn> Columns { get; } = new(StringComparer.Ordinal);

        public bool NewlyCreated { get; } = newlyCreated;
    }

    private sealed record ProjectedColumn(
        ExpectedColumnDefinition Definition,
        bool AddedToExistingTable
    );

    private sealed partial class ProjectedTable
    {
        private readonly List<string> _columnOrder;
        private string _table;
        private string? _schema;
        private readonly string? _comment;

        public ProjectedTable(
            ExpectedTableDefinition definition
        )
        {
            _table = definition.Table;
            _schema = definition.Schema;
            _comment = definition.Comment;
            _columnOrder = definition
                .Columns
                .Select(static value => value.Name)
                .ToList();

            Columns = definition.Columns.ToDictionary(static value => value.Name, StringComparer.Ordinal);
            PrimaryKey = definition.PrimaryKey;

            UniqueConstraints = definition.UniqueConstraints.ToDictionary(
                static value => value.Name,
                StringComparer.Ordinal);

            CheckConstraints = definition.CheckConstraints.ToDictionary(
                static value => value.Name,
                StringComparer.Ordinal);

            ForeignKeys = definition.ForeignKeys.ToDictionary(static value => value.Name, StringComparer.Ordinal);
        }

        public ExpectedTableDefinition Definition =>
            new(
                _table,
                _columnOrder.Select(name => Columns[name]),
                _schema,
                _comment,
                PrimaryKey,
                UniqueConstraints.Values,
                CheckConstraints.Values,
                ForeignKeys.Values);

        public Dictionary<string, ExpectedColumnDefinition> Columns { get; }

        public string Table => _table;

        public string? Schema => _schema;

        public ExpectedPrimaryKeyDefinition? PrimaryKey { get; set; }

        public Dictionary<string, ExpectedUniqueConstraintDefinition> UniqueConstraints { get; }

        public Dictionary<string, ExpectedCheckConstraintDefinition> CheckConstraints { get; }

        public Dictionary<string, ExpectedForeignKeyDefinition> ForeignKeys { get; }

        public Dictionary<string, ExpectedIndexDefinition> Indexes { get; } = new(StringComparer.Ordinal);

        private static void ReplaceValues<T>(
            Dictionary<string, T> dictionary,
            Func<T, T> transform
        )
        {
            foreach (var key in dictionary.Keys.ToArray())
            {
                dictionary[key] = transform(dictionary[key]);
            }
        }

        private static string[] Rename(
            IReadOnlyList<string> values,
            string source,
            string target
        ) => values
            .Select(value => StringComparer.Ordinal.Equals(value, source) ? target : value)
            .ToArray();

        private static bool SameIdentity(
            string leftTable,
            string? leftSchema,
            string rightTable,
            string? rightSchema
        ) => StringComparer.Ordinal.Equals(leftTable, rightTable)
            && StringComparer.Ordinal.Equals(leftSchema, rightSchema);

        private static ExpectedColumnDefinition Copy(
            ExpectedColumnDefinition value,
            string? name = null,
            string? computedColumnSql = null,
            SafeMigrationSqlExpression? computedExpression = null,
            bool replaceComputed = false
        ) => new(
            name ?? value.Name,
            value.ClrType,
            value.IsNullable,
            value.StoreType,
            value.IsUnicode,
            value.MaxLength,
            value.IsFixedLength,
            value.IsRowVersion,
            value.Precision,
            value.Scale,
            value.Collation,
            value.Comment,
            value.DefaultValue,
            replaceComputed ? computedColumnSql : computedColumnSql ?? value.ComputedColumnSql,
            value.IsStored,
            replaceComputed ? computedExpression : computedExpression ?? value.ComputedExpression)
        {
            ProviderAnnotations = value.ProviderAnnotations,
        };

        private static ExpectedPrimaryKeyDefinition Copy(
            ExpectedPrimaryKeyDefinition value,
            string? table = null,
            string? schema = null,
            IReadOnlyList<string>? columns = null
        ) => new(value.Name, table ?? value.Table, columns ?? value.Columns, schema ?? value.Schema);

        private static ExpectedUniqueConstraintDefinition Copy(
            ExpectedUniqueConstraintDefinition value,
            string? table = null,
            string? schema = null,
            IReadOnlyList<string>? columns = null
        ) => new(value.Name, table ?? value.Table, columns ?? value.Columns, schema ?? value.Schema);

        private static ExpectedCheckConstraintDefinition Copy(
            ExpectedCheckConstraintDefinition value,
            string? table = null,
            string? schema = null,
            string? sql = null,
            SafeMigrationSqlExpression? expression = null,
            bool replaceExpression = false
        )
        {
            var selectedSql = replaceExpression ? sql : sql ?? value.Sql;
            var selectedExpression = replaceExpression ? expression : expression ?? value.Expression;

            return selectedSql is not null
                ? new ExpectedCheckConstraintDefinition(
                    value.Name,
                    table ?? value.Table,
                    selectedSql,
                    schema ?? value.Schema)
                : ExpectedCheckConstraintDefinition.FromExpression(
                    value.Name,
                    table ?? value.Table,
                    selectedExpression ?? throw new InvalidOperationException("A check constraint has no expression."),
                    schema ?? value.Schema);
        }

        private static ExpectedForeignKeyDefinition Copy(
            ExpectedForeignKeyDefinition value,
            string? table = null,
            string? schema = null,
            IReadOnlyList<string>? columns = null,
            string? principalTable = null,
            string? principalSchema = null,
            IReadOnlyList<string>? principalColumns = null
        ) => new(
            value.Name,
            table ?? value.Table,
            columns ?? value.Columns,
            principalTable ?? value.PrincipalTable,
            principalColumns ?? value.PrincipalColumns,
            schema ?? value.Schema,
            principalSchema ?? value.PrincipalSchema,
            value.OnUpdate,
            value.OnDelete);

        private static ExpectedIndexDefinition Copy(
            ExpectedIndexDefinition value,
            string? name = null,
            string? table = null,
            string? schema = null,
            IEnumerable<ExpectedIndexKeyDefinition>? keys = null,
            string? filter = null,
            SafeMigrationSqlExpression? structuredFilter = null,
            bool replaceFilter = false
        ) => new(
            name ?? value.Name,
            table ?? value.Table,
            keys ?? value.Keys,
            schema ?? value.Schema,
            value.Unique,
            replaceFilter ? filter : filter ?? value.Filter,
            value.IncludedColumns,
            value.Method,
            value.NullsDistinct,
            replaceFilter ? structuredFilter : structuredFilter ?? value.StructuredFilter);

        private static ExpectedIndexKeyDefinition Copy(
            ExpectedIndexKeyDefinition value,
            string source,
            string target
        ) => new(
            value.Column is not null && StringComparer.Ordinal.Equals(value.Column, source) ? target : value.Column,
            expression: null,
            value.SortOrder,
            value.NullOrder,
            value.PrefixLength,
            value.Collation,
            value.OperatorClass,
            value.Expression is not null
                ? SafeMigrationSql.OpaqueAfterRename(value.Expression)
                : value.StructuredExpression is null
                    ? null
                    : SafeMigrationSqlExpressionInspector.RenameIdentifier(value.StructuredExpression, source, target));
    }
}
