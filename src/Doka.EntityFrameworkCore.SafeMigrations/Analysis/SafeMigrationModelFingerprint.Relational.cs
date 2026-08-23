namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationModelFingerprint
{
    private static void WriteRelationalModel(
        CanonicalHashWriter writer,
        IRelationalModel model
    )
    {
        writer.Add("relational-model");
        writer.Add(model.Collation);
        WriteAnnotations(writer, model.Model);
        WriteAnnotations(writer, model);
        WriteCollection(writer, model.Tables, TableKey, WriteTable);
        WriteCollection(writer, model.Views, ViewKey, WriteView);
        WriteCollection(writer, model.Queries, static query => query.Name, WriteQuery);
        WriteCollection(writer, model.Sequences, SequenceKey, WriteSequence);
        WriteCollection(writer, model.Functions, FunctionKey, WriteFunction);
        WriteCollection(writer, model.StoredProcedures, StoredProcedureKey, WriteStoredProcedure);
    }

    private static void WriteTable(
        CanonicalHashWriter writer,
        ITable table
    )
    {
        writer.Add(table.Schema);
        writer.Add(table.Name);
        writer.Add(table.Comment);
        writer.Add(table.IsExcludedFromMigrations);
        WriteAnnotations(writer, table);
        WriteCollection(writer, table.Columns, static column => column.Name, WriteColumn);
        WriteCollection(writer, table.UniqueConstraints, static constraint => constraint.Name, WriteUniqueConstraint);
        WriteCollection(writer, table.ForeignKeyConstraints, ForeignKeyKey, WriteForeignKey);
        WriteCollection(writer, table.Indexes, static index => index.Name, WriteIndex);
        WriteCollection(
            writer,
            table.CheckConstraints,
            static check => check.Name ?? string.Empty,
            WriteCheckConstraint);
        WriteCollection(writer, table.Triggers, static trigger => trigger.ModelName, WriteTrigger);
    }

    private static void WriteColumn(
        CanonicalHashWriter writer,
        IColumn column
    )
    {
        var mappings = column.PropertyMappings;
        var mapping = mappings[0];
        var property = mapping.Property;

        if (mappings.Count > 1)
        {
            var storeObject = StoreObjectIdentifier.Table(column.Table.Name, column.Table.Schema);
            var rootProperty = property.FindSharedStoreObjectRootProperty(storeObject);

            if (rootProperty is not null)
            {
                property = rootProperty;
                mapping = mappings.First(candidate => ReferenceEquals(candidate.Property, rootProperty));
            }
        }

        writer.Add(column.Name);
        writer.Add(column.StoreType);
        writer.Add(column.ProviderClrType.FullName);
        writer.Add(column.IsNullable);
        writer.Add(property.GetMaxLength());
        writer.Add(property.GetPrecision());
        writer.Add(property.GetScale());
        writer.Add(property.IsUnicode());
        writer.Add(property.IsFixedLength());
        writer.Add(property.IsConcurrencyToken && property.ValueGenerated == ValueGenerated.OnAddOrUpdate);
        writer.Add(property.GetCollation());
        writer.Add(property.GetComment());
        writer.Add(property.GetColumnOrder());
        writer.Add(property.GetComputedColumnSql());
        writer.Add(property.GetIsStored());
        writer.Add(property.GetDefaultValueSql());
        WriteColumnDefaultValue(writer, mapping);
        WriteAnnotations(writer, column);
    }

    private static void WriteColumnDefaultValue(
        CanonicalHashWriter writer,
        Microsoft.EntityFrameworkCore.Metadata.IColumnMapping mapping
    )
    {
        var annotation = mapping.Property.FindAnnotation(RelationalAnnotationNames.DefaultValue);
        if (annotation is null)
        {
            writer.Add(false);
            return;
        }

        var converter = mapping.TypeMapping.Converter;
        writer.Add(true);
        WriteValue(
            writer,
            converter is null ? annotation.Value : converter.ConvertToProvider(annotation.Value),
            "column default value");
    }

    private static void WriteColumnBase(
        CanonicalHashWriter writer,
        IColumnBase column
    )
    {
        writer.Add(column.Name);
        writer.Add(column.StoreType);
        writer.Add(column.ProviderClrType.FullName);
        writer.Add(column.IsNullable);
        WriteAnnotations(writer, column);
    }

    private static void WriteUniqueConstraint(
        CanonicalHashWriter writer,
        IUniqueConstraint constraint
    )
    {
        writer.Add(constraint.Name);
        WriteNames(writer, constraint.Columns.Select(static column => column.Name));
        WriteAnnotations(writer, constraint);
    }

    private static void WriteForeignKey(
        CanonicalHashWriter writer,
        IForeignKeyConstraint foreignKey
    )
    {
        writer.Add(foreignKey.Name);
        WriteNames(writer, foreignKey.Columns.Select(static column => column.Name));
        writer.Add(foreignKey.PrincipalTable.Schema);
        writer.Add(foreignKey.PrincipalTable.Name);
        WriteNames(writer, foreignKey.PrincipalColumns.Select(static column => column.Name));
        writer.Add((int)foreignKey.OnDeleteAction);
        WriteAnnotations(writer, foreignKey);
    }

    private static void WriteIndex(
        CanonicalHashWriter writer,
        ITableIndex index
    )
    {
        writer.Add(index.Name);
        writer.Add(index.IsUnique);
        writer.Add(index.Filter);
        WriteNames(writer, index.Columns.Select(static column => column.Name));
        writer.Add(index.IsDescending?.Count ?? -1);
        foreach (var descending in index.IsDescending ?? [])
        {
            writer.Add(descending);
        }

        WriteAnnotations(writer, index);
    }

    private static void WriteCheckConstraint(
        CanonicalHashWriter writer,
        ICheckConstraint checkConstraint
    )
    {
        writer.Add(checkConstraint.Name);
        writer.Add(checkConstraint.Sql);
        WriteAnnotations(writer, checkConstraint);
    }

    private static void WriteTrigger(
        CanonicalHashWriter writer,
        ITrigger trigger
    )
    {
        writer.Add(trigger.ModelName);
        WriteAnnotations(writer, trigger);
    }

    private static void WriteView(
        CanonicalHashWriter writer,
        IView view
    )
    {
        writer.Add(view.Schema);
        writer.Add(view.Name);
        writer.Add(view.ViewDefinitionSql);
        WriteAnnotations(writer, view);
        WriteCollection(writer, view.Columns, static column => column.Name, WriteColumnBase);
    }

    private static void WriteQuery(
        CanonicalHashWriter writer,
        ISqlQuery query
    )
    {
        writer.Add(query.Name);
        writer.Add(query.Sql);
        WriteAnnotations(writer, query);
        WriteCollection(writer, query.Columns, static column => column.Name, WriteColumnBase);
    }

    private static void WriteSequence(
        CanonicalHashWriter writer,
        ISequence sequence
    )
    {
        writer.Add(sequence.Schema);
        writer.Add(sequence.Name);
        writer.Add(sequence.Type.FullName);
        writer.Add(sequence.StartValue);
        writer.Add(sequence.IncrementBy);
        writer.Add(sequence.MinValue);
        writer.Add(sequence.MaxValue);
        writer.Add(sequence.IsCyclic);
        WriteAnnotations(writer, sequence);
    }

    private static void WriteFunction(
        CanonicalHashWriter writer,
        IStoreFunction function
    )
    {
        writer.Add(function.Schema);
        writer.Add(function.Name);
        writer.Add(function.ReturnType);
        writer.Add(function.IsBuiltIn);
        WriteAnnotations(writer, function);
        WriteCollection(writer, function.Parameters, FunctionParameterKey, WriteFunctionParameter);
        WriteCollection(writer, function.Columns, static column => column.Name, WriteColumnBase);
    }

    private static void WriteFunctionParameter(
        CanonicalHashWriter writer,
        IStoreFunctionParameter parameter
    )
    {
        writer.Add(parameter.Name);
        writer.Add(parameter.StoreType);
        WriteAnnotations(writer, parameter);
    }

    private static void WriteStoredProcedure(
        CanonicalHashWriter writer,
        IStoreStoredProcedure procedure
    )
    {
        writer.Add(procedure.Schema);
        writer.Add(procedure.Name);
        WriteAnnotations(writer, procedure);
        WriteCollection(writer, procedure.Parameters, StoredProcedureParameterKey, WriteStoredProcedureParameter);
        WriteCollection(writer, procedure.ResultColumns, StoredProcedureResultKey, WriteStoredProcedureResultColumn);

        writer.Add(procedure.ReturnValue is not null);
        if (procedure.ReturnValue is not null)
        {
            WriteStoredProcedureReturnValue(writer, procedure.ReturnValue);
        }
    }

    private static void WriteStoredProcedureParameter(
        CanonicalHashWriter writer,
        IStoreStoredProcedureParameter parameter
    )
    {
        writer.Add(parameter.Position);
        writer.Add(parameter.Name);
        writer.Add(parameter.StoreType);
        writer.Add(parameter.ProviderClrType.FullName);
        writer.Add(parameter.IsNullable);
        writer.Add((int)parameter.Direction);
        WriteAnnotations(writer, parameter);
    }

    private static void WriteStoredProcedureResultColumn(
        CanonicalHashWriter writer,
        IStoreStoredProcedureResultColumn column
    )
    {
        writer.Add(column.Position);
        writer.Add(column.Name);
        writer.Add(column.StoreType);
        writer.Add(column.ProviderClrType.FullName);
        writer.Add(column.IsNullable);
        WriteAnnotations(writer, column);
    }

    private static void WriteStoredProcedureReturnValue(
        CanonicalHashWriter writer,
        IStoreStoredProcedureReturnValue value
    )
    {
        writer.Add(value.Name);
        writer.Add(value.StoreType);
        writer.Add(value.ProviderClrType.FullName);
        writer.Add(value.IsNullable);
        WriteAnnotations(writer, value);
    }

    private static void WriteNames(
        CanonicalHashWriter writer,
        IEnumerable<string> names
    )
    {
        var values = names.ToArray();
        writer.Add(values.Length);
        foreach (var value in values)
        {
            writer.Add(value);
        }
    }

    private static void WriteCollection<T>(
        CanonicalHashWriter writer,
        IEnumerable<T> values,
        Func<T, string> keySelector,
        Action<CanonicalHashWriter, T> write
    )
    {
        var ordered = values
            .OrderBy(keySelector, StringComparer.Ordinal)
            .ToArray();

        writer.Add(ordered.Length);
        foreach (var value in ordered)
        {
            write(writer, value);
        }
    }

    private static string TableKey(
        ITable table
    ) => JoinKey(table.Schema, table.Name);

    private static string ViewKey(
        IView view
    ) => JoinKey(view.Schema, view.Name);

    private static string SequenceKey(
        ISequence sequence
    ) => JoinKey(sequence.Schema, sequence.Name);

    private static string FunctionKey(
        IStoreFunction function
    ) => JoinKey(
        function.Schema,
        function.Name,
        string.Join('\u001f', function.Parameters.Select(static parameter => parameter.StoreType)));

    private static string FunctionParameterKey(
        IStoreFunctionParameter parameter
    ) => JoinKey(parameter.Name, parameter.StoreType);

    private static string StoredProcedureKey(
        IStoreStoredProcedure procedure
    ) => JoinKey(procedure.Schema, procedure.Name);

    private static string StoredProcedureParameterKey(
        IStoreStoredProcedureParameter parameter
    ) => parameter.Position.ToString("D10", CultureInfo.InvariantCulture);

    private static string StoredProcedureResultKey(
        IStoreStoredProcedureResultColumn column
    ) => column.Position.ToString("D10", CultureInfo.InvariantCulture);

    private static string ForeignKeyKey(
        IForeignKeyConstraint foreignKey
    ) => JoinKey(foreignKey.Name, foreignKey.PrincipalTable.Schema, foreignKey.PrincipalTable.Name);

    private static string JoinKey(
        params string?[] components
    ) => string.Join('\u001f', components.Select(static value => value ?? string.Empty));
}
