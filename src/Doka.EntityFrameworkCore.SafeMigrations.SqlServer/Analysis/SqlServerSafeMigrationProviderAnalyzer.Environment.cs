namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    /// <inheritdoc />
    public async Task<SafeMigrationProviderEnvironment> GetEnvironmentAsync(
        DbContext context,
        CancellationToken cancellationToken = default
    )
    {
        ValidateContext(context);

        var connection = context.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            return new SafeMigrationProviderEnvironment(ProviderId, "sqlserver", connection.ServerVersion);
        }
        finally
        {
            if (openedHere)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<SqlServerCatalogEnvironment> ReadCatalogEnvironmentAsync(
        DbConnection connection,
        DbTransaction? transaction,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        ApplyCommandTimeout(command, commandTimeout);
        command.CommandText =
            "SELECT SCHEMA_NAME(), CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')), "
            + "HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION'), "
            + "CASE WHEN SCHEMA_NAME() COLLATE CATALOG_DEFAULT = N'dbo' COLLATE CATALOG_DEFAULT "
            + "THEN 1 ELSE 0 END, DATABASE_PRINCIPAL_ID(), CONVERT(varchar(170), SUSER_SID(), 2), "
            + "CASE WHEN EXISTS(SELECT 1 FROM sys.triggers WHERE parent_class=0 AND is_disabled=0) "
            + "OR EXISTS(SELECT 1 FROM sys.server_triggers WHERE is_disabled=0) THEN 1 "
            + "WHEN COALESCE(HAS_PERMS_BY_NAME(DB_NAME(), N'DATABASE', N'VIEW DEFINITION'),0)<>1 "
            + "OR COALESCE(HAS_PERMS_BY_NAME(NULL,NULL,N'VIEW ANY DEFINITION'),0)<>1 THEN 2 ELSE 0 END, "
            + "CASE WHEN ("
            + SqlServerSafeMigrationCatalogSqlBuilder.ExpressionDependencyReadPermission
            + ") THEN 1 ELSE 0 END, "
            // WHY: One invocation-local bit covers SQL/CLR triggers on any
            // table or view, including indirectly modified cascade targets.
            // Complete database visibility is already mandatory above.
            + "CASE WHEN EXISTS(SELECT 1 FROM sys.triggers WHERE parent_class=1 AND is_disabled=0) "
            + "THEN 1 ELSE 0 END;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (reader.FieldCount != 9
            || !await reader.ReadAsync(cancellationToken)
            || reader.IsDBNull(0)
            || reader.IsDBNull(1))
        {
            throw new InvalidOperationException("SQL Server did not return its catalog identity.");
        }

        return new SqlServerCatalogEnvironment(
            reader.GetString(0),
            !reader.IsDBNull(2) && reader.GetInt32(2) == 1,
            !reader.IsDBNull(3) && reader.GetInt32(3) == 1,
            reader.GetString(1),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5))
        {
            DdlRowEffectRisk = reader.IsDBNull(6)
                ? SqlServerDdlRowEffectRisk.VisibilityUnproven
                : reader.GetInt32(6) switch
                {
                    0 => SqlServerDdlRowEffectRisk.None,
                    1 => SqlServerDdlRowEffectRisk.EnabledTrigger,
                    _ => SqlServerDdlRowEffectRisk.VisibilityUnproven,
                },
            CanReadExpressionDependencies = !reader.IsDBNull(7) && reader.GetInt32(7) == 1,
            HasEnabledDmlTriggers = reader.IsDBNull(8) || reader.GetInt32(8) != 0,
        };
    }

    private async Task<bool> ReadIdentifierContractAsync(
        DbConnection connection,
        DbTransaction? transaction,
        IReadOnlyList<SqlServerIdentifierReference> references,
        int? commandTimeout,
        CancellationToken cancellationToken
    )
    {
        var commands = SqlServerIdentifierContract.BuildCollisionGuardCommands(
            references,
            throwOnCollision: false,
            out var temporaryTable);

        // WHY: Prove the invocation's name is unowned before entering the
        // cleanup scope. Attention during CREATE can otherwise leave a table
        // even when ExecuteNonQueryAsync never reports successful creation.
        if (temporaryTable is not null)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            ApplyCommandTimeout(command, commandTimeout);
            command.CommandText = $"SELECT OBJECT_ID(N'tempdb..{temporaryTable}');";
            var existing = await command.ExecuteScalarAsync(cancellationToken);
            if (existing is not null and not DBNull)
            {
                throw new InvalidOperationException("SQL Server identifier guard temporary scope is occupied.");
            }
        }

        Exception? originalFailure = null;
        try
        {
            // WHY: Execute setup commands first and read the scalar verdict only from the last.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var ordinal = 0; ordinal < commands.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                ApplyCommandTimeout(command, commandTimeout);
                command.CommandText = commands[ordinal];
                if (ordinal + 1 < commands.Count)
                {
                    await command.ExecuteNonQueryAsync(cancellationToken);

                    continue;
                }

                var result = await command.ExecuteScalarAsync(cancellationToken);

                return result is not null && Convert.ToInt32(result, CultureInfo.InvariantCulture) == 1;
            }

            throw new UnreachableException();
        }
        catch (Exception exception)
        {
            originalFailure = exception;

            throw;
        }
        finally
        {
            if (temporaryTable is not null)
            {
                await CleanupTemporaryScopeAsync(
                    connection,
                    transaction,
                    temporaryTable,
                    commandTimeout,
                    originalFailure);
            }
        }
    }

    private static bool RequiresDefaultSchema(
        SafeMigrationIntent intent
    ) => intent switch
    {
        EnsureTableIntent value =>
            value.Definition.Schema is null
            || value.Definition.ForeignKeys.Any(static foreignKey => foreignKey.PrincipalSchema is null),
        DropTableIntent value => value.Schema is null,
        RenameTableIntent value => value.Schema is null,
        EnsureColumnIntent value => value.Schema is null,
        DropColumnIntent value => value.Schema is null,
        RenameColumnIntent value => value.Schema is null,
        AlterColumnIntent value => value.Schema is null,
        EnsureIndexIntent value => value.Definition.Schema is null,
        DropIndexIntent value => value.Schema is null,
        RenameIndexIntent value => value.Schema is null,
        EnsurePrimaryKeyIntent value => value.Definition.Schema is null,
        DropPrimaryKeyIntent value => value.Schema is null,
        EnsureUniqueConstraintIntent value => value.Definition.Schema is null,
        DropUniqueConstraintIntent value => value.Schema is null,
        EnsureCheckConstraintIntent value => value.Definition.Schema is null,
        DropCheckConstraintIntent value => value.Schema is null,
        EnsureForeignKeyIntent value => value.Definition.Schema is null || value.Definition.PrincipalSchema is null,
        DropForeignKeyIntent value => value.Schema is null,
        ModelManagedDataIntent value => value.Schema is null,
        _ => false,
    };

    /// <summary>Captures the database environment and current execution identity for bounded proof reuse.</summary>
    /// <param name="DefaultSchema">The current user's default schema.</param>
    /// <param name="CanSeeDatabaseMetadata">Whether complete database definitions are visible.</param>
    /// <param name="DefaultSchemaIsDbo">Whether unqualified names resolve to the supported default schema.</param>
    /// <param name="Collation">The current database collation.</param>
    /// <param name="DatabasePrincipalId">The current database principal, or null when it cannot be resolved.</param>
    /// <param name="LoginSid">The current login SID in hexadecimal, or null when it cannot be resolved.</param>
    internal readonly record struct SqlServerCatalogEnvironment(
        string DefaultSchema,
        bool CanSeeDatabaseMetadata,
        bool DefaultSchemaIsDbo,
        string Collation,
        int? DatabasePrincipalId,
        string? LoginSid
    )
    {
        /// <summary>Gets the visible trigger or metadata-visibility uncertainty for this invocation.</summary>
        public SqlServerDdlRowEffectRisk DdlRowEffectRisk { get; init; }

        /// <summary>Gets whether both database and protected-view dependency-read permissions are proven.</summary>
        public bool CanReadExpressionDependencies { get; init; }

        /// <summary>Gets whether enabled SQL or CLR DML triggers can invalidate unrelated physical metadata.</summary>
        public bool HasEnabledDmlTriggers { get; init; }

        /// <summary>Requires both execution identities before another call can reuse this proof.</summary>
        public bool HasReusableIdentity => DatabasePrincipalId.HasValue && LoginSid is not null;
    }

    /// <summary>Retains the session-scoped probes of one analysis run for its inventory pass.</summary>
    /// <remarks>
    /// WHY: Classification and the unexpected-object inventory are two calls of the same run
    /// against the same session. The environment must be read afresh to detect same-session
    /// identity changes. Its stamp gates reuse of the costlier identifier collision verdict;
    /// only a completed analysis in the active scope can publish this preamble.
    /// </remarks>
    /// <param name="Context">The context that produced the probes.</param>
    /// <param name="Environment">The observed catalog environment of that session.</param>
    /// <param name="References">The identifier references the contract verdict was proven for.</param>
    /// <param name="IdentifierSafe">Whether the identifier contract held for those references.</param>
    internal sealed record SqlServerCatalogPreamble(
        DbContext Context,
        SqlServerCatalogEnvironment Environment,
        IReadOnlyList<SqlServerIdentifierReference> References,
        bool IdentifierSafe
    )
    {
        /// <summary>Determines whether this preamble proves the identifier contract for another request.</summary>
        /// <remarks>
        /// WHY: The verdict only covers the references it was computed from. Reusing it for a
        /// different reference set would turn a cache into a silent contract change, so the
        /// comparison is exact in content and order.
        /// </remarks>
        /// <param name="context">The context of the requesting call.</param>
        /// <param name="references">The identifier references the caller needs proven.</param>
        /// <param name="environment">The freshly read environment and execution identity.</param>
        /// <returns><see langword="true" /> when the retained verdict applies unchanged.</returns>
        public bool Covers(
            DbContext context,
            IReadOnlyList<SqlServerIdentifierReference> references,
            SqlServerCatalogEnvironment environment
        )
        {
            ArgumentNullException.ThrowIfNull(references);

            if (!ReferenceEquals(Context, context)
                || References.Count != references.Count
                || !environment.HasReusableIdentity
                || Environment != environment)
            {
                return false;
            }

            // WHY: Compare both reference snapshots at the same ordinal to prove exact reuse.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
            for (var index = 0; index < references.Count; index++)
            {
                if (!References[index]
                        .Equals(references[index]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
