using System;
using System.Collections.Generic;
using System.Linq;

namespace StorybrewCommon.Storyboarding
{
    /// <summary>
    /// Represents a runtime view over storyboard layers shared across one or more generators.
    /// The context owns layer lifetime, keeps references stable, and exposes snapshots for safe iteration.
    /// </summary>
    public class StoryboardContext
    {
        private readonly Dictionary<string, StoryboardLayer> layers;
        private readonly List<StoryboardLayer> orderedLayers;
        private readonly object syncRoot = new object();
        private StoryboardLayer unnamedLayer;

        public StoryboardContext()
            : this(StringComparer.Ordinal)
        {
        }

        public StoryboardContext(IEqualityComparer<string> comparer)
        {
            layers = new Dictionary<string, StoryboardLayer>(comparer ?? StringComparer.Ordinal);
            orderedLayers = new List<StoryboardLayer>();
        }

        /// <summary>
        /// Raised when a new layer is materialised by the context.
        /// </summary>
        public event Action<StoryboardLayer> LayerCreated;

        /// <summary>
        /// The current change version. Incremented whenever the layer set mutates.
        /// </summary>
        public int Version { get; private set; }

        /// <summary>
        /// Retrieves an existing layer or creates a new one using the provided factory.
        /// </summary>
        public StoryboardLayer GetLayer(string identifier, Func<string, StoryboardLayer> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            StoryboardLayer layer;
            var created = false;

            lock (syncRoot)
            {
                if (identifier == null)
                {
                    if (unnamedLayer == null)
                    {
                        unnamedLayer = factory(null);
                        if (unnamedLayer == null)
                            throw new InvalidOperationException("Layer factory returned null for unnamed layer.");
                        Version++;
                        created = true;
                        orderedLayers.Insert(0, unnamedLayer);
                    }
                    layer = unnamedLayer;
                }
                else if (!layers.TryGetValue(identifier, out layer))
                {
                    layer = factory(identifier);
                    if (layer == null)
                        throw new InvalidOperationException($"Layer factory returned null for identifier '{identifier}'.");

                    layers.Add(identifier, layer);
                    orderedLayers.Add(layer);
                    Version++;
                    created = true;
                }
            }

            if (created)
                LayerCreated?.Invoke(layer);

            return layer;
        }

        /// <summary>
        /// Attempts to retrieve a layer without creating one.
        /// </summary>
        public bool TryGetLayer(string identifier, out StoryboardLayer layer)
        {
            lock (syncRoot)
            {
                if (identifier == null)
                {
                    layer = unnamedLayer;
                    return layer != null;
                }

                return layers.TryGetValue(identifier, out layer);
            }
        }

        /// <summary>
        /// Returns a snapshot of every layer currently registered with the context.
        /// </summary>
        public IReadOnlyList<StoryboardLayer> SnapshotLayers()
        {
            lock (syncRoot)
            {
                if (orderedLayers.Count == 0)
                    return Array.Empty<StoryboardLayer>();

                return orderedLayers.ToArray();
            }
        }

        /// <summary>
        /// Enumerates all layers, optionally creating a stable snapshot before iteration.
        /// </summary>
        public IEnumerable<StoryboardLayer> EnumerateLayers(bool snapshot = false)
        {
            if (!snapshot)
            {
                lock (syncRoot)
                {
                    foreach (var layer in orderedLayers)
                        yield return layer;
                }
                yield break;
            }

            foreach (var layer in SnapshotLayers())
                yield return layer;
        }

        /// <summary>
        /// Clears every layer reference from the context without disposing them.
        /// </summary>
        public void Reset()
        {
            lock (syncRoot)
            {
                layers.Clear();
                orderedLayers.Clear();
                unnamedLayer = null;
                Version++;
            }
        }
    }
}
