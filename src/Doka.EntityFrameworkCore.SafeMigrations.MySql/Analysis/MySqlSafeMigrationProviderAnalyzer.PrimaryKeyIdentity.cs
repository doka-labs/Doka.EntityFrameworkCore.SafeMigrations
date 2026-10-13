namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationProviderAnalyzer
{
    // WHY: Native ALTER TABLE DROP PRIMARY KEY addresses MySQL/MariaDB's
    // singleton PRIMARY index, not the authored constraint symbol. Other
    // providers retain the interface's name-sensitive physical identity.

    /// <inheritdoc />
    bool ISafeMigrationProjectedKeyAnalyzer.PrimaryKeyUsesNameIndependentIdentity => true;
}
