namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Checks required-token source compatibility and the metadata guard's adversarial boundaries.</summary>
public sealed class SafeMigrationCancellationContractTests
{
    /// <summary>
    /// A same-operation local receives its token explicitly and preserves cancellation identity.
    /// </summary>
    /// <param name="canceled">Whether the enclosing operation is canceled before the local function executes.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalFunctionArgumentPreservesTheOperationToken(
        bool canceled
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var operationToken = cancellation.Token;
        var observed = CancellationToken.None;
        if (canceled)
        {
            await cancellation.CancelAsync();
        }

        // Act
        var failure = await Record.ExceptionAsync(() => ObserveTokenAsync(operationToken));

        // Assert
        Assert.Equal(operationToken, observed);
        if (canceled)
        {
            var exception = Assert.IsType<OperationCanceledException>(failure, exactMatch: false);
            Assert.Equal(operationToken, exception.CancellationToken);
        }
        else
        {
            Assert.Null(failure);
        }

        async Task ObserveTokenAsync(
            CancellationToken cancellationToken
        )
        {
            await Task.Yield();
            observed = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Typed phase measurements retain their functional result and exact caller token.</summary>
    [Fact]
    public async Task TypedPhaseMeasurementForwardsCallerCancellation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var observed = CancellationToken.None;

        // Act
        var measurement = await ModelManagedDataLargeExecutionEvidence.MeasureAsync(
            token =>
            {
                observed = token;

                return Task.FromResult(42);
            },
            cancellation.Token);

        // Assert
        Assert.Equal(cancellation.Token, observed);
        Assert.Equal(42, measurement.Result);
    }

    /// <summary>Untyped phase measurements retain cancellation without machine-dependent metric assertions.</summary>
    [Fact]
    public async Task UntypedPhaseMeasurementForwardsCallerCancellation()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        var observed = CancellationToken.None;

        // Act
        await ModelManagedDataLargeExecutionEvidence.MeasureAsync(
            token =>
            {
                observed = token;

                return Task.CompletedTask;
            },
            cancellation.Token);

