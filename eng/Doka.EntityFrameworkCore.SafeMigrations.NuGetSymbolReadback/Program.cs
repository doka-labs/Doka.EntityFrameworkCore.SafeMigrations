namespace Doka.EntityFrameworkCore.SafeMigrations.NuGetSymbolReadback;

internal static class Program
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    internal static async Task<int> Main(
        string[] args
    )
    {
        try
        {
            var options = ParseOptions(args);
            var manifest = SymbolReadbackManifestBuilder.Build(
                options.PackageDirectory,
                options.Version);

            var json = JsonSerializer.Serialize(manifest, s_jsonOptions) + Environment.NewLine;
            var output = Path.GetFullPath(options.Output);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await File.WriteAllTextAsync(output, json, CancellationToken.None);

            Console.WriteLine($"Validated {manifest.Symbols.Count} public symbol probes.");

            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or BadImageFormatException
                                              or InvalidDataException
                                              or IOException
                                              or JsonException)
        {
            Console.Error.WriteLine($"NuGet symbol manifest failed: {exception.Message}");

            return 1;
        }
    }

    private static Options ParseOptions(
        string[] args
    )
    {
        string? packageDirectory = null;
        string? version = null;
        string? output = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--package-dir":
                    packageDirectory = ReadValue(args, ref index);
                    break;
                case "--version":
                    version = ReadValue(args, ref index);
                    break;
                case "--output":
                    output = ReadValue(args, ref index);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'.", nameof(args));
            }
        }

        if (packageDirectory is null
            || version is null
            || output is null)
        {
            throw new ArgumentException(
                "Usage: NuGetSymbolReadback --package-dir <path> --version <version> --output <path>",
                nameof(args));
        }

        return new Options(packageDirectory, version, output);
    }

    private static string ReadValue(
        string[] args,
        ref int index
    )
    {
        if (++index >= args.Length
            || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException("Command option requires a value.", nameof(args));
        }

        return args[index];
    }

    private sealed record Options(
        string PackageDirectory,
        string Version,
        string Output
    );
}
