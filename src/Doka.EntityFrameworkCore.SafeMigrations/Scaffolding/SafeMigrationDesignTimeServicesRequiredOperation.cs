namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Guards model differences against scaffolding without the SafeMigrations
/// design-time services.
/// </summary>
/// <remarks>
/// The SafeMigrations code generator consumes this operation before delegating
/// to the provider generator. EF Core's ordinary generator cannot render the
/// operation and therefore fails closed instead of emitting ordinary migration
/// calls when package build assets are missing or excluded. SafeMigrations
/// provider adapters also consume the marker when EF Core uses the decorated
/// runtime model differ to create migration-history SQL.
/// </remarks>
internal sealed class SafeMigrationDesignTimeServicesRequiredOperation : MigrationOperation;