        // Assert
        Assert.Equal(cancellation.Token, observed);
    }

    /// <summary>Neither phase overload starts a canceled action or fabricates a measurement result.</summary>
    /// <param name="typed">Whether the functional result is retained by the generic overload.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PhaseMeasurementRejectsCancellationBeforeAction(
        bool typed
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var calls = 0;
        Func<Task> execute = typed
            ? () => ModelManagedDataLargeExecutionEvidence.MeasureAsync(
                _ =>
                {
                    calls++;

                    return Task.FromResult(42);
                },
                cancellation.Token)
            : () => ModelManagedDataLargeExecutionEvidence.MeasureAsync(
                _ =>
                {
                    calls++;

                    return Task.CompletedTask;
                },
                cancellation.Token);

        // Act
        var failure = await Record.ExceptionAsync(execute);

        // Assert
        var canceled = Assert.IsType<OperationCanceledException>(failure, exactMatch: false);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(0, calls);
    }

    /// <summary>Explicit caller cancellation is observed before model services or catalog work are accessed.</summary>
    /// <param name="postflight">Whether the public verification or analysis entry point is called.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunnerObservesCancellationBeforeCatalogAccess(
        bool postflight
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var context = new DbContext(new DbContextOptionsBuilder().Options);
        var analyzer = new RecordingAnalyzer();
        var runner = new SafeMigrationRunner(analyzer);
        var options = new SafeMigrationRunOptions("cancellation-contract");

        // Act
        var exception = await Record.ExceptionAsync(() => postflight
            ? runner.VerifyAsync(context, [], options, cancellation.Token)
            : runner.AnalyzeAsync(context, [], options, cancellation.Token));

        // Assert
        var canceled = Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(0, analyzer.ValidationCount);
        Assert.Empty(analyzer.ObservedTokens);
    }

    /// <summary>The ordered runner preserves caller cancellation through classification and inventory.</summary>
    /// <param name="postflight">Whether final postconditions or pending operations are inspected.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedRunnerForwardsCancellationToEveryProviderRead(
        bool postflight
    )
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        await using var context = new DbContext(new DbContextOptionsBuilder().Options);
        var analyzer = new RecordingAnalyzer();
        var runner = new SafeMigrationRunner(analyzer);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(
                "items",
                new ExpectedColumnDefinition("Id", typeof(int), true, "int")),
            SafeMigrationPolicy.ThrowIfDifferent);

        var mode = postflight
            ? SafeMigrationReportMode.Postflight
            : SafeMigrationReportMode.Preflight;

        // Act
        var report = await AnalyzeOrderedAsync(runner, context, [operation], mode, cancellation.Token);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal([cancellation.Token, cancellation.Token], analyzer.ObservedTokens);
    }

    /// <summary>Owned public entry points reject omitted tokens and accept explicit uncancelable choices.</summary>
    /// <param name="contract">The public interface or implementation being consumed.</param>
    /// <param name="invocation">The authored call before its final token argument.</param>
    /// <param name="explicitToken">Whether the caller deliberately supplies an uncancelable token.</param>
    [Theory]
    [InlineData("ISafeMigrationRunner", "AnalyzePendingMigrationsAsync(context, options", false)]
    [InlineData("ISafeMigrationRunner", "AnalyzePendingMigrationsAsync(context, options", true)]
    [InlineData("ISafeMigrationRunner", "AnalyzeAsync(context, operations, options", false)]
    [InlineData("ISafeMigrationRunner", "AnalyzeAsync(context, operations, options", true)]
    [InlineData("ISafeMigrationRunner", "VerifyAsync(context, operations, options", false)]
    [InlineData("ISafeMigrationRunner", "VerifyAsync(context, operations, options", true)]
    [InlineData("SafeMigrationRunner", "AnalyzePendingMigrationsAsync(context, options", false)]
    [InlineData("SafeMigrationRunner", "AnalyzePendingMigrationsAsync(context, options", true)]
    [InlineData("SafeMigrationRunner", "AnalyzeAsync(context, operations, options", false)]
    [InlineData("SafeMigrationRunner", "AnalyzeAsync(context, operations, options", true)]
    [InlineData("SafeMigrationRunner", "VerifyAsync(context, operations, options", false)]
    [InlineData("SafeMigrationRunner", "VerifyAsync(context, operations, options", true)]
    [InlineData("ISafeMigrationProviderAnalyzer", "GetEnvironmentAsync(context", false)]
    [InlineData("ISafeMigrationProviderAnalyzer", "GetEnvironmentAsync(context", true)]
    [InlineData("ISafeMigrationProviderAnalyzer", "AcquireAnalysisScopeAsync(context", false)]
    [InlineData("ISafeMigrationProviderAnalyzer", "AcquireAnalysisScopeAsync(context", true)]
    [InlineData("ISafeMigrationProviderAnalyzer", "AnalyzeAsync(context, safeOperations", false)]
    [InlineData("ISafeMigrationProviderAnalyzer", "AnalyzeAsync(context, safeOperations", true)]
    [InlineData("ISafeMigrationProviderAnalyzer", "FindUnexpectedObjectsAsync(context, operations", false)]
    [InlineData("ISafeMigrationProviderAnalyzer", "FindUnexpectedObjectsAsync(context, operations", true)]
    public void PublicConsumersMustChooseCancellationExplicitly(
        string contract,
        string invocation,
        bool explicitToken
    )
    {
        // Arrange
        var token = explicitToken
            ? ", System.Threading.CancellationToken.None"
            : string.Empty;

        var source = $$"""
            using Doka.EntityFrameworkCore.SafeMigrations;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Migrations.Operations;
            public static class Consumer
            {
                public static object Invoke({{contract}} service, DbContext context, SafeMigrationRunOptions options,
                    MigrationOperation[] operations, SafeMigrationOperation[] safeOperations)
                    => service.{{invocation}}{{token}});
            }
            """;

        // Act
        var errors = Compile(source)
            .GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        // Assert
        if (explicitToken)
        {
            Assert.Empty(errors);
        }
        else
        {
            var error = Assert.Single(errors);
            Assert.Equal("CS7036", error.Id);
            Assert.Contains(
                "cancellationToken",
                error.GetMessage(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }
    }

    /// <summary>External EF and ADO.NET defaults remain callable without inventing a new owned contract.</summary>
    /// <param name="contract">The external framework type being consumed.</param>
    /// <param name="invocation">The framework call that intentionally omits its optional token.</param>
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.Migrations.IMigrator", "MigrateAsync()")]
    [InlineData("System.Data.Common.DbCommand", "ExecuteNonQueryAsync()")]
    public void ExternalFrameworkConsumersKeepTheirOptionalTokens(
        string contract,
        string invocation
    )
    {
        // Arrange
        var source = $$"""
            public static class Consumer
            {
                public static object Invoke({{contract}} service) => service.{{invocation}};
            }
            """;

        // Act
        var errors = Compile(source)
            .GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        // Assert
        Assert.Empty(errors);
    }

    /// <summary>Familiar names and owned interface implementations cannot bypass the optional-token guard.</summary>
    /// <param name="declarations">The isolated optional declarations compiled only for this negative control.</param>
    /// <param name="expectedViolations">The number of actual owned declarations the guard must reject.</param>
    [Theory]
    [InlineData("public static void ExecuteNonQueryAsync(System.Threading.CancellationToken token = default) {}", 1)]
    [InlineData("public static void Helper(System.Threading.CancellationToken token = default) {}", 1)]
    [InlineData("public void AnalyzeAsync(System.Threading.CancellationToken token = default) {}", 2)]
    public void GuardRejectsOptionalOwnMethodsAndOwnInterfaceImplementations(
        string declarations,
        int expectedViolations
    )
    {
        // Arrange
        var ownedInterface = expectedViolations == 2
            ? "public interface IOwn { void AnalyzeAsync(System.Threading.CancellationToken token = default); }"
            : string.Empty;

        var implementation = expectedViolations == 2
            ? " : IOwn"
            : string.Empty;

        var compilation = Compile($"{ownedInterface} public class Fixture{implementation} {{ {declarations} }}");
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;
        var context = new AssemblyLoadContext("cancellation-contract-negative-control", isCollectible: true);

        try
        {
            var assembly = context.LoadFromStream(image);

            // Act
            var violations = CancellationTokenContract.FindViolations(assembly);

            // Assert
            Assert.Equal(expectedViolations, violations.Count);
            Assert.All(violations, static violation => Assert.Contains("token", violation, StringComparison.Ordinal));
        }
        finally
        {
            // WHY: Repeated IDE runs must not retain dynamically compiled negative-control assemblies.
            context.Unload();
        }
    }

    /// <summary>Tokenless helpers cannot impersonate framework hooks, cleanup or compiler-owned entry points.</summary>
    /// <param name="source">The complete isolated declaration to inspect without executing its methods.</param>
    /// <param name="expectedViolations">The exact count of ordinary tokenless asynchronous declarations.</param>
    [Theory]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task Helper() => "
        + "System.Threading.Tasks.Task.CompletedTask; }",
        1)]
    [InlineData("public class Fixture { public System.Threading.Tasks.ValueTask Helper() => default; }", 1)]
    [InlineData(
        "public class Fixture { public async void Helper() "
        + "{ await System.Threading.Tasks.Task.Yield(); } }",
        1)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task DisposeAsync() => "
        + "System.Threading.Tasks.Task.CompletedTask; }",
        1)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task InitializeAsync() => "
        + "System.Threading.Tasks.Task.CompletedTask; }",
        1)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task Main() => "
        + "System.Threading.Tasks.Task.CompletedTask; }",
        1)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task Run("
        + "System.Threading.CancellationToken token) "
        + "{ return Inner(); async System.Threading.Tasks.Task Inner() "
        + "{ await System.Threading.Tasks.Task.Yield(); token.ThrowIfCancellationRequested(); } } }",
        1)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task Run("
        + "System.Threading.CancellationToken token) "
        + "{ var source = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(token); "
        + "return Inner(); async System.Threading.Tasks.Task Inner() "
        + "{ await System.Threading.Tasks.Task.Yield(); source.Token.ThrowIfCancellationRequested(); } } }",
        1)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task Run("
        + "System.Threading.CancellationToken token) "
        + "{ return First(); System.Threading.Tasks.Task First() "
        + "{ var value = 1; return Inner(); async System.Threading.Tasks.Task Inner() "
        + "{ await System.Threading.Tasks.Task.Yield(); token.ThrowIfCancellationRequested(); "
        + "System.GC.KeepAlive(value); } } } }",
        2)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task Run("
        + "System.Threading.CancellationToken token) "
        + "{ var value = 1; return Inner(); async System.Threading.Tasks.Task Inner() "
        + "{ await System.Threading.Tasks.Task.Yield(); System.GC.KeepAlive(value); } } }",
        1)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task Run("
        + "System.Threading.CancellationToken token) "
        + "{ return Inner(); static async System.Threading.Tasks.Task Inner() "
        + "{ await System.Threading.Tasks.Task.Yield(); } } }",
        1)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task Run("
        + "System.Threading.CancellationToken token) "
        + "{ return Inner(); async System.Threading.Tasks.Task Inner() "
        + "{ await System.Threading.Tasks.Task.Yield(); } } }",
        1)]
    [InlineData(
        "public class Fixture { private readonly System.Threading.CancellationToken token; "
        + "public Fixture(System.Threading.CancellationToken token) { this.token = token; } "
        + "public async System.Threading.Tasks.Task Helper() "
        + "{ await System.Threading.Tasks.Task.Yield(); token.ThrowIfCancellationRequested(); } }",
        1)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task Run("
        + "System.Threading.CancellationToken token) "
        + "{ return Inner(token); async System.Threading.Tasks.Task Inner(System.Threading.CancellationToken value) "
        + "{ await System.Threading.Tasks.Task.Yield(); value.ThrowIfCancellationRequested(); } } }",
        0)]
    [InlineData(
        "public class Fixture { public System.Threading.Tasks.Task Ready => "
        + "System.Threading.Tasks.Task.CompletedTask; public System.Threading.Tasks.Task Run("
        + "System.Threading.CancellationToken token) => System.Threading.Tasks.Task.Run(async () => "
        + "{ await System.Threading.Tasks.Task.Yield(); token.ThrowIfCancellationRequested(); }); }",
        0)]
    [InlineData(
        "public class Fixture : System.IAsyncDisposable "
        + "{ public System.Threading.Tasks.ValueTask DisposeAsync() => default; }",
        0)]
    [InlineData(
        "public class Fixture : System.IO.MemoryStream { public override async "
        + "System.Threading.Tasks.ValueTask DisposeAsync() { await base.DisposeAsync(); } }",
        0)]
    [InlineData(
        "public class Fixture { [Xunit.Fact] public System.Threading.Tasks.Task Test() => "
        + "System.Threading.Tasks.Task.CompletedTask; }",
        0)]
    [InlineData(
        "public class DerivedFactAttribute : Xunit.FactAttribute {} public class Fixture "
        + "{ [DerivedFact] public System.Threading.Tasks.Task Test() "
        + "=> System.Threading.Tasks.Task.CompletedTask; }",
        0)]
    [InlineData(
        "namespace Xunit { public class FactAttribute : System.Attribute {} } public class Fixture "
        + "{ [Xunit.Fact] public System.Threading.Tasks.Task Test() "
        + "=> System.Threading.Tasks.Task.CompletedTask; }",
        1)]
    [InlineData(
        "public class Fixture { [BenchmarkDotNet.Attributes.Benchmark] "
        + "public System.Threading.Tasks.Task Run() "
        + "=> System.Threading.Tasks.Task.CompletedTask; }",
        0)]
    [InlineData(
        "namespace BenchmarkDotNet.Attributes { public class BenchmarkAttribute : System.Attribute {} } "
        + "public class Fixture { [BenchmarkDotNet.Attributes.Benchmark] public System.Threading.Tasks.Task Run() "
        + "=> System.Threading.Tasks.Task.CompletedTask; }",
        1)]
    [InlineData(
        "namespace Doka.EntityFrameworkCore.SafeMigrations.MySql { public class MySqlCatalogSnapshot "
        + "{ public System.Threading.Tasks.Task DisposeAfterFailureAsync(System.Exception failure) "
        + "=> System.Threading.Tasks.Task.CompletedTask; } }",
        1)]
    [InlineData(
        "namespace Doka.EntityFrameworkCore.SafeMigrations.Testing "
        + "{ public class SharedContainerFixtureLifecycle<T> { public System.Threading.Tasks.Task DisposeAsync() "
        + "=> System.Threading.Tasks.Task.CompletedTask; } }",
        1)]
    [InlineData(
        "namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests "
        + "{ public static class PostgreSqlNativeAdmission "
        + "{ public static System.Threading.Tasks.Task CompleteAsync(System.Exception failure, "
        + "System.Threading.Tasks.Task execution, System.Data.Common.DbConnection observer, "
        + "System.Data.Common.DbCommand releaseCommand, System.Threading.CancellationTokenSource cancellation) "
        + "=> System.Threading.Tasks.Task.CompletedTask; } }",
        1)]
    [InlineData(
        "namespace Doka.EntityFrameworkCore.SafeMigrations { public sealed class SafeMigrationCatalogBatch {} } "
        + "namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer "
        + "{ public static class SqlServerSafeMigrationProviderAnalyzer "
        + "{ public static System.Threading.Tasks.Task CleanupTargetCaptureAsync("
        + "Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationCatalogBatch batch, "
        + "System.Data.Common.DbCommand command, System.Exception failure) "
        + "=> System.Threading.Tasks.Task.CompletedTask; } }",
        1)]
    [InlineData("public delegate System.Threading.Tasks.Task Operation(System.Threading.CancellationToken token);", 0)]
    [InlineData(
        "namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests "
        + "{ public class MySqlProviderAnalysisCleanupTests { public class ProbeCleanupFailureState "
        + "{ public System.Threading.Tasks.Task DisposeTargetAsync(string stage) "
        + "=> System.Threading.Tasks.Task.CompletedTask; } } }",
        1)]
    [InlineData(
        "namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer "
        + "{ public static class SqlServerSafeMigrationProviderAnalyzer "
        + "{ public static System.Threading.Tasks.Task CleanupTemporaryCommandAsync("
        + "System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction, "
        + "string temporaryTable, int? commandTimeout) => System.Threading.Tasks.Task.CompletedTask; } }",
        1)]
    [InlineData("public delegate System.Threading.Tasks.Task Operation();", 1)]
    public void TokenlessGuardDistinguishesOwnedOperationsFromActualFrameworkContracts(
        string source,
        int expectedViolations
    )
    {
        // Arrange
        using var image = new MemoryStream();
        var emitted = Compile(source)
            .Emit(image);

        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;
        var context = new AssemblyLoadContext("tokenless-contract-control", isCollectible: true);

        try
        {
            var assembly = context.LoadFromStream(image);

            // Act
            var violations = CancellationTokenContract.FindTokenlessViolations(assembly);

            // Assert
            Assert.Equal(expectedViolations, violations.Count);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Protected cleanup callbacks are exempt only at their reviewed owner, signature and parameter slot.
    /// </summary>
    /// <param name="contract">The exact protected cleanup callback boundary.</param>
    /// <param name="mutation">The isolated identity or signature mismatch, or the matching contract.</param>
    /// <param name="expectedViolations">The number of asynchronous callbacks that must remain rejected.</param>
    [Theory]
    [InlineData("pg-fixture", "matching", 0)]
    [InlineData("pg-fixture", "foreign-assembly", 1)]
    [InlineData("pg-fixture", "renamed-slot", 1)]
    [InlineData("pg-fixture", "changed-signature", 1)]
    [InlineData("pg-fixture", "different-member", 1)]
    [InlineData("pg-attempt", "matching", 0)]
    [InlineData("pg-attempt", "foreign-assembly", 1)]
    [InlineData("pg-attempt", "renamed-slot", 1)]
    [InlineData("pg-attempt", "changed-signature", 1)]
    [InlineData("pg-attempt", "different-member", 1)]
    [InlineData("sql-fixture", "matching", 0)]
    [InlineData("sql-fixture", "foreign-assembly", 1)]
    [InlineData("sql-fixture", "renamed-slot", 1)]
    [InlineData("sql-fixture", "changed-signature", 1)]
    [InlineData("sql-fixture", "different-member", 1)]
    public void CleanupCallbackExceptionsRequireTheirExactReviewedSlot(
        string contract,
        string mutation,
        int expectedViolations
    )
    {
        // Arrange
        var (provider, owner, slotName, parameterList) = contract switch
        {
            "pg-fixture" => ("PostgreSql", "PostgreSqlContainerFixture", "disposeContainer",
                "Func<string, CancellationToken, Task> createDatabase, "
                + "Func<string, CancellationToken, Task> dropDatabase, Func<string, string> testConnectionString, "
                + "Func<Task> disposeContainer"),
            "pg-attempt" => ("PostgreSql", "PostgreSqlOwnedConcurrentAttempt", "releaseOnFailure",
                "Func<CancellationToken, Task> execute, Func<Task, CancellationToken, Task> observe, "
                + "Func<CancellationToken, Task> release, Func<Task> releaseOnFailure, "
                + "CancellationTokenSource cancellationSource, CancellationToken cancellationToken"),
            "sql-fixture" => ("SqlServer", "SqlServerContainerFixture", "disposeContainerAsync",
                "string rootConnectionString, Func<string, CancellationToken, Task> createDatabaseAsync, "
                + "Func<string, CancellationToken, Task> dropDatabaseAsync, Func<Task> disposeContainerAsync"),
            _ => throw new ArgumentOutOfRangeException(nameof(contract), contract, "Unknown cleanup boundary."),
        };

        if (mutation == "renamed-slot")
        {
            parameterList = parameterList.Replace(slotName, "unreviewedCallback", StringComparison.Ordinal);
        }
        else if (mutation == "changed-signature")
        {
            parameterList = "int unrelated, " + parameterList;
        }

        var memberName = mutation == "different-member"
            ? "Forward"
            : "RunAsync";

        var declaration = contract == "pg-attempt"
            ? $"public static Task<Exception> {memberName}({parameterList}) => Task.FromResult<Exception>(null);"
            : mutation == "different-member"
                ? $"public void Forward({parameterList}) {{ }}"
                : $"public {owner}({parameterList}) {{ }}";

        var source = "using System; using System.Threading; using System.Threading.Tasks; "
            + $"namespace Doka.EntityFrameworkCore.SafeMigrations.{provider}.Tests "
            + $"{{ public class {owner} {{ {declaration} }} }}";

        var assemblyName = mutation == "foreign-assembly"
            ? "CleanupCallbackSpoof"
            : $"Doka.EntityFrameworkCore.SafeMigrations.{provider}.Tests";

        using var image = new MemoryStream();
        var emitted = Compile(source)
            .WithAssemblyName(assemblyName)
            .Emit(image);

        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;
        var context = new AssemblyLoadContext("cleanup-callback-contract-control", isCollectible: true);

        try
        {
            var assembly = context.LoadFromStream(image);

            // Act
            var violations = CancellationTokenContract.FindSignatureViolations(assembly);

            // Assert
            Assert.Equal(expectedViolations, violations.Count);
            Assert.All(violations,
                static violation =>
                    Assert.EndsWith("callback cancellation", violation, StringComparison.Ordinal));
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>
    /// Owned signatures and callback slots cannot hide cancellation behind captures or unrelated names.
    /// </summary>
    /// <param name="declaration">The isolated declaration whose actual parameter metadata is inspected.</param>
    /// <param name="expectedViolations">The exact number of invalid operation or callback contracts.</param>
    [Theory]
    [InlineData("void Run(System.Threading.CancellationToken cancellationToken) {}", 0)]
    [InlineData("void Run(System.Threading.CancellationToken callbackCancellationToken) {}", 0)]
    [InlineData("void Run(System.Threading.CancellationToken token) {}", 1)]
    [InlineData("void Run(System.Threading.CancellationToken cancellationToken, int value) {}", 1)]
    [InlineData("void Run(System.Threading.CancellationToken cancellationToken = default) {}", 1)]
    [InlineData("void Run(System.Func<System.Threading.Tasks.Task> execute) {}", 1)]
    [InlineData("void Run(System.Func<System.Threading.Tasks.ValueTask> execute) {}", 1)]
    [InlineData("System.Func<System.Threading.Tasks.Task> Callback { get; set; }", 0)]
    [InlineData("void set_Initialize(System.Func<System.Threading.Tasks.Task> execute) {}", 1)]
    [InlineData("void Run(System.Reflection.MethodBase member) {}", 0)]
    [InlineData("void Run(System.Func<System.Threading.CancellationToken, System.Threading.Tasks.Task> execute) {}", 0)]
    [InlineData("void Run(System.Func<System.Threading.CancellationToken, int, "
        + "System.Threading.Tasks.Task> execute) {}",
        1)]
    public void SignatureGuardChecksOwnedTokensAndAsynchronousCallbackContracts(
        string declaration,
        int expectedViolations
    )
    {
        // Arrange
        using var image = new MemoryStream();
        var emitted = Compile($"public class Fixture {{ public {declaration} }}")
            .Emit(image);

        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;
        var context = new AssemblyLoadContext("cancellation-signature-control", isCollectible: true);

        try
        {
            var assembly = context.LoadFromStream(image);

            // Act
            var violations = CancellationTokenContract.FindSignatureViolations(assembly);

            // Assert
            Assert.Equal(expectedViolations, violations.Count);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Authored async Main is bound through the real compiler-generated executable entry point.</summary>
    /// <param name="returnsCode">Whether Main returns an integer exit code.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TokenlessGuardPreservesActualAsyncExecutableEntryPoints(
        bool returnsCode
    )
    {
        // Arrange
        var result = returnsCode
            ? "System.Threading.Tasks.Task<int>"
            : "System.Threading.Tasks.Task";

        var returned = returnsCode
            ? "return 0;"
            : string.Empty;

        var source = $"public static class Entry {{ public static async {result} Main(string[] args) "
            + $"{{ await System.Threading.Tasks.Task.Yield(); {returned} }} }}";

        using var image = new MemoryStream();
        var emitted = Compile(source)
            .WithOptions(
                new CSharpCompilationOptions(OutputKind.ConsoleApplication))
            .Emit(image);

        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;
        var context = new AssemblyLoadContext("async-main-contract-control", isCollectible: true);

        try
        {
            var assembly = context.LoadFromStream(image);

            // Act
            var violations = CancellationTokenContract.FindTokenlessViolations(assembly);

            // Assert
            Assert.NotNull(assembly.EntryPoint);
            Assert.Empty(violations);
        }
        finally
        {
            context.Unload();
        }
    }

    /// <summary>Compiles a consumer against the tests' loaded runtime and current product assemblies.</summary>
    /// <param name="source">The isolated source probe, never persisted as a product declaration.</param>
    /// <returns>The compilation whose diagnostics or emitted metadata are asserted by the caller.</returns>
    private static CSharpCompilation Compile(
        string source
    )
    {
        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = platform
            .Concat(
            [
                typeof(DbContext).Assembly.Location, typeof(Migration).Assembly.Location,
                typeof(ISafeMigrationRunner).Assembly.Location, typeof(FactAttribute).Assembly.Location,
                typeof(BenchmarkAttribute).Assembly.Location,
            ])
            .Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));

        return CSharpCompilation.Create(
            "Doka.EntityFrameworkCore.SafeMigrations.CancellationProbe_"
            + Guid
                .NewGuid()
                .ToString("N"),
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>Exercises the real provider dispatch loop without requiring a fabricated database provider.</summary>
    /// <param name="runner">The production runner whose ordered dispatch is exercised.</param>
    /// <param name="context">The context supplied to each captured provider call.</param>
    /// <param name="operations">The deterministic safe operations classified by the analyzer.</param>
    /// <param name="mode">The analysis or verification dispatch mode.</param>
    /// <param name="cancellationToken">The caller token that every provider read must receive unchanged.</param>
    /// <returns>The report produced by the production ordered loop.</returns>
    private static Task<SafeMigrationRunReport> AnalyzeOrderedAsync(
        SafeMigrationRunner runner,
        DbContext context,
        IReadOnlyList<MigrationOperation> operations,
        SafeMigrationReportMode mode,
        CancellationToken cancellationToken
    )
    {
        var method = typeof(SafeMigrationRunner).GetMethod(
            "AnalyzeOperationsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        // WHY: Core has no concrete database provider. The established ordered seam isolates real dispatch
        // and inventory from EF model/history discovery without replacing either provider call with a report stub.
        return (Task<SafeMigrationRunReport>)method.Invoke(
            runner,
            [
                context, operations, mode, new SafeMigrationRunOptions("cancellation-contract"),
                $"safe-relational-model:v1:sqlite:sha256:{new string('a', 64)}", new string('b', 64),
                DateTimeOffset.UnixEpoch, new SafeMigrationProviderEnvironment("sqlite", "sqlite", "test"),
                null, null, cancellationToken,
            ])!;
    }

    /// <summary>Captures exact cancellation identities at the runner's provider boundary without I/O.</summary>
    private sealed class RecordingAnalyzer : ISafeMigrationProviderAnalyzer
    {
        /// <summary>Gets the count of side-effect-free context validation calls.</summary>
        public int ValidationCount { get; private set; }

        /// <summary>Gets the exact tokens passed to catalog classification and inventory.</summary>
        public List<CancellationToken> ObservedTokens { get; } = [];

        /// <inheritdoc />
        public string ProviderId => "sqlite";

        /// <inheritdoc />
        public void ValidateContext(
            DbContext context
        )
        {
            ArgumentNullException.ThrowIfNull(context);
            ValidationCount++;
        }

        /// <inheritdoc />
        public Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
            DbContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("The isolated ordered seam receives its captured environment.");

        /// <inheritdoc />
        public Task<IAsyncDisposable> AcquireAnalysisScopeAsync(
            DbContext context,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException("The isolated ordered seam performs no database I/O.");

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationProviderAnalysis>> AnalyzeAsync(
            DbContext context,
            IReadOnlyList<SafeMigrationOperation> operations,
            CancellationToken cancellationToken
        )
        {
            ObservedTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<IReadOnlyList<SafeMigrationProviderAnalysis>>(
                operations
                    .Select(static _ =>
                        new SafeMigrationProviderAnalysis(
                            SafeMigrationObservedState.Matching,
                            SafeMigrationRepairCapability.None,
                            true,
                            "classified_matching"))
                    .ToArray());
        }

        /// <inheritdoc />
        public Task<IReadOnlyList<SafeMigrationUnexpectedObject>> FindUnexpectedObjectsAsync(
            DbContext context,
            IReadOnlyList<MigrationOperation> operations,
            CancellationToken cancellationToken
        )
        {
            ObservedTokens.Add(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult<IReadOnlyList<SafeMigrationUnexpectedObject>>([]);
        }
    }
}
