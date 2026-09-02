namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationBuilderExtensions
{
    /// <summary>
    /// Ensures source-controlled model-managed rows exist without overwriting a
    /// row whose key already has different values.
    /// </summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="table">The target table.</param>
    /// <param name="keyColumns">The ordered key columns.</param>
    /// <param name="keyColumnTypes">The exact provider store type for every key column.</param>
    /// <param name="columns">The ordered inserted columns.</param>
    /// <param name="columnTypes">The exact provider store type for every inserted column.</param>
    /// <param name="values">The source-controlled target values.</param>
    /// <param name="schema">The target schema, or null for the provider default.</param>
    /// <param name="uniqueKeys">Source-model candidate keys used for collision analysis.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> EnsureModelManagedDataFromModel(
        this MigrationBuilder migrationBuilder,
        string table,
        string[] keyColumns,
        string[] keyColumnTypes,
        string[] columns,
        string[] columnTypes,
        object?[,] values,
        string? schema = null,
        ExpectedModelManagedDataUniqueKeyDefinition[]? uniqueKeys = null
    ) => Add(
        migrationBuilder,
        new EnsureModelManagedDataIntent(
            table,
            keyColumns,
            keyColumnTypes,
            columns,
            columnTypes,
            values,
            schema,
            uniqueKeys),
        SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>
    /// Updates source-controlled model-managed rows only when their captured
    /// source values still match.
    /// </summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="table">The target table.</param>
    /// <param name="keyColumns">The ordered key columns.</param>
    /// <param name="keyColumnTypes">The exact provider store type for every key column.</param>
    /// <param name="keyValues">The ordered key values.</param>
    /// <param name="columns">The ordered managed columns.</param>
    /// <param name="columnTypes">The exact provider store type for every managed column.</param>
    /// <param name="oldValues">The captured source values required by compare-and-swap.</param>
    /// <param name="newValues">The source-controlled target values.</param>
    /// <param name="schema">The target schema, or null for the provider default.</param>
    /// <param name="uniqueKeys">Source-model candidate keys used for collision analysis.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> UpdateModelManagedDataFromModel(
        this MigrationBuilder migrationBuilder,
        string table,
        string[] keyColumns,
        string[] keyColumnTypes,
        object?[,] keyValues,
        string[] columns,
        string[] columnTypes,
        object?[,] oldValues,
        object?[,] newValues,
        string? schema = null,
        ExpectedModelManagedDataUniqueKeyDefinition[]? uniqueKeys = null
    ) => Add(
        migrationBuilder,
        new UpdateModelManagedDataIntent(
            table,
            keyColumns,
            keyColumnTypes,
            keyValues,
            columns,
            columnTypes,
            oldValues,
            newValues,
            schema,
            uniqueKeys),
        SafeMigrationPolicy.ThrowIfDifferent);

    /// <summary>
    /// Deletes source-controlled model-managed rows only when their complete
    /// captured source values still match and no dependent row would be changed.
    /// </summary>
    /// <param name="migrationBuilder">The EF Core migration builder that receives the operation.</param>
    /// <param name="table">The target table.</param>
    /// <param name="keyColumns">The ordered key columns.</param>
    /// <param name="keyColumnTypes">The exact provider store type for every key column.</param>
    /// <param name="keyValues">The ordered key values.</param>
    /// <param name="columns">The complete ordered captured source columns.</param>
    /// <param name="columnTypes">The exact provider store type for every captured source column.</param>
    /// <param name="oldValues">The complete captured source values required by compare-and-swap.</param>
    /// <param name="schema">The target schema, or null for the provider default.</param>
    /// <param name="foreignKeys">Incoming source-model dependencies that must remain unaffected.</param>
    /// <returns>A builder for annotations on the created SafeMigrations operation.</returns>
    public static OperationBuilder<SafeMigrationOperation> DeleteModelManagedDataFromModel(
        this MigrationBuilder migrationBuilder,
        string table,
        string[] keyColumns,
        string[] keyColumnTypes,
        object?[,] keyValues,
        string[] columns,
        string[] columnTypes,
        object?[,] oldValues,
        string? schema = null,
        ExpectedModelManagedDataForeignKeyDefinition[]? foreignKeys = null
    ) => Add(
        migrationBuilder,
        new DeleteModelManagedDataIntent(
            table,
            keyColumns,
            keyColumnTypes,
            keyValues,
            columns,
            columnTypes,
            oldValues,
            schema,
            foreignKeys),
        SafeMigrationPolicy.ThrowIfDifferent);
}
