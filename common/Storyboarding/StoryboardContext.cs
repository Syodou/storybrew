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
        private readonly object syncRoot = new object();

        private Func<string, StoryboardLayer> layerFactory;

        public StoryboardContext()
            : this(StringComparer.Ordinal)
        {
        }

        public StoryboardContext(IEqualityComparer<string> comparer)
        {
            layers = new Dictionary<string, StoryboardLayer>(comparer ?? StringComparer.Ordinal);
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
        /// Attaches a creation factory that will be invoked the first time an identifier is requested.
        /// Subsequent calls keep the first factory to guarantee consistent layer types.
        /// </summary>
        public void AttachLayerFactory(Func<string, StoryboardLayer> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            lock (syncRoot)
            {
                if (layerFactory == null)
                    layerFactory = factory;
            }
        }

        /// <summary>
        /// Retrieves an existing layer or creates a new one using the configured factory.
        /// </summary>
        public StoryboardLayer GetLayer(string identifier)
        {
            if (identifier == null)
                throw new ArgumentNullException(nameof(identifier));

            lock (syncRoot)
            {
                if (!layers.TryGetValue(identifier, out var layer))
                {
                    if (layerFactory == null)
                        throw new InvalidOperationException("StoryboardContext requires a layer factory before creating layers.");

                    layer = layerFactory(identifier);
                    layers.Add(identifier, layer);
                    Version++;
                    LayerCreated?.Invoke(layer);
                }

                return layer;
            }
        }

        /// <summary>
        /// Attempts to retrieve a layer without creating one.
        /// </summary>
        public bool TryGetLayer(string identifier, out StoryboardLayer layer)
        {
            if (identifier == null)
                throw new ArgumentNullException(nameof(identifier));

            lock (syncRoot)
                return layers.TryGetValue(identifier, out layer);
        }

        /// <summary>
        /// Returns a snapshot of every layer currently registered with the context.
        /// </summary>
        public IReadOnlyList<StoryboardLayer> SnapshotLayers()
        {
            lock (syncRoot)
                return layers.Values.ToArray();
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
                    foreach (var layer in layers.Values)
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
                Version++;
            }
        }
    }
}
