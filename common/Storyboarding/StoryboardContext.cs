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
        private StoryboardLayer unnamedLayer;
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
    if (layerFactory == null)
        layerFactory = factory;
}

/// <summary>
/// Retrieves an existing layer or creates a new one using the provided factory.
/// </summary>
public StoryboardLayer GetLayer(string identifier, Func<string, StoryboardLayer> factory)
{
    if (identifier == null)
    {
        // Unnamed layer: use dedicated slot
        if (unnamedLayer == null)
            unnamedLayer = factory("");

        return unnamedLayer;
    }

    if (!layers.TryGetValue(identifier, out var layer))
    {
        layer = factory(identifier);
        layers.Add(identifier, layer);
        LayerCreated?.Invoke(layer);
    }

    return layer;
    }

        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

public StoryboardLayer GetLayer(string identifier, Func<string, StoryboardLayer> factory)
{
    StoryboardLayer layer;
    var created = false;

    lock (syncRoot)
    {
        if (identifier == null)
        {
            // Unnamed layer support
            if (unnamedLayer == null)
            {
                unnamedLayer = factory(null);
                if (unnamedLayer == null)
                    throw new InvalidOperationException("Layer factory returned null for unnamed layer.");
                Version++;
                created = true;
            }
            layer = unnamedLayer;
        }
        else if (!layers.TryGetValue(identifier, out layer))
        {
            layer = factory(identifier);
            if (layer == null)
                throw new InvalidOperationException($"Layer factory returned null for identifier '{identifier}'.");

            layers.Add(identifier, layer);
            Version++;
            created = true;
        }
    }

    if (created)
        LayerCreated?.Invoke(layer);

    return layer;
}

/// <summary>
/// Legacy method retained for backward compatibility.
/// Uses the configured layerFactory, if any, and delegates to the new layered API.
/// </summary>
public StoryboardLayer GetLayer(string identifier)
{
    if (layerFactory == null)
        throw new InvalidOperationException(
            "StoryboardContext requires a layer factory before creating layers in legacy mode.");

    // Forward legacy call to the new implementation
    return GetLayer(identifier, layerFactory);
}

        }

        /// <summary>
        /// Attempts to retrieve an existing layer without creating a new one.
        /// Supports unnamed layers (identifier == null).
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
                    if (unnamedLayer != null)
                        yield return unnamedLayer;

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
                unnamedLayer = null;
                Version++;
            }
        }
    }
}
