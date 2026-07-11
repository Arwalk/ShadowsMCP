using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ShadowsMcp
{
    /// <summary>
    /// Session-scoped ids for game entities that lack a stable native id (units, persons,
    /// social groups, challenges). Ids look like "U17", "P42", "SG5", "C8" and are assigned
    /// on first serialization.
    ///
    /// Save-game safety: uses only weak references toward game objects — nothing is ever
    /// stored on the game's object graph, so saves are untouched and discarded maps can be
    /// garbage collected. Ids reset whenever a different Map instance is seen (new game / load).
    /// </summary>
    public sealed class EntityRegistry
    {
        private readonly object _lock = new object();
        private ConditionalWeakTable<object, string> _idByEntity = new ConditionalWeakTable<object, string>();
        private Dictionary<string, WeakReference> _entityById = new Dictionary<string, WeakReference>(StringComparer.Ordinal);
        private int _nextId = 1;

        public string IdFor(object entity, string prefix)
        {
            if (entity == null) return null;
            lock (_lock)
            {
                string id;
                if (_idByEntity.TryGetValue(entity, out id)) return id;
                id = prefix + _nextId++;
                _idByEntity.Add(entity, id);
                _entityById[id] = new WeakReference(entity);
                return id;
            }
        }

        /// <summary>Returns the entity for a registry id, or null if unknown or collected.</summary>
        public object Resolve(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_lock)
            {
                WeakReference wr;
                if (!_entityById.TryGetValue(id, out wr)) return null;
                return wr.Target;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _idByEntity = new ConditionalWeakTable<object, string>();
                _entityById = new Dictionary<string, WeakReference>(StringComparer.Ordinal);
                _nextId = 1;
            }
        }
    }
}
