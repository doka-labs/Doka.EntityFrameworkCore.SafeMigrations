namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private sealed partial class ProjectedTable : ISafeMigrationProjectedAlterTable
    {
        private readonly List<string> _columnOrder;
        private readonly ISafeMigrationProviderObjectIdentityNormalizer? _objectIdentityNormalizer;
        private string _table;
        private string? _schema;
        private readonly string? _comment;

        public ProjectedTable(
            ExpectedTableDefinition definition,
            long dataMutationVersion,
            ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer,
            bool sharesUniqueIndexIdentity
        )
        {
            definition = SafeMigrationCapturedExpressionNormalization.NormalizeTable(
                definition,
                objectIdentityNormalizer?.FoldUnquotedCapturedSqlIdentifiers == true);

            _objectIdentityNormalizer = objectIdentityNormalizer;
            _table = definition.Table;
            _schema = definition.Schema;
            _comment = definition.Comment;
            _columnOrder = definition
                .Columns
                .Select(static value => value.Name)
                .ToList();

            var identifierComparer = new IdentifierComparer(objectIdentityNormalizer);

            Columns = definition.Columns.ToDictionary(
                static value => value.Name,
                new IdentifierComparer(objectIdentityNormalizer, objectIdentityNormalizer?.ColumnIdentifierComparer));

            PrimaryKey = definition.PrimaryKey;

            UniqueConstraints = definition.UniqueConstraints.ToDictionary(
                static value => value.Name,
                new IdentifierComparer(
                    objectIdentityNormalizer,
                    sharesUniqueIndexIdentity ? objectIdentityNormalizer?.IndexIdentifierComparer : null));

            CheckConstraints = definition.CheckConstraints.ToDictionary(static value => value.Name, identifierComparer);

            ForeignKeys = definition.ForeignKeys.ToDictionary(
                static value => value.Name,
                new IdentifierComparer(
                    objectIdentityNormalizer,
                    objectIdentityNormalizer?.ForeignKeyIdentifierComparer));

            Indexes = new Dictionary<string, ExpectedIndexDefinition>(
                new IdentifierComparer(objectIdentityNormalizer, objectIdentityNormalizer?.IndexIdentifierComparer));

            DataMutationVersion = dataMutationVersion;
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

        public long DataMutationVersion { get; }

        public bool CanReuseCreationCharacterSet { get; init; } = true;

        public string Table => _table;

        public string? Schema => _schema;

        public ExpectedPrimaryKeyDefinition? PrimaryKey { get; set; }

        public Dictionary<string, ExpectedUniqueConstraintDefinition> UniqueConstraints { get; }

        public Dictionary<string, ExpectedCheckConstraintDefinition> CheckConstraints { get; }

        public Dictionary<string, ExpectedForeignKeyDefinition> ForeignKeys { get; }

        public Dictionary<string, ExpectedIndexDefinition> Indexes { get; }

        IEnumerable<ExpectedColumnDefinition> ISafeMigrationProjectedAlterTable.Columns => Columns.Values;

        IEnumerable<ExpectedUniqueConstraintDefinition> ISafeMigrationProjectedAlterTable.UniqueConstraints =>
            UniqueConstraints.Values;

        IEnumerable<ExpectedIndexDefinition> ISafeMigrationProjectedAlterTable.Indexes => Indexes.Values;

        bool ISafeMigrationProjectedColumnSource.TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [NotNullWhen(true)] out ExpectedColumnDefinition? definition
        ) => Columns.TryGetValue(column, out definition);

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

        private string[] Rename(
            IReadOnlyList<string> values,
            string source,
            string target
        ) => values
            .Select(value => Columns.Comparer.Equals(value, source) ? target : value)
            .ToArray();

        private bool SameIdentity(
            string leftTable,
            string? leftSchema,
            string rightTable,
            string? rightSchema
        ) => IdentifierEquals(_objectIdentityNormalizer, leftTable, rightTable)
            && StringComparer.Ordinal.Equals(
                NormalizeSchema(_objectIdentityNormalizer, leftSchema),
                NormalizeSchema(_objectIdentityNormalizer, rightSchema));

        private static ExpectedColumnDefinition Copy(
            ExpectedColumnDefinition value,
            string? name = null,
            string? computedColumnSql = null,
            SafeMigrationSqlExpression? computedExpression = null,
            string? capturedComputedSql = null,
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
            CapturedComputedSql = replaceComputed ? capturedComputedSql : value.CapturedComputedSql,
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
            string? capturedSql = null,
            bool replaceExpression = false
        )
        {
            var selectedSql = replaceExpression ? sql : sql ?? value.Sql;
            var selectedExpression = replaceExpression ? expression : expression ?? value.Expression;
            var selectedCapturedSql = replaceExpression ? capturedSql : value.CapturedSql;

            if (selectedSql is not null)
            {
                return new ExpectedCheckConstraintDefinition(
                    value.Name,
                    table ?? value.Table,
                    selectedSql,
                    schema ?? value.Schema);
            }

            var structured = selectedExpression
                ?? throw new InvalidOperationException("A check constraint has no expression.");

            return selectedCapturedSql is not null
                ? ExpectedCheckConstraintDefinition.FromCapturedExpression(
                    value.Name,
                    table ?? value.Table,
                    structured,
                    selectedCapturedSql,
                    schema ?? value.Schema)
                : ExpectedCheckConstraintDefinition.FromExpression(
                    value.Name,
                    table ?? value.Table,
                    structured,
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
            IReadOnlyList<string>? includedColumns = null,
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
            includedColumns ?? value.IncludedColumns,
            value.Method,
            value.NullsDistinct,
            replaceFilter ? structuredFilter : structuredFilter ?? value.StructuredFilter);

        private ExpectedIndexKeyDefinition Copy(
            ExpectedIndexKeyDefinition value,
            string source,
            string target
        ) => new(
            value.Column is not null && Columns.Comparer.Equals(value.Column, source) ? target : value.Column,
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
                    : RenameColumnExpression(value.StructuredExpression, source, target));
    }
}
