namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Checks asynchronous functional evidence without starting any BenchmarkDotNet measurements.</summary>
public sealed class MaintenanceAttributionEvidenceTests
{
    /// <summary>Persists the exact functional inventory and retains an explicitly nonmeasurement label.</summary>
    [Fact]
    public async Task WriteAsync_PersistsCompleteFunctionalEvidence()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var path = Path.Combine(evidence.DirectoryPath, "nested", "functional.json");
        object[] results =
        [
            new
            {
                Marker = "independent-functional-marker",
                Count = 3,
            },
        ];

        // Act
        await MaintenanceAttribution.WriteAsync(path, "synthetic client control", results, CancellationToken.None);

        // Assert
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, CancellationToken.None));
        var root = document.RootElement;
        var result = Assert.Single(root
            .GetProperty("Results")
            .EnumerateArray());

        Assert.Equal("independent-functional-marker",
            result
                .GetProperty("Marker")
                .GetString());

        Assert.Equal(3,
            result
                .GetProperty("Count")
                .GetInt32());

        Assert.Equal("synthetic client control",
            root
                .GetProperty("Provider")
                .GetString());

        Assert.Equal(Environment.Version.ToString(),
            root
                .GetProperty("Runtime")
                .GetString());

        Assert.Contains("Functional controls only",
            root
                .GetProperty("Evidence")
                .GetString(),
            StringComparison.Ordinal);

        Assert.False(root.TryGetProperty("Statistics", out _));
        Assert.False(root.TryGetProperty("Measurements", out _));
    }

    /// <summary>Forwards pre-cancellation to the native file API without replacing existing evidence.</summary>
    [Fact]
    public async Task WriteAsync_PreservesExistingEvidenceWhenPreCanceled()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        const string original = "independent-existing-evidence-marker";
        await File.WriteAllTextAsync(evidence.ReportPath, original, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var exception = await Record.ExceptionAsync(() => MaintenanceAttribution.WriteAsync(
            evidence.ReportPath,
            "synthetic client control",
            [],
            cancellation.Token));

        // Assert
        var canceled = Assert.IsType<OperationCanceledException>(exception, exactMatch: false);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.Equal(original, await File.ReadAllTextAsync(evidence.ReportPath, CancellationToken.None));
    }

    /// <summary>Does not turn a requested export failure into successful functional evidence.</summary>
    [Fact]
    public async Task WriteAsync_PropagatesInvalidDestination()
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();

        // Act
        var exception = await Record.ExceptionAsync(() => MaintenanceAttribution.WriteAsync(
            evidence.DirectoryPath,
            "synthetic client control",
            [],
            CancellationToken.None));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(exception);
        Assert.True(Directory.Exists(evidence.DirectoryPath));
    }
}
