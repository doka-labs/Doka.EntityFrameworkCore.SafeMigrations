namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Pins native job, filter and artifact behavior while binding only source-project discovery.</summary>
public sealed class BenchmarkProgramSourceBoundTests
{
    /// <summary>Preserves native asynchronous validation, including unsupported runtime settings.</summary>
    /// <param name="unsupportedJit">Whether the native configuration selects the rejected legacy JIT.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceBoundToolchain_PreservesNativeValidation(
        bool unsupportedJit
    )
    {
        // Arrange
        var native = CsProjCoreToolchain.NetCoreApp11_0;
        var config = ManualConfig.Create(DefaultConfig.Instance);
        config.AddJob(unsupportedJit
            ? Job.Dry.WithJit(Jit.LegacyJit)
            : Job.Dry);

        var selected = await BenchmarkConverter.TypeToBenchmarksAsync(
            typeof(BenchmarkProgramOutcomeProbe),
            config,
            CancellationToken.None);

        var benchmark = selected.BenchmarksCases[0];
        var resolver = new CompositeResolver(EnvironmentResolver.Instance, EngineResolver.Instance);
        var expected = new List<(bool Critical, string Message)>();
        await foreach (var error in native.ValidateAsync(benchmark, resolver)
                           .WithCancellation(CancellationToken.None))
        {
            expected.Add((error.IsCritical, error.Message));
        }

        var bound = new SourceBoundToolchain(native, (CsProjBuilder)native.Builder);
        var actual = new List<(bool Critical, string Message)>();

        // Act
        await foreach (var error in bound.ValidateAsync(benchmark, resolver)
                           .WithCancellation(CancellationToken.None))
        {
            actual.Add((error.IsCritical, error.Message));
        }

        // Assert
        Assert.Equal(expected, actual);
        Assert.Equal(unsupportedJit, actual.Any(static error => error.Critical));
    }

    /// <summary>Preserves native job expansion and every configured leaf characteristic except the toolchain.</summary>
    /// <param name="arguments">The native CLI selection of job and runtime.</param>
    [Theory]
    [InlineData("--job", "Default")]
    [InlineData("--job", "Dry")]
    [InlineData("--job", "Short")]
    [InlineData("--runtimes", "net11.0")]
    [InlineData("--job", "Dry", "--runtimes", "net11.0")]
    public void SourceBoundConfig_PreservesNativeExpandedJob(
        params string[] arguments
    )
    {
        // Arrange
        var baseConfig = ManualConfig.Create(DefaultConfig.Instance);
        var (parsed, parsedConfig, _) = ConfigParser.Parse(arguments, NullLogger.Instance, baseConfig);
        ArgumentNullException.ThrowIfNull(parsedConfig);

        var nativeConfig = ImmutableConfigBuilder.Create(ManualConfig.Union(baseConfig, parsedConfig));
        var nativeJob = nativeConfig
            .GetJobs()
            .Single();

        var nativeToolchain = nativeJob.Infrastructure.TryGetToolchain(out var selected)
            ? selected
            : CsProjCoreToolchain.NetCoreApp11_0;

        var nativeGenerator = (CsProjBuilder)nativeToolchain.Builder;
        // WHY: BDN 0.16 couples an explicit toolchain to its runtime. Compare the native
        // resolved assignment, not a raw job whose implicit runtime has not been materialized.
        var nativeSettings = JobSettings(nativeJob.WithToolchain(nativeToolchain));

        // Act
        IConfig boundConfig = new SourceBoundConfig(baseConfig, parsedConfig);
        var boundJobs = boundConfig
            .GetJobs()
            .ToArray();

        var boundSettings = boundJobs
            .Select(JobSettings)
            .ToArray();

        // Assert
        Assert.True(parsed);
        var boundJob = Assert.Single(boundJobs);
        Assert.Equal(nativeSettings, Assert.Single(boundSettings));
        var boundToolchain = Assert.IsType<SourceBoundToolchain>(boundJob.Infrastructure.Toolchain);
        Assert.Equal(nativeToolchain.ToString(), boundToolchain.ToString());
        Assert.Same(nativeToolchain.Runtime, boundToolchain.Runtime);
        Assert.Same(nativeToolchain.Executor, boundToolchain.Executor);
        Assert.Same(((IHasSettings)nativeToolchain).Settings, ((IHasSettings)boundToolchain).Settings);
        var boundGenerator = Assert.IsType<CsProjBuilder>(boundToolchain.Builder, exactMatch: false);
        Assert.Same(nativeGenerator.Settings, boundGenerator.Settings);
        Assert.Equal(nativeGenerator.EntryPointType, boundGenerator.EntryPointType);
        Assert.Equal(nativeGenerator.BenchmarkRunCallType, boundGenerator.BenchmarkRunCallType);
    }

    /// <summary>Keeps native in-process toolchains and job settings without wrapping their generator.</summary>
    /// <param name="arguments">The native in-process argument vector.</param>
    [Theory]
    [InlineData("--inProcess")]
    [InlineData("--inProcess", "--job", "Dry")]
    [InlineData("--inProcess", "--job", "Short")]
    public void SourceBoundConfig_PreservesNativeInProcessToolchain(
        params string[] arguments
    )
    {
        // Arrange
        var baseConfig = ManualConfig.Create(DefaultConfig.Instance);
        var (parsed, parsedConfig, _) = ConfigParser.Parse(arguments, NullLogger.Instance, baseConfig);
        ArgumentNullException.ThrowIfNull(parsedConfig);

        var nativeConfig = ImmutableConfigBuilder.Create(ManualConfig.Union(baseConfig, parsedConfig));
        var nativeJob = nativeConfig
            .GetJobs()
            .Single();

        var nativeSettings = JobSettings(nativeJob);

        // Act
        IConfig boundConfig = new SourceBoundConfig(baseConfig, parsedConfig);
        var boundJobs = boundConfig
            .GetJobs()
            .ToArray();

        var boundSettings = boundJobs
            .Select(JobSettings)
            .ToArray();

        // Assert
        Assert.True(parsed);
        var boundJob = Assert.Single(boundJobs);
        Assert.Equal(nativeSettings, Assert.Single(boundSettings));
        Assert.NotNull(boundJob.Infrastructure.Toolchain);
        Assert.True(boundJob.Infrastructure.Toolchain.IsInProcess);
        Assert.Same(nativeJob.Infrastructure.Toolchain, boundJob.Infrastructure.Toolchain);
    }

    /// <summary>Preserves either native artifact-directory spelling over a different base directory.</summary>
    /// <param name="option">The long or short native artifact-directory option.</param>
    [Theory]
    [InlineData("--artifacts")]
    [InlineData("-a")]
    public void SourceBoundConfig_PreservesNativeArtifactDirectory(
        string option
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var baseConfig = ManualConfig
            .Create(DefaultConfig.Instance)
            .WithArtifactsPath(Path.Combine(evidence.DirectoryPath, "base-artifacts"));

        var (parsed, parsedConfig, _) = ConfigParser.Parse(
            ["--filter", "*", option, evidence.DirectoryPath],
            NullLogger.Instance,
            baseConfig);
        ArgumentNullException.ThrowIfNull(parsedConfig);

        // Act
        var boundConfig = new SourceBoundConfig(baseConfig, parsedConfig);
        var path = boundConfig.ArtifactsPath;

        // Assert
        Assert.True(parsed);
        Assert.Equal(evidence.DirectoryPath, path);
        Assert.Equal(parsedConfig.ArtifactsPath, path);
        Assert.NotEqual(baseConfig.ArtifactsPath, path);
    }

    /// <summary>Counts native filtered cases, including individual parameters and a no-match filter.</summary>
    /// <param name="filter">The native glob expression.</param>
    /// <param name="expectedCases">The independently known selected-case count.</param>
    [Theory]
    [InlineData("*", 2)]
    [InlineData("*BenchmarkProgramOutcomeProbe.Measure*", 2)]
    [InlineData("*OperationCount: 1*", 1)]
    [InlineData("*OperationCount: 2*", 1)]
    [InlineData("*no_such_benchmark*", 0)]
    public void SourceBoundConfig_PreservesNativeFilteredCaseCount(
        string filter,
        int expectedCases
    )
    {
        // Arrange
        var baseConfig = ManualConfig.Create(DefaultConfig.Instance);
        var (parsed, parsedConfig, _) = ConfigParser.Parse(
            ["--filter", filter, "--job", "Dry"],
            NullLogger.Instance,
            baseConfig);
        ArgumentNullException.ThrowIfNull(parsedConfig);

        var boundConfig = new SourceBoundConfig(baseConfig, parsedConfig);
        var (validTypes, runnableTypes) = TypeFilter.GetTypesWithRunnableBenchmarks(
            [typeof(BenchmarkProgramOutcomeProbe)],
            [],
            NullLogger.Instance);

        // Act
        var selected = TypeFilter.Filter(boundConfig, runnableTypes);
        var selectedCases = selected.Sum(static info => info.BenchmarksCases.Length);

        // Assert
        Assert.True(parsed);
        Assert.True(validTypes);
        Assert.Equal(expectedCases, selectedCases);
    }

    /// <summary>Resolves the actual test project rather than artifacts or another project's name match.</summary>
    [Fact]
    public void FindProject_ResolvesExactBuildStampedSourceProject()
    {
        // Arrange
        var expectedPath = ExpectedTestProjectPath();

        // Act
        var project = SourceBoundToolchain.FindProject(typeof(BenchmarkProgramOutcomeProbe));

        // Assert
        Assert.Equal(expectedPath, project.FullName);
        Assert.True(project.Exists);
        Assert.Equal("Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj", project.Name);
    }

    /// <summary>Rejects unrelated assemblies that have no build-stamped benchmark source project.</summary>
    [Fact]
    public void FindProject_RejectsAssemblyWithoutSourceProjectMetadata()
    {
        // Arrange
        var unrelatedType = typeof(object);

        // Act
        var exception = Record.Exception(() => SourceBoundToolchain.FindProject(unrelatedType));

        // Assert
        Assert.IsType<InvalidDataException>(exception);
    }

    /// <summary>Keeps native compile/runtime assets transitive to generated hosts in all five executables.</summary>
    /// <param name="projectName">One of the finite reviewed benchmark executable projects.</param>
    [Theory]
    [InlineData("Doka.EntityFrameworkCore.SafeMigrations.Benchmarks")]
    [InlineData("Doka.EntityFrameworkCore.SafeMigrations.MySql.Benchmarks")]
    [InlineData("Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Benchmarks")]
    [InlineData("Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Benchmarks")]
    [InlineData("Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks")]
    public void BenchmarkProject_PreservesGeneratedHostNativeDependency(
        string projectName
    )
    {
        // Arrange
        var benchmarkRoot = Path.Combine(RepositoryRoot(), "benchmarks");
        var sharedPath = Path.Combine(benchmarkRoot, "Shared", "BenchmarkProject.props");
        var projectPath = Path.Combine(benchmarkRoot, projectName, projectName + ".csproj");

        // Act
        var shared = XDocument.Load(sharedPath);
        var project = XDocument.Load(projectPath);
        var sharedReferences = shared
            .Descendants("PackageReference")
            .Where(static element => (string?)element.Attribute("Include") == "BenchmarkDotNet")
            .ToArray();

        var localOverrides = project
            .Descendants("PackageReference")
            .Where(static element => (string?)element.Attribute("Include") == "BenchmarkDotNet"
                || (string?)element.Attribute("Update") == "BenchmarkDotNet")
            .ToArray();

        // Assert
        var reference = Assert.Single(sharedReferences);
        Assert.Null(reference.Attribute("PrivateAssets"));
        Assert.Null(reference.Element("PrivateAssets"));
        Assert.Null(reference.Attribute("ExcludeAssets"));
        Assert.Null(reference.Element("ExcludeAssets"));
        Assert.Empty(localOverrides);
        var import = Assert.Single(project.Descendants("Import"));
        Assert.Equal("../Shared/BenchmarkProject.props", (string?)import.Attribute("Project"));
    }

    /// <summary>Isolates builder instances with identical assembly, partition and program names.</summary>
    [Fact]
    public void SourceBoundBuilder_IsolatesIdenticalNativeBuildPartitionAcrossInvocations()
    {
        // Arrange
        var partition = CreateBuildPartition();
        var firstGenerator = CreateSourceBoundBuilder();
        var secondGenerator = CreateSourceBoundBuilder();
        var nativePath = BuildDirectory(
            new NativeProjectControlBuilder((CsProjBuilder)CsProjCoreToolchain.NetCoreApp11_0.Builder),
            partition,
            partition.ProgramName);

        // Act
        var firstPath = BuildDirectory(firstGenerator, partition, partition.ProgramName);
        var secondPath = BuildDirectory(secondGenerator, partition, partition.ProgramName);

        // Assert
        Assert.NotEqual(firstPath, secondPath);
        Assert.True(Path.IsPathFullyQualified(firstPath));
        Assert.True(Path.IsPathFullyQualified(secondPath));
        Assert.StartsWith(nativePath + "-", firstPath, StringComparison.Ordinal);
        Assert.StartsWith(nativePath + "-", secondPath, StringComparison.Ordinal);
    }

    /// <summary>Keeps the build directory stable for repeated calculations on the same builder.</summary>
    [Fact]
    public void SourceBoundBuilder_ReusesOwnBuildDirectoryForSamePartitionAndProgram()
    {
        // Arrange
        var partition = CreateBuildPartition();
        var generator = CreateSourceBoundBuilder();

        // Act
        var firstPath = BuildDirectory(generator, partition, partition.ProgramName);
        var repeatedPath = BuildDirectory(generator, partition, partition.ProgramName);

        // Assert
        Assert.Equal(firstPath, repeatedPath);
        Assert.True(Path.IsPathFullyQualified(firstPath));
    }

    /// <summary>Isolates generated builds even though separate report roots do not change native build paths.</summary>
    [Fact]
    public void SourceBoundBuilder_IsolatesBuildsIndependentlyOfReportArtifactDirectories()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var firstConfig = ManualConfig
            .Create(DefaultConfig.Instance)
            .WithArtifactsPath(Path.Combine(evidence.DirectoryPath, "first-reports"));

        var secondConfig = ManualConfig
            .Create(DefaultConfig.Instance)
            .WithArtifactsPath(Path.Combine(evidence.DirectoryPath, "second-reports"));

        var firstPartition = CreateBuildPartition(firstConfig);
        var secondPartition = CreateBuildPartition(secondConfig);
        var firstGenerator = CreateSourceBoundBuilder();
        var secondGenerator = CreateSourceBoundBuilder();
        const string programName = "same-assembly-Dry-1";
        var nativeGenerator = new NativeProjectControlBuilder(
            (CsProjBuilder)CsProjCoreToolchain.NetCoreApp11_0.Builder);

        // Act
        var firstNativePath = BuildDirectory(nativeGenerator, firstPartition, programName);
        var secondNativePath = BuildDirectory(nativeGenerator, secondPartition, programName);
        var firstPath = BuildDirectory(firstGenerator, firstPartition, programName);
        var secondPath = BuildDirectory(secondGenerator, secondPartition, programName);

        // Assert
        Assert.NotEqual(firstConfig.ArtifactsPath, secondConfig.ArtifactsPath);
        // WHY: BDN 0.16 assigns distinct partition IDs even when only report roots differ.
        // Both native paths must still share the source-local build root, not either report root.
        Assert.NotEqual(firstNativePath, secondNativePath);
        Assert.Equal(Path.GetDirectoryName(firstNativePath), Path.GetDirectoryName(secondNativePath));
        var sourceProjectDirectory = Assert.IsType<string>(Path.GetDirectoryName(ExpectedTestProjectPath()));
        Assert.Equal(Path.Combine(sourceProjectDirectory, ".bdn"), Path.GetDirectoryName(firstNativePath));
        Assert.NotEqual(firstPath, secondPath);
        Assert.True(Path.IsPathFullyQualified(firstPath));
        Assert.True(Path.IsPathFullyQualified(secondPath));
        Assert.StartsWith(firstNativePath + "-", firstPath, StringComparison.Ordinal);
        Assert.StartsWith(secondNativePath + "-", secondPath, StringComparison.Ordinal);
    }

    /// <summary>Creates the native build-partition input without generating or compiling a child project.</summary>
    /// <param name="config">The optional report configuration, independent of generated build directories.</param>
    /// <returns>A real native partition for one parameterized probe case.</returns>
    private static BuildPartition CreateBuildPartition(
        ManualConfig? config = null
    )
    {
        config ??= ManualConfig.Create(DefaultConfig.Instance);
        config.AddJob(Job.Dry);
        var benchmark = BenchmarkConverter
            .TypeToBenchmarks(typeof(BenchmarkProgramOutcomeProbe), config)
            .BenchmarksCases[0];

        var info = new BenchmarkBuildInfo(benchmark, benchmark.Config, 0, new CompositeInProcessDiagnoser([]));

        return new BuildPartition([info], EngineResolver.Instance);
    }

    /// <summary>Creates an independent source-bound builder for the same native toolchain.</summary>
    /// <returns>A builder with its own invocation-scoped build-directory identity.</returns>
    private static IBuilder CreateSourceBoundBuilder()
    {
        var native = CsProjCoreToolchain.NetCoreApp11_0;

        return new SourceBoundToolchain(native, (CsProjBuilder)native.Builder).Builder;
    }

    /// <summary>Isolates native path calculation from its unrelated recursive project-name search.</summary>
    private sealed class NativeProjectControlBuilder : CsProjBuilder
    {
        /// <summary>Keeps native path and build settings while selecting the independently known source.</summary>
        /// <param name="native">The native builder whose immutable settings remain authoritative.</param>
        internal NativeProjectControlBuilder(
            CsProjBuilder native
        ) : base(native.Settings) { }

        /// <inheritdoc />
        protected override FileInfo GetProjectFilePath(
            Type benchmarkTarget,
            ILogger logger
        ) => new(ExpectedTestProjectPath());
    }

    /// <summary>Invokes only the protected path calculation, including native behavior as a control.</summary>
    /// <param name="generator">The native or source-bound builder.</param>
    /// <param name="partition">The same native build input used for the compared invocations.</param>
    /// <param name="programName">The deliberately identical native program name.</param>
    /// <returns>The calculated directory without creating files or launching benchmark children.</returns>
    private static string BuildDirectory(
        IBuilder generator,
        BuildPartition partition,
        string programName
    )
    {
        var method = generator
                .GetType()
                .GetMethod(
                    "GetBuildArtifactsDirectoryPath",
                    BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("Native builder build-directory calculation is unavailable.");

        return method.Invoke(generator, [partition, programName]) as string
            ?? throw new InvalidDataException("Native builder did not return a build directory.");
    }

    /// <summary>Captures all configured leaf values except the one intentionally changed by source binding.</summary>
    /// <param name="job">The native or source-bound job.</param>
    /// <returns>Ordered exact values, excluding generated IDs and the source-bound toolchain.</returns>
    private static (string Name, object? Value)[] JobSettings(
        Job job
    ) => job
        .GetCharacteristicsWithValues()
        .Where(static characteristic => !characteristic.HasChildCharacteristics
            && characteristic != InfrastructureMode.ToolchainCharacteristic
            && characteristic != CharacteristicObject.IdCharacteristic)
        .OrderBy(static characteristic => characteristic.FullId, StringComparer.Ordinal)
        .Select(characteristic => (characteristic.FullId, characteristic[job]))
        .ToArray();

    /// <summary>Finds the repository independently of the assembly's source-project metadata.</summary>
    /// <returns>The actual source test project's absolute path.</returns>
    private static string ExpectedTestProjectPath() => Path.Combine(
        RepositoryRoot(),
        "tests",
        "Doka.EntityFrameworkCore.SafeMigrations.Tests",
        "Doka.EntityFrameworkCore.SafeMigrations.Tests.csproj");

    /// <summary>Locates the source tree without assuming the current working directory.</summary>
    /// <returns>The source repository root containing the solution.</returns>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Doka.EntityFrameworkCore.SafeMigrations.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the SafeMigrations source test project.");
    }
}
