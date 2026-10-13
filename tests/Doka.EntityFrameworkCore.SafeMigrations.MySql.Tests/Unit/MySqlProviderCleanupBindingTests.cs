namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies exact binding of native acquisition and restoration statements.</summary>
public sealed class MySqlProviderCleanupBindingTests
{
    private const string Snapshot = "/*! SET @__doka_previous_sql_mode = @@SESSION.sql_mode */;";
    private const string Restore = "/*! SET SESSION sql_mode = @__doka_previous_sql_mode */;";

    /// <summary>Acquisition is recorded in the snapshot statement without changing transport whitespace.</summary>
    /// <param name="prefix">The original leading transport whitespace.</param>
    /// <param name="suffix">The original trailing transport whitespace.</param>
    [Theory]
    [InlineData("", "")]
    [InlineData("\r\n\t", " \n")]
    [InlineData("\v\f", "\t\r\n")]
    public void AcquisitionRetainsExactSnapshotAndSurroundingWhitespace(string prefix, string suffix)
    {
        // Arrange
        var source = prefix + Snapshot + suffix;

        // Act
        var acquired = MySqlSafeMigrationOperationHandler.BuildAcquiredNativeSqlModeSnapshot(source);

        // Assert
        Assert.Equal(prefix + "/*! SET @__doka_previous_sql_mode = @@SESSION.sql_mode, "
            + "@doka_sm_provider_setup_entered = 1 */;" + suffix, acquired);
        Assert.Equal(prefix + Snapshot + suffix, source);
    }

    /// <summary>Restoration remains conditional on the operation's own acquired snapshot.</summary>
    /// <param name="prefix">The original leading transport whitespace.</param>
    /// <param name="suffix">The original trailing transport whitespace.</param>
    [Theory]
    [InlineData("", "")]
    [InlineData("\r\n\t", " \n")]
    [InlineData("\v\f", "\t\r\n")]
    public void RestorationRetainsExactScopeAndSurroundingWhitespace(string prefix, string suffix)
    {
        // Arrange
        var source = prefix + Restore + suffix;

        // Act
        var conditional = MySqlSafeMigrationOperationHandler.BuildConditionalNativeSqlModeRestore(source);

        // Assert
        Assert.Equal(prefix + "/*! SET SESSION sql_mode = IF(COALESCE(@doka_sm_provider_setup_entered = 1, FALSE), "
            + "@__doka_previous_sql_mode, @@SESSION.sql_mode) */;" + suffix, conditional);
        Assert.Equal(prefix + Restore + suffix, source);
    }

    /// <summary>Constant drift or an executable suffix cannot silently leave acquisition unbound.</summary>
    /// <param name="snapshot">The altered or unowned native fragment.</param>
    [Theory]
    [InlineData("")]
    [InlineData("/*! SET @__doka_previous_sql_mode = @@sql_mode */;")]
    [InlineData("/*! SET @__doka_previous_sql_mode = @@SESSION.sql_mode */; DO 1;")]
    [InlineData("/*! SET @__doka_previous_sql_mode = @@SESSION.sql_mode */; /* comment */")]
    [InlineData("/*! SET @other = @@SESSION.sql_mode */;")]
    [InlineData("/*! SET @__doka_previous_sql_mode=@@SESSION.sql_mode */;")]
    public void AcquisitionRejectsFragmentsOutsideCertifiedStatement(string snapshot)
    {
        // Arrange
        var source = snapshot;

        // Act
        var exception = Record.Exception(() =>
            MySqlSafeMigrationOperationHandler.BuildAcquiredNativeSqlModeSnapshot(source));

        // Assert
        Assert.Contains("certified native SQL-mode statement", Assert.IsType<InvalidOperationException>(exception)
            .Message, StringComparison.Ordinal);
        Assert.Equal(snapshot, source);
    }

    /// <summary>An unbound restore is rejected before it can alter a session or become an inert cleanup.</summary>
    /// <param name="restore">The altered or unowned native fragment.</param>
    [Theory]
    [InlineData("")]
    [InlineData("/*! SET SESSION sql_mode = @other */;")]
    [InlineData("/*! SET SESSION sql_mode = @__doka_previous_sql_mode */; DO 1;")]
    [InlineData("/*! SET SESSION sql_mode = @__doka_previous_sql_mode */; /* comment */")]
    [InlineData("/*! SET SESSION sql_mode=@__doka_previous_sql_mode */;")]
    [InlineData("/*! SET SESSION sql_mode = IF(TRUE, @__doka_previous_sql_mode, '') */;")]
    public void RestorationRejectsFragmentsOutsideCertifiedStatement(string restore)
    {
        // Arrange
        var source = restore;

        // Act
        var exception = Record.Exception(() =>
            MySqlSafeMigrationOperationHandler.BuildConditionalNativeSqlModeRestore(source));

        // Assert
        Assert.Contains("certified native SQL-mode statement", Assert.IsType<InvalidOperationException>(exception)
            .Message, StringComparison.Ordinal);
        Assert.Equal(restore, source);
    }
}
