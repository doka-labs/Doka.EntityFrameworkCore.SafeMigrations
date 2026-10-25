namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationCatalogSqlBuilder
{
    // WHY: The existing reason column carries this exact private certificate,
    // avoiding another result field on every unrelated operation. The reader
    // accepts it only for a physically matching DROP and hides it from reports.
    internal const string EmptyGeneratedDropSetCode = "generated_column_drop_empty_set_proof";

    /// <summary>
    /// Does not apply a single-owner certificate to PostgreSQL's recursive inheritance DROP semantics.
    /// </summary>
    private string GeneratedDropStandaloneOwner(DropColumnIntent intent)
    {
        var owner = QualifiedRegclass(intent.Table, intent.Schema);

        return "NOT EXISTS(SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhparent OPERATOR(pg_catalog.=) "
            + owner + " OR i.inhrelid OPERATOR(pg_catalog.=) " + owner + ")";
    }

    /// <summary>Checks explicit native LOCK rights without assuming ownership grants revoked DML privileges.</summary>
    private string GeneratedDropLockPrivilege(DropColumnIntent intent)
        => ExplicitExclusiveLockPrivilege(intent.Table, intent.Schema);

    /// <summary>Checks explicit owner-fence authority without implicitly granting table privileges.</summary>
    private string ExplicitExclusiveLockPrivilege(string table, string? schema)
    {
        var owner = QualifiedRegclass(table, schema);

        // WHY: Ownership alone authorizes DROP but not an explicit LOCK after
        // self-revoking table privileges. MAINTAIN is available from PostgreSQL17;
        // older engines must never parse it as a privilege specification.
        return "CASE WHEN pg_catalog.current_setting('server_version_num')::pg_catalog.int4 "
            + "OPERATOR(pg_catalog.>=) 170000 THEN pg_catalog.has_table_privilege(" + owner
            + ", 'UPDATE,DELETE,TRUNCATE,MAINTAIN') ELSE pg_catalog.has_table_privilege(" + owner
            + ", 'UPDATE,DELETE,TRUNCATE') END";
    }

    /// <summary>Finds only surviving generated members which natively depend on the exact dropped column.</summary>
    private string GeneratedDropDependency(DropColumnIntent intent)
    {
        var owner = QualifiedRegclass(intent.Table, intent.Schema);

        // WHY: PostgreSQL14 stores direct pg_class/column AUTO edges and may
        // silently remove a generated dependent under RESTRICT. PostgreSQL15+
        // instead links a generated pg_attrdef through NORMAL source edges.
        // Neither ordinary indexes nor CHECK/default dependencies are vetoed.
        return "EXISTS (SELECT 1 FROM pg_catalog.pg_attribute src "
            + "JOIN pg_catalog.pg_attribute gen ON gen.attrelid OPERATOR(pg_catalog.=) src.attrelid "
            + "AND gen.attnum OPERATOR(pg_catalog.<>) src.attnum "
            + "AND gen.attgenerated OPERATOR(pg_catalog.<>) '' AND NOT gen.attisdropped "
            + "JOIN pg_catalog.pg_depend dep ON dep.refclassid OPERATOR(pg_catalog.=) "
            + "'pg_catalog.pg_class'::pg_catalog.regclass AND dep.refobjid OPERATOR(pg_catalog.=) src.attrelid "
            + "AND dep.refobjsubid OPERATOR(pg_catalog.=) src.attnum "
            + "WHERE src.attrelid OPERATOR(pg_catalog.=) " + owner
            + " AND src.attname OPERATOR(pg_catalog.=) " + Literal(intent.Name)
            + " AND src.attnum OPERATOR(pg_catalog.>) 0 AND NOT src.attisdropped AND (("
            + "dep.classid OPERATOR(pg_catalog.=) 'pg_catalog.pg_class'::pg_catalog.regclass "
            + "AND dep.objid OPERATOR(pg_catalog.=) gen.attrelid "
            + "AND dep.objsubid OPERATOR(pg_catalog.=) gen.attnum AND dep.deptype OPERATOR(pg_catalog.=) 'a') OR ("
            + "dep.classid OPERATOR(pg_catalog.=) 'pg_catalog.pg_attrdef'::pg_catalog.regclass "
            + "AND dep.objsubid OPERATOR(pg_catalog.=) 0 AND dep.deptype OPERATOR(pg_catalog.=) 'n' "
            + "AND EXISTS (SELECT 1 FROM pg_catalog.pg_attrdef ad WHERE ad.oid OPERATOR(pg_catalog.=) dep.objid "
            + "AND ad.adrelid OPERATOR(pg_catalog.=) gen.attrelid "
            + "AND ad.adnum OPERATOR(pg_catalog.=) gen.attnum))))";
    }

    /// <summary>Proves the complete native generated set remaining after this exact member disappears.</summary>
    private string NoSurvivingGeneratedDropMembers(DropColumnIntent intent)
        => "NOT EXISTS (SELECT 1 FROM pg_catalog.pg_attribute gen WHERE gen.attrelid OPERATOR(pg_catalog.=) "
            + QualifiedRegclass(intent.Table, intent.Schema)
            + " AND gen.attnum OPERATOR(pg_catalog.>) 0 AND NOT gen.attisdropped "
            + "AND gen.attname OPERATOR(pg_catalog.<>) " + Literal(intent.Name)
            + " AND gen.attgenerated OPERATOR(pg_catalog.<>) '')";
}
