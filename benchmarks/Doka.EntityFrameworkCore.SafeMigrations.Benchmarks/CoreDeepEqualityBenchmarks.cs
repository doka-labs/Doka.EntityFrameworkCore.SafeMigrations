namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Measures traversal of independent deep trees with native timing and allocation diagnostics.</summary>
[UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
public class CoreDeepEqualityBenchmarks
{
    private SafeMigrationSqlExpression _left = null!;
    private SafeMigrationSqlExpression _right = null!;

    /// <summary>Gets or sets whether the final leaf matches after all 1,024 binary branches are traversed.</summary>
    [Params(true, false)]
    public bool EquivalentTrees { get; set; }

    /// <summary>Builds and verifies the immutable traversal workload outside measured invocations.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _left = CreateExpression(1);
        _right = CreateExpression(EquivalentTrees
            ? 1
            : 2);

        VerifyWorkload();
    }

    /// <summary>Compares one pair without handwritten warmup, sampling, or allocation accounting.</summary>
    /// <returns>The equality result for the selected native benchmark case.</returns>
    [Benchmark]
    public bool CompareDeepTrees() => SafeMigrationSqlExpressionContract.Equivalent(_left, _right);

    /// <summary>Rejects missing, aliased, or semantically changed fixtures outside measured invocations.</summary>
    [GlobalCleanup]
    public void VerifyWorkload()
    {
        // WHY: Runtime checks retain the adversarial fixture oracle; aliased trees would bypass traversal.
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (_left is null
            // WHY: The second independently mutated tree must retain the same runtime validation as the first.
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            || _right is null
            || ReferenceEquals(_left, _right)
            || SafeMigrationSqlExpressionContract.Equivalent(_left, _right) != EquivalentTrees)
        {
            throw new InvalidOperationException("The deep equality fixture no longer matches its selected case.");
        }
    }

    /// <summary>Builds the former 1,024-branch equality workload without recursive setup.</summary>
    /// <param name="leafValue">The final integer leaf distinguishing equivalent and mismatching cases.</param>
    /// <returns>An independent left-deep addition tree with a shared immutable zero scalar.</returns>
    internal static SafeMigrationSqlExpression CreateExpression(
        int leafValue
    )
    {
        var expression = SafeMigrationSql.Literal(leafValue);
        var scalar = SafeMigrationSql.Literal(0);

        for (var depth = 0; depth < 1_024; depth++)
        {
            expression = SafeMigrationSql.Binary(expression, SafeMigrationSqlBinaryOperator.Add, scalar);
        }

        return expression;
    }
}
