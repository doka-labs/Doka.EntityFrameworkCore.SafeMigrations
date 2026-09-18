namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private string? BuildCurrentDatabaseQualificationExpression(
        SafeMigrationIntent intent,
        IReadOnlyList<string>? requiredDatabaseQualifiers
    )
    {
        var collector = new DatabaseQualificationExpressionCollector();
        if (requiredDatabaseQualifiers is not null)
        {
            foreach (var qualifier in requiredDatabaseQualifiers)
            {
                collector.Visit(qualifier);
            }
        }

        VisitDatabaseQualifiers(intent, ref collector);

        if (collector.First is null)
        {
            return null;
        }

        if (collector.Distinct is null)
        {
            return "DATABASE() IS NOT NULL AND " + BuildCurrentDatabaseEquality(collector.First);
        }

        return "DATABASE() IS NOT NULL AND "
            + string.Join(
                " AND ",
                collector.Distinct.Select(BuildCurrentDatabaseEquality));
    }

    private string BuildCurrentDatabaseEquality(
        string qualifier
    )
    {
        // WHY: lower_case_table_names changes with server configuration and
        // platform. A binary comparison is the only portable rule that never
        // widens one reviewed database identity into a server-dependent alias.
        return $"BINARY DATABASE() = BINARY {Literal(qualifier)}";
    }

    internal static string[] GetDatabaseQualifiers(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        ArgumentNullException.ThrowIfNull(operations);

        var collector = new DatabaseQualificationExpressionCollector();
        foreach (var operation in operations.OfType<SafeMigrationOperation>())
        {
            VisitDatabaseQualifiers(operation.Intent, ref collector);
        }

        return collector.GetValues();
    }

    internal static bool HasDatabaseQualifier(
        SafeMigrationIntent intent
    )
    {
        var visitor = new DatabaseQualifierPresenceVisitor();
        VisitDatabaseQualifiers(intent, ref visitor);

        return visitor.HasQualifier;
    }

    internal static bool TargetsDatabase(
        SafeMigrationIntent intent,
        string? currentDatabase
    )
    {
        var visitor = new DatabaseQualifierTargetVisitor(currentDatabase);
        VisitDatabaseQualifiers(intent, ref visitor);

        return visitor.TargetsDatabase;
    }

    private static void VisitDatabaseQualifiers<TVisitor>(
        SafeMigrationIntent intent,
        ref TVisitor visitor
    ) where TVisitor : struct, IDatabaseQualifierVisitor
    {
        switch (intent)
        {
            case EnsureSchemaIntent value:
                visitor.Visit(value.Name);
                break;
            case DropSchemaIntent value:
                visitor.Visit(value.Name);
                break;
            case EnsureTableIntent value:
                visitor.Visit(value.Definition.Schema);
                foreach (var foreignKey in value.Definition.ForeignKeys)
                {
                    visitor.Visit(foreignKey.PrincipalSchema);
                }

                break;
            case DropTableIntent value:
                visitor.Visit(value.Schema);
                break;
            case RenameTableIntent value:
                visitor.Visit(value.Schema);
                visitor.Visit(value.NewSchema);
                break;
            case EnsureColumnIntent value:
                visitor.Visit(value.Schema);
                break;
            case DropColumnIntent value:
                visitor.Visit(value.Schema);
                break;
            case RenameColumnIntent value:
                visitor.Visit(value.Schema);
                break;
            case AlterColumnIntent value:
                visitor.Visit(value.Schema);
                break;
            case EnsureIndexIntent value:
                visitor.Visit(value.Definition.Schema);
                break;
            case DropIndexIntent value:
                visitor.Visit(value.Schema);
                break;
            case RenameIndexIntent value:
                visitor.Visit(value.Schema);
                break;
            case EnsurePrimaryKeyIntent value:
                visitor.Visit(value.Definition.Schema);
                break;
            case DropPrimaryKeyIntent value:
                visitor.Visit(value.Schema);
                break;
            case EnsureUniqueConstraintIntent value:
                visitor.Visit(value.Definition.Schema);
                break;
            case DropUniqueConstraintIntent value:
                visitor.Visit(value.Schema);
                break;
            case EnsureCheckConstraintIntent value:
                visitor.Visit(value.Definition.Schema);
                break;
            case DropCheckConstraintIntent value:
                visitor.Visit(value.Schema);
                break;
            case EnsureForeignKeyIntent value:
                visitor.Visit(value.Definition.Schema);
                visitor.Visit(value.Definition.PrincipalSchema);
                break;
            case DropForeignKeyIntent value:
                visitor.Visit(value.Schema);
                break;
            case DeleteModelManagedDataIntent value:
                visitor.Visit(value.Schema);
                foreach (var foreignKey in value.ForeignKeys)
                {
                    visitor.Visit(foreignKey.Schema);
                }

                break;
            case ModelManagedDataIntent value:
                visitor.Visit(value.Schema);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported SafeMigrations intent '{intent.GetType().FullName}' "
                    + "in MySQL database identity analysis.");
        }
    }

    private interface IDatabaseQualifierVisitor
    {
        void Visit(
            string? qualifier
        );
    }

    private struct DatabaseQualificationExpressionCollector : IDatabaseQualifierVisitor
    {
        public string? First { get; private set; }

        public List<string>? Distinct { get; private set; }

        public void Visit(
            string? qualifier
        )
        {
            if (qualifier is null)
            {
                return;
            }

            if (First is null)
            {
                First = qualifier;

                return;
            }

            if (StringComparer.Ordinal.Equals(First, qualifier)
                || Distinct?.Contains(qualifier, StringComparer.Ordinal) == true)
            {
                return;
            }

            Distinct ??= [First];
            Distinct.Add(qualifier);
        }

        public string[] GetValues(
        ) => First is null
            ? []
            : Distinct?.ToArray() ?? [First];
    }

    private struct DatabaseQualifierPresenceVisitor : IDatabaseQualifierVisitor
    {
        public bool HasQualifier { get; private set; }

        public void Visit(
            string? qualifier
        ) => HasQualifier |= qualifier is not null;
    }

    private struct DatabaseQualifierTargetVisitor(
        string? currentDatabase
    ) : IDatabaseQualifierVisitor
    {
        public bool TargetsDatabase { get; private set; } = true;

        public void Visit(
            string? qualifier
        )
        {
            if (qualifier is not null
                && (currentDatabase is null
                    || !StringComparer.Ordinal.Equals(qualifier, currentDatabase)))
            {
                TargetsDatabase = false;
            }
        }
    }

    private static MySqlSafeMigrationRuntimePlan BuildEnsureSchema(
        EnsureSchemaIntent intent
    ) => Plan("'matching'", "TRUE");

    private static MySqlSafeMigrationRuntimePlan BuildDropSchema(
        DropSchemaIntent intent
    ) => Unsupported("schema_operations");

    private MySqlSafeMigrationRuntimePlan ApplyCurrentDatabaseQualification(
        SafeMigrationIntent intent,
        MySqlSafeMigrationRuntimePlan plan,
        IReadOnlyList<string>? requiredDatabaseQualifiers
    )
    {
        var expression = BuildCurrentDatabaseQualificationExpression(
            intent,
            requiredDatabaseQualifiers);
        if (expression is null)
        {
            return plan;
        }

        return plan with
        {
            CurrentDatabaseQualificationExpression = expression,
            CurrentDatabaseQualificationFailureCode = "database_qualifier_mismatch",
            RequiresLazyStateEvaluation = true,
        };
    }
}
