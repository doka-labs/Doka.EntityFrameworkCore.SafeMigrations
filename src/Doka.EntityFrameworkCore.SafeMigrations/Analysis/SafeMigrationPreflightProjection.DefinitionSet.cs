namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private sealed class ProjectedDefinitionSet<T>(
        IEqualityComparer<T> semanticComparer,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer,
        StringComparer? memberComparer = null
    )
        where T : class
    {
        private readonly IdentifierComparer _identifierComparer = new(objectIdentityNormalizer, memberComparer);

        private readonly Dictionary<string, PhysicalDefinition> _aliases = new(
            new IdentifierComparer(objectIdentityNormalizer, memberComparer));

        private readonly HashSet<string> _missingNames =
            new(new IdentifierComparer(objectIdentityNormalizer, memberComparer));

        private readonly Dictionary<string, PhysicalDefinition> _physicalDefinitions =
            new(new IdentifierComparer(objectIdentityNormalizer, memberComparer));

        private readonly Dictionary<string, T> _removedDefinitions =
            new(new IdentifierComparer(objectIdentityNormalizer, memberComparer));

        private readonly HashSet<string> _unresolvedMissingNames =
            new(new IdentifierComparer(objectIdentityNormalizer, memberComparer));

        // WHY: A semantic NoOp is an alias for an existing physical object, not
        // another object. Keeping that binding lets one drop invalidate every
        // alias without hiding a distinct physical object with the same shape.
        private readonly Dictionary<T, HashSet<PhysicalDefinition>> _semanticDefinitions = new(semanticComparer);
        private readonly HashSet<T> _mutatedSemanticDefinitions = new(semanticComparer);
        private HashSet<T>? _unboundDefinitions;
        private bool _unboundIdentityInvalidated;

        /// <summary>Gets the accepted removal ordinal that invalidated unresolved member identities.</summary>
        public int? IdentityUnknownOriginOrdinal { get; private set; }

        /// <summary>Gets whether accepted physical or unbound semantic shapes require column invalidation.</summary>
        public bool HasAcceptedDefinitions => _physicalDefinitions.Count > 0 || _unboundDefinitions?.Count > 0;

        /// <summary>Enumerates physical and unbound semantic prerequisites without cloning either set.</summary>
        public IEnumerable<T> Definitions =>
            _unboundDefinitions is null
                ? _physicalDefinitions.Values.Select(static value => value.Definition)
                : _physicalDefinitions
                    .Values
                    .Select(static value => value.Definition)
                    .Concat(_unboundDefinitions);

        /// <summary>Tests accepted prerequisite shapes without materializing a second collection.</summary>
        public bool Any(
            Func<T, bool> predicate
        ) => _physicalDefinitions.Values.Any(value => predicate(value.Definition))
            || _unboundDefinitions?.Any(predicate) == true;

        /// <summary>Tests accepted shapes with explicit state instead of allocating a per-query closure.</summary>
        /// <typeparam name="TState">The immutable caller's lookup state.</typeparam>
        /// <param name="state">The state consumed by the static predicate.</param>
        /// <param name="predicate">The non-capturing accepted-shape predicate.</param>
        public bool Any<TState>(
            TState state,
            Func<T, TState, bool> predicate
        )
        {
            foreach (var physical in _physicalDefinitions.Values)
            {
                if (predicate(physical.Definition, state))
                {
                    return true;
                }
            }

            if (_unboundDefinitions is not null)
            {
                foreach (var definition in _unboundDefinitions)
                {
                    if (predicate(definition, state))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Tests only fresh accepted Apply lifetimes, never matching aliases or shape-only facts.</summary>
        /// <typeparam name="TState">The immutable caller's lookup state.</typeparam>
        /// <param name="state">The state consumed by the static predicate.</param>
        /// <param name="predicate">The non-capturing current-shape predicate.</param>
        public bool AnyCreated<TState>(
            TState state,
            Func<T, TState, bool> predicate
        )
        {
            foreach (var physical in _physicalDefinitions.Values)
            {
                if (physical.CreatedByProjection
                    && predicate(physical.Definition, state))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Retains semantic prerequisite shape without inventing a physical member name.</summary>
        /// <param name="definition">The shape proven by a matching existing-table contract.</param>
        public void AcceptShapeOnly(
            T definition
        )
        {
            if (_unboundIdentityInvalidated || _semanticDefinitions.ContainsKey(definition))
            {
                return;
            }

            // WHY: Table matching can ignore constraint symbols. Allocate this
            // separate set only for live shapes that lack physical-name proof.
            _unboundDefinitions ??= new HashSet<T>(semanticComparer);
            _unboundDefinitions.Add(definition);
        }

        /// <summary>Checks whether immutable live shape may describe an unresolved removed physical member.</summary>
        /// <param name="analysis">The original live analysis.</param>
        public bool HasUnprovenMutatedIdentity(
            SafeMigrationProviderAnalysis analysis
        ) => _unboundIdentityInvalidated
            && (analysis.ObservedState == SafeMigrationObservedState.Different
                || analysis is { ObservedState: SafeMigrationObservedState.Matching, MatchedObjectName: null });

        /// <summary>Rebinds immutable identities without discarding accepted aliases or removal provenance.</summary>
        public void Rebind(
            Func<T, T> transform
        )
        {
            _semanticDefinitions.Clear();
            foreach (var physical in _physicalDefinitions.Values)
            {
                physical.Definition = transform(physical.Definition);
                if (!_semanticDefinitions.TryGetValue(physical.Definition, out var definitions))
                {
                    definitions = [];
                    _semanticDefinitions.Add(physical.Definition, definitions);
                }

                definitions.Add(physical);
            }

            foreach (var name in _removedDefinitions.Keys.ToArray())
            {
                _removedDefinitions[name] = transform(_removedDefinitions[name]);
            }

            var mutated = _mutatedSemanticDefinitions.ToArray();
            _mutatedSemanticDefinitions.Clear();
            foreach (var definition in mutated)
            {
                _mutatedSemanticDefinitions.Add(transform(definition));
            }

            if (_unboundDefinitions is not null)
            {
                var unbound = _unboundDefinitions.ToArray();
                _unboundDefinitions.Clear();
                foreach (var definition in unbound)
                {
                    _unboundDefinitions.Add(transform(definition));
                }
            }
        }

        public void AcceptPhysical(
            string physicalName,
            T definition,
            bool createdByProjection = false
        )
        {
            if (_physicalDefinitions.Remove(physicalName, out var previous))
            {
                RemovePhysical(previous);
            }

            RemoveAlias(physicalName);
            RemoveMissingName(physicalName);

            var physical = new PhysicalDefinition(physicalName, definition, _identifierComparer, createdByProjection);
            physical.Aliases.Add(physicalName);
            _physicalDefinitions.Add(physicalName, physical);
            _aliases[physicalName] = physical;

            if (!_semanticDefinitions.TryGetValue(definition, out var definitions))
            {
                definitions = [];
                _semanticDefinitions.Add(definition, definitions);
            }

            definitions.Add(physical);
            _unboundDefinitions?.Remove(definition);
        }

        public bool BindAlias(
            string alias,
            string physicalName,
            T definition
        )
        {
            if (_missingNames.Contains(physicalName)
                && !_physicalDefinitions.ContainsKey(physicalName))
            {
                // WHY: Immutable native Matching can still name a removed
                // candidate while a different semantic candidate survives.
                // Only accepted creation may reintroduce that physical lifetime.
                return false;
            }

            if (!_physicalDefinitions.TryGetValue(physicalName, out var physical))
            {
                AcceptPhysical(physicalName, definition);
                physical = _physicalDefinitions[physicalName];
            }
            else if (!semanticComparer.Equals(physical.Definition, definition))
            {
                return false;
            }

            if (_physicalDefinitions.ContainsKey(alias)
                && !_identifierComparer.Equals(alias, physicalName))
            {
                return false;
            }

            RemoveAlias(alias);
            RemoveMissingName(alias);
            _aliases[alias] = physical;
            physical.Aliases.Add(alias);

            return true;
        }

        public bool BindUniqueSemanticAlias(
            string alias,
            T definition
        )
        {
            if (!_semanticDefinitions.TryGetValue(definition, out var definitions)
                || definitions.Count != 1)
            {
                return false;
            }

            var physical = definitions.Single();

            return BindAlias(alias, physical.Name, definition);
        }

        public bool ContainsSemantically(
            T definition
        ) => _semanticDefinitions.ContainsKey(definition) || _unboundDefinitions?.Contains(definition) == true;

        /// <summary>Checks exact semantic provenance retained from an accepted physical removal.</summary>
        /// <param name="expected">The requested semantics, excluding only the logical member name.</param>
        public bool HasRemovedSemantically(
            T expected
        )
        {
            foreach (var removed in _removedDefinitions.Values)
            {
                if (semanticComparer.Equals(removed, expected))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Resolves an exact bound name or a deterministic accepted semantic physical candidate.</summary>
        /// <param name="name">The requested logical or physical name.</param>
        /// <param name="expected">The required accepted semantics.</param>
        /// <param name="physicalName">The actual accepted physical object's name.</param>
        public bool TryResolvePhysicalName(
            string name,
            T expected,
            [NotNullWhen(true)] out string? physicalName
        )
        {
            if (_aliases.TryGetValue(name, out var exact)
                && semanticComparer.Equals(exact.Definition, expected))
            {
                physicalName = exact.Name;

                return true;
            }

            physicalName = null;

            if (!_semanticDefinitions.TryGetValue(expected, out var candidates))
            {
                return false;
            }

            foreach (var candidate in candidates)
            {
                if (physicalName is null
                    || StringComparer.Ordinal.Compare(candidate.Name, physicalName) < 0)
                {
                    physicalName = candidate.Name;
                }
            }

            return physicalName is not null;
        }

        /// <summary>Distinguishes accepted physical presence from tombstones and logical-only aliases.</summary>
        /// <param name="name">The requested physical name.</param>
        /// <param name="present">Whether that exact physical object is accepted as present.</param>
        public bool TryGetPhysicalState(
            string name,
            out bool present
        )
        {
            present = _physicalDefinitions.ContainsKey(name);

            return present || _missingNames.Contains(name) || _aliases.ContainsKey(name);
        }

        /// <summary>Reads an accepted exact physical definition without following a logical alias.</summary>
        /// <param name="name">The independently certified physical key name.</param>
        /// <param name="definition">The definition of that accepted physical lifetime.</param>
        public bool TryGetPhysicalDefinition(
            string name,
            [NotNullWhen(true)] out T? definition
        )
        {
            definition = _physicalDefinitions.TryGetValue(name, out var physical) ? physical.Definition : null;

            return definition is not null;
        }

        /// <summary>Checks whether an accepted physical creation proves the current object's fresh lifetime.</summary>
        /// <param name="name">The exact physical member name, not a semantic alias.</param>
        public bool WasCreatedByProjection(
            string name
        ) => _physicalDefinitions.TryGetValue(name, out var physical) && physical.CreatedByProjection;

        /// <summary>Records actual known owned index names without allocating an iterator or a name snapshot.</summary>
        /// <param name="tombstones">The existing physical-key removal set.</param>
        /// <param name="owner">The physical owner whose lifetime is being removed.</param>
        public void CaptureIndexTombstones(
            HashSet<IndexKey> tombstones,
            TableKey owner
        )
        {
            foreach (var physical in _physicalDefinitions.Values)
            {
                tombstones.Add(new IndexKey(owner.Table, owner.Schema, physical.Name));
            }
        }

        /// <summary>Allows exact live identity only for physical names untouched by accepted bookkeeping.</summary>
        /// <param name="name">The requested physical name.</param>
        /// <param name="physicalName">The immutable provider's resolved physical name.</param>
        public bool CanReuseExactLivePhysicalName(
            string name,
            string physicalName
        ) => _identifierComparer.Equals(name, physicalName) && !TryGetPhysicalState(physicalName, out _);

        public void MarkPhysicalMissing(
            string physicalName,
            int removalOrdinal = -1
        )
        {
            if (_unboundDefinitions?.Count > 0 || _unboundIdentityInvalidated)
            {
                // WHY: A literal physical drop cannot identify which unnamed
                // semantic shape it removed. Invalidate only this member family;
                // exact physical bindings in the same owner remain authoritative.
                _unboundDefinitions?.Clear();
                _unboundIdentityInvalidated = true;
                IdentityUnknownOriginOrdinal = removalOrdinal >= 0 ? removalOrdinal : IdentityUnknownOriginOrdinal;
            }

            if (RemovePhysical(physicalName, out _))
            {
                return;
            }

            RemoveAlias(physicalName);
            _missingNames.Add(physicalName);
            _removedDefinitions.Remove(physicalName);
            _unresolvedMissingNames.Add(physicalName);
        }

        public bool SemanticallyEquals(
            T left,
            T right
        ) => semanticComparer.Equals(left, right);

        public bool RemovePhysical(
            string physicalName,
            [NotNullWhen(true)] out T? definition
        )
        {
            if (!_physicalDefinitions.Remove(physicalName, out var physical))
            {
                definition = null;
                return false;
            }

            definition = physical.Definition;
            RemovePhysical(physical);

            return true;
        }

        public bool RemoveWhere(
            Func<T, bool> predicate
        )
        {
            ArgumentNullException.ThrowIfNull(predicate);

            var names = _physicalDefinitions
                .Where(pair => predicate(pair.Value.Definition))
                .Select(static pair => pair.Key)
                .ToArray();

            foreach (var name in names)
            {
                _ = RemovePhysical(name, out _);
            }

            var unboundRemoved = _unboundDefinitions?.RemoveWhere(value => predicate(value)) ?? 0;

            return names.Length > 0 || unboundRemoved > 0;
        }

        public ProjectedDefinitionMutation ResolveMutation(
            string name,
            T expected,
            SafeMigrationProviderAnalysis liveAnalysis
        )
        {
            if (liveAnalysis.ObservedState is not (SafeMigrationObservedState.Missing
                or SafeMigrationObservedState.Matching
                or SafeMigrationObservedState.Different))
            {
                return ProjectedDefinitionMutation.None;
            }

            if (_missingNames.Contains(name))
            {
                if (_removedDefinitions.TryGetValue(name, out var removed)
                    && semanticComparer.Equals(removed, expected))
                {
                    return ProjectedDefinitionMutation.Missing;
                }

                return liveAnalysis is
                    {
                        ObservedState: SafeMigrationObservedState.Matching, MatchedObjectName: not null,
                    }
                    && _identifierComparer.Equals(liveAnalysis.MatchedObjectName, name)
                        ? ProjectedDefinitionMutation.Missing
                        : ProjectedDefinitionMutation.Unknown;
            }

            if (liveAnalysis.MatchedObjectName is { } physicalName
                && _missingNames.Contains(physicalName))
            {
                return liveAnalysis.ObservedState == SafeMigrationObservedState.Matching
                    ? ProjectedDefinitionMutation.Missing
                    : ProjectedDefinitionMutation.Unknown;
            }

            // WHY: A missing provider identity means the immutable live match
            // may have represented more than one semantic candidate. Once one
            // such candidate was mutated, the remaining live state is unknown.

            return liveAnalysis is { ObservedState: SafeMigrationObservedState.Matching, MatchedObjectName: null }
                && (_unresolvedMissingNames.Count > 0 || _mutatedSemanticDefinitions.Contains(expected))
                    ? ProjectedDefinitionMutation.Unknown
                    : ProjectedDefinitionMutation.None;
        }

        public bool TryGetValue(
            string name,
            [NotNullWhen(true)] out T? definition
        )
        {
            if (_aliases.TryGetValue(name, out var physical))
            {
                definition = physical.Definition;
                return true;
            }

            definition = null;
            return false;
        }

        private void RemoveAlias(
            string alias
        )
        {
            if (!_aliases.Remove(alias, out var physical))
            {
                return;
            }

            physical.Aliases.Remove(alias);
        }

        private void RemoveMissingName(
            string name
        )
        {
            _missingNames.Remove(name);
            _removedDefinitions.Remove(name);
            _unresolvedMissingNames.Remove(name);
        }

        private void RemovePhysical(
            PhysicalDefinition physical
        )
        {
            foreach (var alias in physical.Aliases)
            {
                _aliases.Remove(alias);
                _missingNames.Add(alias);
                _removedDefinitions[alias] = physical.Definition;
                _unresolvedMissingNames.Remove(alias);
            }

            _mutatedSemanticDefinitions.Add(physical.Definition);

            if (_semanticDefinitions.TryGetValue(physical.Definition, out var definitions))
            {
                definitions.Remove(physical);
                if (definitions.Count == 0)
                {
                    _semanticDefinitions.Remove(physical.Definition);
                }
            }
        }

        private sealed class PhysicalDefinition(
            string name,
            T definition,
            IEqualityComparer<string> identifierComparer,
            bool createdByProjection
        )
        {
            public HashSet<string> Aliases { get; } = new(identifierComparer);

            public T Definition { get; set; } = definition;

            public string Name { get; } = name;

            /// <summary>Gets whether accepted DDL created this lifetime rather than observing a live alias.</summary>
            public bool CreatedByProjection { get; } = createdByProjection;
        }
    }

    private enum ProjectedDefinitionMutation : byte
    {
        None = 0,
        Missing = 1,
        Unknown = 2,
    }
}
