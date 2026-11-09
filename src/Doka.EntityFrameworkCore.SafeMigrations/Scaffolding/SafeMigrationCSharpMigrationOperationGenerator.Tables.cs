namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationCSharpMigrationOperationGenerator
{
    /// <summary>Freezes table annotations inside the factory rather than on the later safe-operation wrapper.</summary>
    private void GenerateCapturedTableOperation(
        CreateTableOperation operation,
        IndentedStringBuilder builder
    )
    {
        ValidateCheckConstraints(operation);
        var baseline = CreateScratchBuilder(builder);
        base.Generate(operation, baseline);
        var source = ReplaceCompositePrincipalColumnArrays(operation, baseline.ToString());
        var invocationStart = source.IndexOf('.', StringComparison.Ordinal);
        if (invocationStart < 0
            || source
                .AsSpan(0, invocationStart)
                .ContainsAnyExcept(' ', '\t'))
        {
            throw new InvalidOperationException("The EF Core C# generator emitted an unexpected CreateTable shape.");
        }

        var (method, policy) = _configuration.Mode switch
        {
            SafeMigrationScaffoldingMode.Strict =>
                (".CreateTableIfNotExistsFromModel(", SafeMigrationPolicy.ThrowIfDifferent),
            SafeMigrationScaffoldingMode.LegacyConvergence =>
                (".ConvergeTableFromModel(", GetLegacyConvergencePolicy()),
            _ => throw new InvalidOperationException("The SafeMigrations scaffolding mode is invalid."),
        };

        builder
            .AppendLine(method)
            .IncrementIndent()
            .Append("operationFactory: static operationBuilder => operationBuilder")
            .Append(source[invocationStart..])
            .AppendLine(",")
            .Append("policy: global::Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationPolicy.")
            .Append(policy.ToString())
            .DecrementIndent()
            .Append(')');
    }
}
