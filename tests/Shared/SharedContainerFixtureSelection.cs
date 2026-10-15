namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Determines whether selected runnable test cases need the shared fixture.</summary>
internal static class SharedContainerFixtureSelection
{
    /// <summary>Checks the actual selected cases rather than all discovered test classes.</summary>
    /// <typeparam name="TFixture">The fixture constructor parameter to find.</typeparam>
    /// <param name="testCases">The selected cases passed to the framework executor.</param>
    /// <returns>Whether at least one runnable selected case requests the fixture.</returns>
    public static bool RequiresFixture<TFixture>(
        IEnumerable<IXunitTestCase> testCases
    ) => testCases.Any(testCase => RequiresFixture<TFixture>(
        testCase.TestMethod.TestClass.Class.ToRuntimeType(),
        testCase.SkipReason));

    /// <summary>Checks a concrete class's public constructor and the selected case's skip reason.</summary>
    /// <typeparam name="TFixture">The exact fixture parameter type.</typeparam>
    /// <param name="testClass">The concrete selected test class.</param>
    /// <param name="skipReason">The case's skip reason; null and empty both mean runnable.</param>
    /// <returns>Whether the selected case requires fixture initialization.</returns>
    public static bool RequiresFixture<TFixture>(
        Type testClass,
        string? skipReason
    ) => string.IsNullOrEmpty(skipReason)
        && !testClass.IsAbstract
        && testClass.GetConstructors().Any(constructor => constructor.GetParameters()
            .Any(parameter => parameter.ParameterType == typeof(TFixture)));
}
