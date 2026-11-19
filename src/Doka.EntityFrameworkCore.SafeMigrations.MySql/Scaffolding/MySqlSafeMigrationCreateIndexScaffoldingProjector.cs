namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>
/// Projects Doka index metadata into the provider-neutral SafeMigrations index
/// definition used by generated migrations.
/// </summary>
internal sealed class MySqlSafeMigrationCreateIndexScaffoldingProjector
    : ISafeMigrationCreateIndexScaffoldingProjector
{
    /// <inheritdoc />
    public SafeMigrationCreateIndexScaffoldingProjection Project(
        CreateIndexOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        var metadata = operation.GetMySqlMigrationMetadata();
        var recognizedAnnotationCount = metadata.IndexPrefixLengths is null
            ? 0
            : 1;

        if (operation
                .GetAnnotations()
                .Count()
            != recognizedAnnotationCount)
        {
            throw new InvalidOperationException(
                $"The create-index operation '{operation.Name}' contains provider metadata that "
                + "SafeMigrations cannot project without changing its meaning.");
        }

        if (metadata.IndexPrefixLengths is null)
        {
            return new SafeMigrationCreateIndexScaffoldingProjection(operation, PrefixLengths: null);
        }

        // Generate from an annotation-free copy. The typed prefix values are
        // emitted as an explicit SafeMigrations argument, so leaving the Doka
        // annotation in the fluent chain would attach it to the outer custom
        // operation and make runtime analysis fail closed.
        var sanitized = new CreateIndexOperation
        {
            Name = operation.Name,
            Table = operation.Table,
            Schema = operation.Schema,

            // WHY: Retain fluent materialization and its existing snapshot
            // ownership rather than replacing this boundary with a spread.
            // ReSharper disable once UseCollectionExpression
            Columns = operation.Columns.ToArray(),
            IsUnique = operation.IsUnique,

            // WHY: Retain fluent materialization and its existing snapshot
            // ownership rather than replacing this boundary with a spread.
            // ReSharper disable once UseCollectionExpression
            IsDescending = operation.IsDescending?.ToArray(),
            Filter = operation.Filter,
        };

        return new SafeMigrationCreateIndexScaffoldingProjection(
            sanitized,

            // WHY: Retain fluent materialization and its existing snapshot
            // ownership rather than replacing this boundary with a spread.
            // ReSharper disable once UseCollectionExpression
            metadata.IndexPrefixLengths.ToArray());
    }
}
