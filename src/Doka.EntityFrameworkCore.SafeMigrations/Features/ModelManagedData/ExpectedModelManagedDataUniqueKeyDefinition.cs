namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes a source-model candidate key whose values must remain unique while
/// model-managed rows converge.
/// </summary>
public sealed class ExpectedModelManagedDataUniqueKeyDefinition
{
    /// <summary>Initializes a candidate-key definition.</summary>
    /// <param name="columns">The ordered candidate-key columns.</param>
    public ExpectedModelManagedDataUniqueKeyDefinition(
        IEnumerable<string> columns
    ) => Columns = SafeMigrationDefinitionValidator.Identifiers(columns, nameof(columns));

    /// <summary>Gets the ordered candidate-key columns.</summary>
    public IReadOnlyList<string> Columns { get; }
}
