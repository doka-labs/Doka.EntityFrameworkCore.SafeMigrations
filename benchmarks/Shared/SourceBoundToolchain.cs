namespace Doka.EntityFrameworkCore.SafeMigrations.Benchmarks;

/// <summary>Binds native expanded jobs without overriding CLI jobs, runtimes, filters or artifact settings.</summary>
internal sealed class SourceBoundConfig : ManualConfig, IConfig
{
    /// <summary>Preserves the complete native base/CLI configuration and its already expanded job choices.</summary>
    /// <param name="baseConfig">The benchmark driver's normal diagnostics and execution policy.</param>
    /// <param name="parsedConfig">The configuration produced by BenchmarkDotNet's native CLI parser.</param>
    internal SourceBoundConfig(
        IConfig baseConfig,
        IConfig parsedConfig
    )
    {
        Add(ImmutableConfigBuilder.Create(Union(baseConfig, parsedConfig)));
    }

    /// <summary>Changes only ordinary project discovery; in-process and specialized generators stay native.</summary>
    /// <returns>The same native jobs with exact project binding where applicable.</returns>
    IEnumerable<Job> IConfig.GetJobs() => base
        .GetJobs()
        .Select(static job =>
        {
            if (!job.Infrastructure.TryGetToolchain(out var native))
            {
                native = CsProjCoreToolchain.NetCoreApp10_0;
            }

            return native is CsProjCoreToolchain && native.Generator.GetType() == typeof(CsProjGenerator)
                ? job.WithToolchain(new SourceBoundToolchain(native, (CsProjGenerator)native.Generator))
                : job;
        });
}

/// <summary>Binds project generation without replacing BDN's builder, executor or validation contract.</summary>
internal sealed class SourceBoundToolchain : Toolchain
{
    private readonly IToolchain _native;

    /// <summary>Retains the exact native runtime, CLI, restore, builder and executor choices.</summary>
    /// <param name="native">The toolchain selected by native configuration and CLI expansion.</param>
    /// <param name="generator">The plain native project generator to specialize only for source discovery.</param>
    internal SourceBoundToolchain(
        IToolchain native,
        CsProjGenerator generator
    ) : base(native.Name, new SourceProjectGenerator(generator), native.Builder, native.Executor)
    {
        _native = native;
    }

    /// <summary>Preserves the native SDK, runtime, JIT and platform validation contract.</summary>
    /// <param name="benchmarkCase">The native case being validated.</param>
    /// <param name="resolver">The native job characteristic resolver.</param>
    /// <returns>The unchanged native toolchain validation results.</returns>
    public override IEnumerable<ValidationError> Validate(
        BenchmarkCase benchmarkCase,
        IResolver resolver
    ) => _native.Validate(benchmarkCase, resolver);

    /// <summary>Uses the build-stamped source project instead of a recursive solution-wide name search.</summary>
    /// <param name="benchmarkType">The actual source benchmark assembly's public workload type.</param>
    /// <returns>The exact original project file, never an artifact or another provider's project.</returns>
    internal static FileInfo FindProject(
        Type benchmarkType
    )
    {
        var path = benchmarkType
            .Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyMetadataAttribute>()
            .SingleOrDefault(static attribute => attribute.Key == "BenchmarkProjectPath")
            ?.Value;

        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || !File.Exists(path))
        {
            throw new InvalidDataException("The benchmark assembly has no available build-stamped source project.");
        }

        return new FileInfo(path);
    }

    /// <summary>Changes only native project discovery; all native generation settings remain authoritative.</summary>
    private sealed class SourceProjectGenerator : CsProjGenerator
    {
        private readonly string _invocationId = Guid
            .NewGuid()
            .ToString("N");

        internal SourceProjectGenerator(
            CsProjGenerator native
        ) : base(native.TargetFrameworkMoniker, native.CliPath, native.PackagesPath, native.RuntimeFrameworkVersion) { }

        protected override FileInfo GetProjectFilePath(
            Type benchmarkTarget,
            ILogger logger
        ) => FindProject(benchmarkTarget);

        protected override string GetBuildArtifactsDirectoryPath(
            BuildPartition buildPartition,
            string programName
        ) =>
            // WHY: BDN's native assembly/job/counter names repeat across parent processes. Separate
            // directories prevent one simultaneous invocation from compiling or deleting another's driver.
            base.GetBuildArtifactsDirectoryPath(buildPartition, programName) + "-" + _invocationId;
    }
}
