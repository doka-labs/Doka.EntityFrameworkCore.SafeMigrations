namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

/// <summary>Guards explicit cancellation at repository-owned asynchronous boundaries.</summary>
public sealed class CancellationTokenContractTests
{
    /// <summary>Every owned token is required unless its optionality belongs to an external contract.</summary>
    [Fact]
    public void RepositoryOwnedTokensRequireAnExplicitArgument()
    {
        // Arrange
        var assemblies = GetOwnedAssemblies();

        // Act
        var violations = assemblies
            .SelectMany(CancellationTokenContract.FindViolations)
            .ToArray();

        // Assert
        Assert.Empty(violations);
    }

    /// <summary>Owned asynchronous helpers require a token parameter or a local captured cancellation scope.</summary>
    [Fact]
    public void RepositoryOwnedAsynchronousHelpersRequireCancellation()
    {
        // Arrange
        var assemblies = GetOwnedAssemblies();

        // Act
        var violations = assemblies
            .SelectMany(CancellationTokenContract.FindTokenlessViolations)
            .ToArray();

        // Assert
        Assert.True(
            violations.Length == 0,
            "Tokenless owned async helpers:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    /// <summary>An owned interface does not become a framework exemption merely by defining a familiar name.</summary>
    [Fact]
    public void OwnedAnalyzerContractIsNotAnExternalSeam()
    {
        // Arrange
        var method = typeof(ISafeMigrationProviderAnalyzer).GetMethod(
            nameof(ISafeMigrationProviderAnalyzer.AnalyzeAsync))!;

        var parameter = method
            .GetParameters()[^1];

        // Act
        var external = CancellationTokenContract.IsExternalOptionalContract(method, parameter.Position);

        // Assert
        Assert.False(external);
        Assert.False(parameter.IsOptional);
        Assert.False(parameter.HasDefaultValue);
    }

    /// <summary>Real framework overrides retain optional tokens with structurally matched contracts.</summary>
    [Fact]
    public void FrameworkOverridesRetainTheirExternalOptionalContract()
    {
        // Arrange
        const BindingFlags declarations = BindingFlags.DeclaredOnly
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic;

        var methods = GetOwnedAssemblies()
            .SelectMany(static assembly => assembly.GetTypes())
            .SelectMany(static type => type.GetMethods(declarations));

        // Act
        var parameters = methods
            .SelectMany(static method => method
                .GetParameters()
                .Where(parameter => parameter.ParameterType == typeof(CancellationToken)
                    && CancellationTokenContract.IsExternalOptionalContract(method, parameter.Position)))
            .ToArray();

        // Assert
        Assert.NotEmpty(parameters);
        Assert.All(
            parameters,
            static parameter =>
            {
                Assert.True(parameter.IsOptional);
                Assert.True(parameter.HasDefaultValue);
            });
    }

    /// <summary>Includes this test assembly and its directly referenced SafeMigrations product assemblies.</summary>
    /// <returns>The same owned declaration scope for required tokens and retained framework defaults.</returns>
    private static IEnumerable<Assembly> GetOwnedAssemblies()
    {
        var testAssembly = typeof(CancellationTokenContractTests).Assembly;

        return testAssembly
            .GetReferencedAssemblies()
            .Where(static name => name.Name?.StartsWith(
                    "Doka.EntityFrameworkCore.SafeMigrations",
                    StringComparison.Ordinal)
                == true)
            .Select(Assembly.Load)
            .Append(testAssembly)
            .Distinct();
    }
}

/// <summary>Inspects metadata rather than exempting token parameters by a fragile method-name allowlist.</summary>
internal static class CancellationTokenContract
{
    /// <summary>Finds asynchronous operations outside actual framework or reviewed cleanup boundaries.</summary>
    /// <param name="assembly">The owned assembly whose compiled declarations are inspected.</param>
    /// <returns>Identifier-only descriptions of tokenless owned asynchronous helpers.</returns>
    internal static IReadOnlyList<string> FindTokenlessViolations(
        Assembly assembly
    )
    {
        const BindingFlags declarations = BindingFlags.DeclaredOnly
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic;

        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(declarations))
            {
                if (method.IsSpecialName
                    || !IsAsynchronous(method)
                    || method
                        .GetParameters()
                        .Any(static parameter =>
                            parameter.ParameterType == typeof(CancellationToken))
                    || IsExternalMethodContract(method)
                    || IsFrameworkEntryPoint(method)
                    || IsCompilerAdapter(method)
                    || IsDelegateAdapter(method)
                    || IsRequiredCleanup(method)
                    || HasCapturedCancellationScope(method))
                {
                    continue;
                }

                violations.Add($"{type.FullName}.{method.Name}");
            }
        }

        return violations;
    }

