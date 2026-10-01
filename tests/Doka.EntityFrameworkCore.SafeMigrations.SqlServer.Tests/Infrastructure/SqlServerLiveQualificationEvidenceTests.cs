namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>Verifies durable, ordered and non-sensitive qualification stage evidence.</summary>
public sealed class SqlServerLiveQualificationEvidenceTests
{
    /// <summary>Each phase is persisted immediately, preserving earlier phases instead of overwriting them.</summary>
    [Fact]
    public void WriteStage_AppendsOrderedUtcMarkers()
    {
        // Arrange
        var workload = "probe-" + Guid.NewGuid().ToString("N");
        var path = SqlServerLiveQualificationEvidence.GetPath(workload);

        try
        {
            // Act
            SqlServerLiveQualificationEvidence.WriteStage(workload, "initial-analysis");
            SqlServerLiveQualificationEvidence.WriteStage(workload, "completed");
            var lines = File.ReadAllLines(path);

            // Assert
            Assert.Equal(2, lines.Length);
            Assert.EndsWith("\tinitial-analysis", lines[0], StringComparison.Ordinal);
            Assert.EndsWith("\tcompleted", lines[1], StringComparison.Ordinal);
            Assert.All(lines, line => Assert.Equal(TimeSpan.Zero, DateTimeOffset.Parse(
                line[..line.IndexOf('\t')], CultureInfo.InvariantCulture).Offset));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Path traversal and diagnostic payloads cannot become artifact names or stage content.</summary>
    /// <param name="value">The invalid workload or stage.</param>
    [Theory]
    [InlineData("../outside")]
    [InlineData("/absolute")]
    [InlineData("connection;password")]
    [InlineData("sql\ntext")]
    [InlineData("")]
    public void WriteStage_RejectsPathsAndPayloads(string value)
    {
        // Arrange
        const string validIdentifier = "probe";

        // Act
        var workloadFailure = Record.Exception(() =>
            SqlServerLiveQualificationEvidence.WriteStage(value, validIdentifier));
        var stageFailure = Record.Exception(() =>
            SqlServerLiveQualificationEvidence.WriteStage(validIdentifier, value));

        // Assert
        Assert.IsAssignableFrom<ArgumentException>(workloadFailure);
        Assert.IsAssignableFrom<ArgumentException>(stageFailure);
    }
}
