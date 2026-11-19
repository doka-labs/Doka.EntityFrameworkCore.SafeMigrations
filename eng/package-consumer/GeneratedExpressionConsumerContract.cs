/// <summary>Checks expression and assembly boundaries through each actual isolated provider package.</summary>
internal static class GeneratedExpressionConsumerContract
{
    /// <summary>Verifies public CASE construction and generated metadata capture without a database.</summary>
    internal static void Verify()
    {
        var expression = SafeMigrationSql.Case(
            [new SafeMigrationSqlCaseWhenClause(SafeMigrationSql.Literal(true), SafeMigrationSql.Literal(1))],
            SafeMigrationSql.Literal(0));

        var builder = new MigrationBuilder("PackageConsumer");
        builder.AddColumnIfNotExistsFromModel(static operations => operations.AddColumn<long>(
            name: "computed_value",
            table: "expression_probe",
            nullable: true,
            computedColumnSql: "CASE WHEN source_value = 0 THEN 1 ELSE 2 END"));

        builder.AddCheckConstraintIfNotExists(
            name: "ck_expression_probe",
            table: "expression_probe",
            sql: "source_value >= 0");

        // WHY: ProjectReference-only tests cannot detect missing public types,
        // metadata bridges or compile assets in an isolated NuGet consumer.
        if (expression is not SafeMigrationSqlCaseExpression { WhenClauses.Count: 1, ElseResult: not null }
            || builder.Operations is not
            [
                SafeMigrationOperation
                {
                    Intent: EnsureColumnIntent { Definition.ComputedExpression: SafeMigrationSqlCaseExpression },
                },
                SafeMigrationOperation { Intent: EnsureCheckConstraintIntent { Definition.Expression: not null } },
            ])
        {
            throw new InvalidOperationException("The packaged generated-expression contract is incomplete.");
        }
    }

    /// <summary>Checks matching Core references and absence of emitted build-only annotations.</summary>
    /// <param name="providerAssembly">The shipping adapter loaded by this isolated consumer.</param>
    internal static void VerifyAssemblyContracts(
        Assembly providerAssembly
    )
    {
        var coreAssembly = typeof(SafeMigrationRunner).Assembly;
        var coreIdentity = coreAssembly.GetName();
        var coreReference = providerAssembly
            .GetReferencedAssemblies()
            .SingleOrDefault(reference => StringComparer.Ordinal.Equals(reference.Name, coreIdentity.Name));

        if (coreReference is null
            || coreReference.Version != coreIdentity.Version)
        {
            throw new InvalidOperationException("The packaged provider does not reference the matching Core ABI.");
        }

        VerifyNoBuildOnlyAnnotations(coreAssembly.Location);
        VerifyNoBuildOnlyAnnotations(providerAssembly.Location);
    }

    /// <summary>Reads metadata without loading optional design-time dependencies or creating attributes.</summary>
    /// <param name="assemblyPath">The shipping assembly image whose complete metadata is checked.</param>
    internal static void VerifyNoBuildOnlyAnnotations(
        string assemblyPath
    )
    {
        // WHY: GetTypes/GetCustomAttributes can require intentionally private EF Design dependencies.
        // Reading the PE tables examines every member without executing or loading those contracts.
        using var stream = File.OpenRead(assemblyPath);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();

        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            if (StringComparer.Ordinal.Equals(metadata.GetString(reference.Name), "JetBrains.Annotations"))
            {
                throw new InvalidOperationException("A build-only annotation assembly reference was emitted.");
            }
        }

        foreach (var handle in metadata.CustomAttributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);
            var owner = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => metadata.GetMemberReference(
                        (MemberReferenceHandle)attribute.Constructor)
                    .Parent,
                HandleKind.MethodDefinition => metadata
                    .GetMethodDefinition(
                        (MethodDefinitionHandle)attribute.Constructor)
                    .GetDeclaringType(),
                _ => throw new InvalidOperationException("An attribute constructor has unsupported metadata."),
            };

            var attributeNamespace = owner.Kind switch
            {
                HandleKind.TypeReference => metadata.GetString(
                    metadata.GetTypeReference((TypeReferenceHandle)owner)
                        .Namespace),
                HandleKind.TypeDefinition => metadata.GetString(
                    metadata.GetTypeDefinition((TypeDefinitionHandle)owner)
                        .Namespace),
                _ => null,
            };

            if (StringComparer.Ordinal.Equals(attributeNamespace, "JetBrains.Annotations"))
            {
                throw new InvalidOperationException("A build-only annotation attribute was emitted.");
            }
        }
    }
}