    /// <summary>Recognizes the asynchronous return contracts supported by repository code.</summary>
    private static bool IsAsynchronous(
        MethodInfo method
    )
    {
        var result = method.ReturnType;

        return typeof(Task).IsAssignableFrom(result)
            || result == typeof(ValueTask)
            || method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false)
            || (result.IsGenericType
                && (result.GetGenericTypeDefinition() == typeof(ValueTask<>)
                    || result.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)
                    || result.GetGenericTypeDefinition() == typeof(IAsyncEnumerator<>)));
    }

    /// <summary>Uses actual base/interface mapping rather than names such as InitializeAsync or RunAsync.</summary>
    private static bool IsExternalMethodContract(
        MethodInfo method
    )
    {
        var baseMethod = method.GetBaseDefinition();

        if (baseMethod != method
            && IsExternal(baseMethod.DeclaringType!))
        {
            return true;
        }

        var declaringType = method.DeclaringType!;

        if (declaringType.IsInterface)
        {
            return false;
        }

        foreach (var contract in declaringType.GetInterfaces())
        {
            if (IsExternal(contract)
                && declaringType
                    .GetInterfaceMap(contract)
                    .TargetMethods
                    .Contains(method))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Retains genuine framework-invoked test, benchmark and executable entry signatures.</summary>
    private static bool IsFrameworkEntryPoint(
        MethodInfo method
    )
    {
        if (IsExecutableEntryPoint(method))
        {
            return true;
        }

        return method
            .GetCustomAttributesData()
            .Any(static attribute =>
                typeof(FactAttribute).IsAssignableFrom(attribute.AttributeType)
                || (attribute.AttributeType.Assembly.GetName()
                        .Name is "BenchmarkDotNet" or "BenchmarkDotNet.Annotations"
                    && attribute.AttributeType.FullName is "BenchmarkDotNet.Attributes.BenchmarkAttribute"
                        or "BenchmarkDotNet.Attributes.GlobalSetupAttribute"
                        or "BenchmarkDotNet.Attributes.GlobalCleanupAttribute"
                        or "BenchmarkDotNet.Attributes.IterationSetupAttribute"
                        or "BenchmarkDotNet.Attributes.IterationCleanupAttribute"));
    }

    /// <summary>Recognizes a real CLR entry point and the compiler bridge for an authored async Main.</summary>
    private static bool IsExecutableEntryPoint(
        MethodInfo method
    )
    {
        var entryPoint = method.Module.Assembly.EntryPoint;

        if (entryPoint == method)
        {
            return true;
        }

        // WHY: Async Main has a synchronous compiler-generated CLR entry bridge, not the authored
        // method as Assembly.EntryPoint. Owner, bridge identity and the legal signature must agree.
        return entryPoint is not null
            && entryPoint.Name == "<Main>"
            && method.Name == "Main"
            && entryPoint.DeclaringType == method.DeclaringType
            && (method.ReturnType == typeof(Task) || method.ReturnType == typeof(Task<int>))
            && method
                .GetParameters()
                .Select(static parameter => parameter.ParameterType)
                .SequenceEqual(entryPoint
                    .GetParameters()
                    .Select(static parameter => parameter.ParameterType));
    }

    /// <summary>Excludes compiler callback/base-call adapters, but not owned local functions.</summary>
    private static bool IsCompilerAdapter(
        MethodInfo method
    ) => (method.Name.Contains(">b__", StringComparison.Ordinal)
            || method.Name.StartsWith("<>n__", StringComparison.Ordinal))
        && (method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            || method.DeclaringType!.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false));

    /// <summary>Recognizes local functions that already carry an enclosing cancellation scope.</summary>
    /// <param name="method">The compiler-lowered local function whose captured environment is inspected.</param>
    /// <returns>Whether the generated closure graph contains an operation token or its owning source.</returns>
    private static bool HasCapturedCancellationScope(
        MethodInfo method
    )
    {
        if (!method.Name.Contains(">g__", StringComparison.Ordinal)
            || (!method.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                && !method.DeclaringType!.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)))
        {
            return false;
        }

        var pending = new Stack<Type>();
        var visited = new HashSet<Type>();
        pending.Push(method.DeclaringType!);
        foreach (var parameter in method.GetParameters())
        {
            var type = parameter.ParameterType;
            pending.Push(
                type.IsByRef
                    ? type.GetElementType()!
                    : type);
        }

        // WHY: A capturing local can lower to a static method with a by-ref closure struct.
        // This checks available scope only; behavioral tests separately verify actual token forwarding.
        while (pending.TryPop(out var type))
        {
            if (!type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                || !visited.Add(type))
            {
                continue;
            }

            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType == typeof(CancellationToken)
                    || field.FieldType == typeof(CancellationTokenSource))
                {
                    return true;
                }

                pending.Push(field.FieldType);
            }
        }

        return false;
    }

    /// <summary>Retains the CLR delegate infrastructure while checking the authored Invoke contract.</summary>
    private static bool IsDelegateAdapter(
        MethodInfo method
    ) => typeof(Delegate).IsAssignableFrom(method.DeclaringType)
        && method.Name is "BeginInvoke" or "EndInvoke"
        && (method.GetMethodImplementationFlags() & MethodImplAttributes.CodeTypeMask) == MethodImplAttributes.Runtime;

    /// <summary>Matches the reviewed owner, method and exact parameter types of mandatory cleanup work.</summary>
    private static bool IsRequiredCleanup(
        MethodInfo method
    )
    {
        var owner = method.DeclaringType!;
        var identity = owner.FullName ?? string.Empty;
        var assembly = owner.Assembly.GetName()
            .Name;

        if (!IsCleanupOwnerAssembly(identity, assembly))
        {
            return false;
        }

        var parameters = string.Join(
            ",",
            method
                .GetParameters()
                .Select(static parameter => parameter.ParameterType.ToString()));

        // WHY: These methods must finish after execution cancellation. New similarly named helpers
        // are not exceptions; each owner/signature must be reviewed as a resource-recovery boundary.
        return (identity, method.Name, parameters) switch
        {
            ("Doka.EntityFrameworkCore.SafeMigrations.Testing.OwnedTestOperationCleanup",
                "CompleteAsync", "System.Exception,System.Threading.Tasks.Task,"
                + "System.Func`1[System.Threading.Tasks.Task],System.Threading.CancellationTokenSource,"
                + "System.Func`1[System.Threading.Tasks.Task]") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.MySql.MySqlCatalogSnapshot",
                "DisposeAfterFailureAsync", "System.Exception") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.MySql.MySqlCatalogSnapshot",
                "InvalidateConnectionAsync", "System.Exception") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.MySql.MySqlCatalogSnapshot",
                "DisposeCommandAfterFailureAsync", "System.Data.Common.DbCommand,System.Exception") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.SqlServer.SqlServerSafeMigrationIdentityInsertCommand",
                "RecoverSessionAsync", "Microsoft.EntityFrameworkCore.Storage.IRelationalConnection,"
                + "System.String,System.Boolean,System.Exception") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.SqlServer.SqlServerSafeMigrationProviderAnalyzer",
                "CleanupTemporaryScopeAsync", "System.Data.Common.DbConnection,System.Data.Common.DbTransaction,"
                + "System.String,System.Nullable`1[System.Int32],System.Exception") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.Testing.SharedContainerFixtureLifecycle`1",
                "DisposeAsync", "") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.Testing.SharedContainerFixtureLifecycle`1",
                "DisposeCoreAsync", "System.Threading.Tasks.Task") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.Testing.SharedContainerTestAssemblyRunner`1",
                "EnsureFixtureDisposedAsync", "") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.Testing.SharedContainerTestAssemblyRunner`1",
                "FinishAsync", "System.Threading.Tasks.Task`1[Xunit.Sdk.RunSummary][]") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.MySqlEngineContainerFixture",
                "DropOwnedDatabasesAsync", "") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.MySqlFixtureLifecycle",
                "DisposeAsync", "System.Func`1[System.Threading.Tasks.Task],"
                + "System.Func`1[System.Threading.Tasks.Task],System.Action") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.MySqlGeneratedCaseIntegrationTests",
                "ReleaseSparseDropGateAsync", "MySqlConnector.MySqlConnection,System.String") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests.MySqlSafeMigrationIntegrationTests",
                "FinishAtomicAddConcurrentAttemptAsync",
                "System.Exception,System.Threading.Tasks.Task,"
                + "System.Threading.CancellationTokenSource,MySqlConnector.MySqlTransaction,"
                + "System.Boolean") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests.PostgreSqlContainerFixture",
                "DisposeNativeContainerAsync", "") => true,
            ("Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests.SqlServerLocalCatalogDiagnostics",
                "DropDatabaseAsync", "Microsoft.Data.SqlClient.SqlConnection,System.String") => true,
            _ => false,
        };
    }

    /// <summary>Prevents a same-named type in an unrelated assembly from inheriting a cleanup waiver.</summary>
    private static bool IsCleanupOwnerAssembly(
        string owner,
        string? assembly
    )
    {
        const string root = "Doka.EntityFrameworkCore.SafeMigrations";

        if (owner.StartsWith(root + ".Testing.", StringComparison.Ordinal))
        {
            return assembly is root + ".Tests"
                or root + ".MySql.Tests"
                or root + ".PostgreSql.Tests"
                or root + ".Sqlite.Tests"
                or root + ".SqlServer.Tests";
        }

        return owner switch
        {
            root + ".MySql.MySqlCatalogSnapshot" => assembly == root + ".MySql",
            root + ".SqlServer.SqlServerSafeMigrationIdentityInsertCommand"
                or root + ".SqlServer.SqlServerSafeMigrationProviderAnalyzer" => assembly == root + ".SqlServer",
            root + ".MySql.Tests.MySqlEngineContainerFixture"
                or root + ".MySql.Tests.MySqlFixtureLifecycle"
                or root + ".MySql.Tests.MySqlGeneratedCaseIntegrationTests"
                or root + ".MySql.Tests.MySqlSafeMigrationIntegrationTests" => assembly == root + ".MySql.Tests",
            root + ".PostgreSql.Tests.PostgreSqlContainerFixture" => assembly == root + ".PostgreSql.Tests",
            root + ".SqlServer.Tests.SqlServerLocalCatalogDiagnostics" => assembly == root + ".SqlServer.Tests",
            _ => false,
        };
    }

    /// <summary>Finds optional owned token parameters, including non-public methods and constructors.</summary>
    /// <param name="assembly">The owned assembly whose declarations are inspected.</param>
    /// <returns>Stable, identifier-only descriptions of each violating declaration.</returns>
    internal static IReadOnlyList<string> FindViolations(
        Assembly assembly
    )
    {
        const BindingFlags declarations = BindingFlags.DeclaredOnly
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.Public
            | BindingFlags.NonPublic;

        var violations = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            var members = type
                .GetMethods(declarations)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(declarations));

            foreach (var member in members)
            {
                foreach (var parameter in member.GetParameters())
                {
                    if (parameter.ParameterType != typeof(CancellationToken)
                        || !parameter.IsOptional)
                    {
                        continue;
                    }

                    if (member is MethodInfo method
                        && IsExternalOptionalContract(method, parameter.Position))
                    {
                        continue;
                    }

                    violations.Add($"{type.FullName}.{member.Name}({parameter.Name})");
                }
            }
        }

        return violations;
    }

    /// <summary>Checks whether the exact parameter's optionality is inherited from a framework-owned seam.</summary>
    /// <param name="method">The declaration being checked.</param>
    /// <param name="position">The token's parameter position.</param>
    /// <returns>Whether an external base declaration or mapped interface defines this optional token.</returns>
    internal static bool IsExternalOptionalContract(
        MethodInfo method,
        int position
    )
    {
        var baseMethod = method.GetBaseDefinition();

        if (baseMethod != method
            && IsExternal(baseMethod.DeclaringType!)
            && IsOptionalToken(baseMethod, position))
        {
            return true;
        }

        var declaringType = method.DeclaringType!;

        if (declaringType.IsInterface)
        {
            return false;
        }

        foreach (var contract in declaringType.GetInterfaces())
        {
            if (!IsExternal(contract))
            {
                continue;
            }

            var map = declaringType.GetInterfaceMap(contract);

            // WHY: Interface-map arrays are paired by ordinal; matching names cannot prove a framework seam.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < map.TargetMethods.Length; index++)
            {
                if (map.TargetMethods[index] == method
                    && IsOptionalToken(map.InterfaceMethods[index], position))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Distinguishes framework declarations from SafeMigrations product and test assemblies.</summary>
    private static bool IsExternal(
        Type type
    ) => !type.Assembly.GetName()
        .Name!
        .StartsWith("Doka.EntityFrameworkCore.SafeMigrations", StringComparison.Ordinal);

    /// <summary>Checks the same parameter position on a structurally proven contract method.</summary>
    private static bool IsOptionalToken(
        MethodInfo method,
        int position
    )
    {
        var parameters = method.GetParameters();

        return position < parameters.Length
            && parameters[position].ParameterType == typeof(CancellationToken)
            && parameters[position].IsOptional;
    }
}
