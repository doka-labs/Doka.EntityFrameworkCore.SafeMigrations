// WHY: Linked Core tests call project discovery directly; executables also use it through the native builder.
// ReSharper disable MemberCanBePrivate.Global

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

    /// <summary>Changes only ordinary project discovery; in-process and specialized builders stay native.</summary>
    /// <returns>The same native jobs with exact project binding where applicable.</returns>
    IEnumerable<Job> IConfig.GetJobs() => base
        .GetJobs()
        .Select(static job =>
        {
            if (!job.Infrastructure.TryGetToolchain(out var native))
            {
                native = CsProjCoreToolchain.NetCoreApp11_0;
            }

            return native is CsProjCoreToolchain && native.Builder.GetType() == typeof(CsProjBuilder)
                ? job.WithToolchain(new SourceBoundToolchain(native, (CsProjBuilder)native.Builder))
                : job;
        });
}

/// <summary>Binds project discovery while retaining BDN's native build, executor and validation contracts.</summary>
internal sealed class SourceBoundToolchain : Toolchain, IHasSettings
{
    private readonly IToolchain _native;

    /// <summary>Retains the exact native runtime, CLI, restore, builder and executor choices.</summary>
    /// <param name="native">The toolchain selected by native configuration and CLI expansion.</param>
    /// <param name="builder">The plain native builder to specialize only for source discovery and isolation.</param>
    internal SourceBoundToolchain(
        IToolchain native,
        CsProjBuilder builder
    ) : base(
        native.ToString() ?? throw new InvalidDataException("The native toolchain has no display identity."),
        native.Runtime,
        new SourceProjectBuilder(builder),
        native.Executor)
    {
        _native = native;
    }

    /// <inheritdoc />
    ISettings IHasSettings.Settings => ((IHasSettings)_native).Settings;

    /// <summary>Preserves the native SDK, runtime, JIT and platform validation contract.</summary>
    /// <param name="benchmarkCase">The native case being validated.</param>
    /// <param name="resolver">The native job characteristic resolver.</param>
    /// <returns>The unchanged native toolchain validation results.</returns>
    public override IAsyncEnumerable<ValidationError> ValidateAsync(
        BenchmarkCase benchmarkCase,
        IResolver resolver
    ) => _native.ValidateAsync(benchmarkCase, resolver);

    /// <summary>Uses the build-stamped source project instead of a recursive solution-wide name search.</summary>
    /// <param name="benchmarkType">The actual source benchmark assembly's public workload type.</param>
    /// <returns>The exact original project file, never an artifact or another provider's project.</returns>
    internal static FileInfo FindProject(
        Type benchmarkType
    )
    {
        var path = benchmarkType
            .Assembly
            .GetCustomAttributes(typeof(AssemblyMetadataAttribute), inherit: false)
            .Cast<AssemblyMetadataAttribute>()
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

    /// <summary>Specializes native source discovery and isolation without changing build settings.</summary>
    private sealed class SourceProjectBuilder : CsProjBuilder
    {
        private readonly string _invocationId = Guid
            .NewGuid()
            .ToString("N");

        internal SourceProjectBuilder(
            CsProjBuilder native
        ) : base(native.Settings)
        {
            // WHY: BDN 0.16 merges generation into its builder. Reuse the full immutable settings object
            // and code-generation choices rather than reconstructing selected CLI or runtime fields.
            EntryPointType = native.EntryPointType;
            BenchmarkRunCallType = native.BenchmarkRunCallType;
        }

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
