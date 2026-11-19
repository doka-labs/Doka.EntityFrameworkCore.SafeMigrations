namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>
/// Projects SQL Server included-index columns into the provider-neutral
/// SafeMigrations index definition used by generated migration source.
/// </summary>
internal sealed class SqlServerSafeMigrationCreateIndexScaffoldingProjector
    : ISafeMigrationCreateIndexScaffoldingProjector
{
    private const string IncludeAnnotation = "SqlServer:Include";

    /// <inheritdoc />
    public SafeMigrationCreateIndexScaffoldingProjection Project(
        CreateIndexOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        string[]? includedColumns = null;
        foreach (var annotation in operation.GetAnnotations())
        {
            if (!StringComparer.Ordinal.Equals(annotation.Name, IncludeAnnotation)
                || annotation.Value is not string[] { Length: > 0 } values
                || includedColumns is not null)
            {
                throw new InvalidOperationException(
                    $"The SQL Server create-index operation '{operation.Name}' contains provider metadata "
                    + "that SafeMigrations cannot project without changing its meaning.");
            }

            includedColumns = values;
        }

        if (includedColumns is null)
        {
            return new SafeMigrationCreateIndexScaffoldingProjection(operation, PrefixLengths: null);
        }

        // WHY: Design-time scaffolding cannot prove the target database's
        // identifier collation. Reject case-only collisions so a CI database
        // cannot turn two projected columns into one physical identifier.
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // WHY: Iterate the authored array directly rather than adding a LINQ iterator for ignored Add results.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var keyColumn in operation.Columns)
        {
            identifiers.Add(keyColumn);
        }

        // WHY: Validation must stop at the first invalid member and update the shared identity set in order.
        // ReSharper disable once LoopCanBeConvertedToQuery
        foreach (var includedColumn in includedColumns)
        {
            if (string.IsNullOrWhiteSpace(includedColumn)
                || !identifiers.Add(includedColumn))
            {
                throw new InvalidOperationException(
                    $"The SQL Server create-index operation '{operation.Name}' has invalid included columns.");
            }
        }

        // WHY: The typed include list becomes a SafeMigrations argument.
        // Leaving its EF annotation on the outer operation would make runtime
        // classification treat already-projected metadata as an unknown facet.
        var sanitized = new CreateIndexOperation
        {
            Name = operation.Name,
            Table = operation.Table,
            Schema = operation.Schema,
            Columns = operation.Columns

                // WHY: Keep the authored-column ownership snapshot as fluent materialization.
                // ReSharper disable once UseCollectionExpression
                .ToArray(),
            IsUnique = operation.IsUnique,
            IsDescending = operation.IsDescending?.ToArray(),
            Filter = operation.Filter,
        };

        return new SafeMigrationCreateIndexScaffoldingProjection(
            sanitized,
            PrefixLengths: null,
            IncludedColumns: includedColumns.ToArray());
    }
}
