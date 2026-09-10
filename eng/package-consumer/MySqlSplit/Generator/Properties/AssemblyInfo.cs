#if SAFE_MIGRATIONS_SOURCE_REFERENCE
using Microsoft.EntityFrameworkCore.Design;

[assembly: DesignTimeServicesReference(
    "Doka.EntityFrameworkCore.SafeMigrations.MySql.MySqlSafeMigrationDesignTimeServices, "
    + "Doka.EntityFrameworkCore.SafeMigrations.MySql",
    "Doka.EntityFrameworkCore.MySql")]
#endif
