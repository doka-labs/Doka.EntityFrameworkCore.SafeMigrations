namespace Doka.EntityFrameworkCore.SafeMigrations;

public sealed partial class SafeMigrationRunner
{
    private partial struct OrderedAssessmentReducer
    {
        /// <summary>
        /// Records provider ownership before observing its effects on later evidence and uncertainty origins.
        /// </summary>
        private void ObserveProviderOperation(
            MigrationOperation operation,
            int ordinal,
            long? managedDataVersion,
            long? globalDataVersion
        )
        {
            _hasProviderOperations = true;
            _assessments.Add(
                new SafeMigrationAssessment(
                    ordinal,
                    operation.GetType()
                        .FullName
                    ?? operation.GetType()
                        .Name,
                    isSafeOperation: false,
                    operationKind: null,
                    objectName: null,
                    observedState: null,
                    action: null,
                    postconditionSatisfied: null,
                    "provider_owned_not_analyzed",
                    analysisCode: "provider_owned_not_analyzed",
                    decisionCode: "provider_owned_not_analyzed",
                    SafeMigrationOperationalImpact.NotApplicable,
                    differences: null,
                    deferredOrigin: null,
                    identity: CaptureProviderIdentity(operation),
                    migrationId: _migrationIds?[ordinal]));

            if (_preflightProjection is not null)
            {
                _preflightProjection.ObserveProviderPostcondition(operation);

                if (_preflightProjection.ModelManagedDataChangeVersion != managedDataVersion)
                {
                    _managedDataOrigin = new SafeMigrationDeferredOrigin(
                        _migrationIds?[ordinal],
                        ordinal,
                        operation.GetType()
                            .FullName
                        ?? operation.GetType()
                            .Name);
                }

                if (_preflightProjection.DataMutationVersion != globalDataVersion)
                {
                    _globalDataOrigin = _managedDataOrigin;
                }

                if (_preflightProjection.HasOpaqueSqlPostcondition
                    && operation is SqlOperation)
                {
                    _rawSqlOrigin ??= new SafeMigrationDeferredOrigin(
                        _migrationIds?[ordinal],
                        ordinal,
                        operation.GetType()
                            .FullName
                        ?? operation.GetType()
                            .Name);
                }
            }
        }

        /// <summary>Applies local, opaque and provider-certified provenance in their established precedence.</summary>
        private readonly SafeMigrationDeferredOrigin? GetDeferredOrigin(
            SafeMigrationProviderAnalysis analysis,
            int ordinal
        )
        {
            var deferredOrigin = analysis switch
            {
                { IsModelManagedProjectionUnknown: true, HasStaleGlobalModelManagedData: true }
                    => _globalDataOrigin ?? _managedDataOrigin,
                { IsModelManagedProjectionUnknown: true } => _managedDataOrigin,
                { IsOpaqueProjectionUnknown: true } => _rawSqlOrigin,
                _ => null,
            };

            if (analysis is
                {
                    IsModelManagedProjectionUnknown: true,
                    HasStaleGlobalModelManagedData: false,
                    ModelManagedLocalOriginOrdinal: { } localOriginOrdinal,
                })
            {
                var originOperation = _operations[localOriginOrdinal];

                deferredOrigin = new SafeMigrationDeferredOrigin(
                    _migrationIds?[localOriginOrdinal],
                    localOriginOrdinal,
                    originOperation.GetType()
                        .FullName
                    ?? originOperation.GetType()
                        .Name);
            }

            if (analysis.ProviderDeferredOriginOrdinal is { } providerOriginOrdinal)
            {
                // WHY: A provider may invalidate row evidence through typed or
                // safe DDL rather than SQL. Only a preceding stream position is
                // a valid provenance certificate; a bad certificate fails closed.
                if (providerOriginOrdinal < 0
                    || providerOriginOrdinal >= ordinal)
                {
                    throw new InvalidOperationException(
                        "The provider returned an invalid deferred-validation origin.");
                }

                var originOperation = _operations[providerOriginOrdinal];

                deferredOrigin = new SafeMigrationDeferredOrigin(
                    _migrationIds?[providerOriginOrdinal],
                    providerOriginOrdinal,
                    originOperation.GetType()
                        .FullName
                    ?? originOperation.GetType()
                        .Name);
            }

            return deferredOrigin;
        }
    }
}
