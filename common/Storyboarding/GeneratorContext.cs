using StorybrewCommon.Mapset;
using System;
using System.Collections.Generic;
using System.Threading;

namespace StorybrewCommon.Storyboarding
{
    public abstract class GeneratorContext
    {
        private StoryboardContext storyboardContext;
        private readonly Action<StoryboardLayer> layerCreatedHandler;

        protected GeneratorContext()
        {
            layerCreatedHandler = handleLayerCreated;
        }

        public abstract string ProjectPath { get; }
        public abstract string ProjectAssetPath { get; }
        public abstract string MapsetPath { get; }

        public abstract void AddDependency(string path);
        public abstract void AppendLog(string message);

        public abstract Beatmap Beatmap { get; }
        public abstract IEnumerable<Beatmap> Beatmaps { get; }

        /// <summary>
        /// Retrieves a storyboard layer by identifier, optionally using a shared storyboard context.
        /// </summary>
        public virtual StoryboardLayer GetLayer(string identifier)
        {
            if (identifier == null)
                throw new ArgumentNullException(nameof(identifier));

            StoryboardLayer layer;
            if (StoryboardContext != null)
            {
                StoryboardContext.AttachLayerFactory(CreateLayerForContext);
                layer = StoryboardContext.GetLayer(identifier);
            }
            else layer = GetOrCreateLayer(identifier);

            OnLayerAccessed(layer);
            return layer;
        }

        /// <summary>
        /// Returns true when this context uses a shared storyboard context between generators.
        /// </summary>
        public bool UsesSharedStoryboardContext => StoryboardContext != null;

        /// <summary>
        /// Provides access to the active shared storyboard context.
        /// Assigning a new context wires layer lifecycle callbacks automatically.
        /// </summary>
        public StoryboardContext StoryboardContext
        {
            get => storyboardContext;
            protected set
            {
                if (ReferenceEquals(storyboardContext, value))
                    return;

                if (storyboardContext != null)
                    storyboardContext.LayerCreated -= layerCreatedHandler;

                storyboardContext = value;

                if (storyboardContext != null)
                {
                    storyboardContext.AttachLayerFactory(CreateLayerForContext);
                    storyboardContext.LayerCreated += layerCreatedHandler;
                }
            }
        }

        /// <summary>
        /// Enumerates layers, including shared contexts when available.
        /// </summary>
        public virtual IEnumerable<StoryboardLayer> EnumerateLayers(bool snapshot = false)
        {
            if (StoryboardContext != null)
                return StoryboardContext.EnumerateLayers(snapshot);

            return EnumerateLocalLayers();
        }

        protected abstract StoryboardLayer GetOrCreateLayer(string identifier);
        protected abstract IEnumerable<StoryboardLayer> EnumerateLocalLayers();

        protected virtual StoryboardLayer CreateLayerForContext(string identifier)
            => GetOrCreateLayer(identifier);

        protected virtual void OnLayerCreated(StoryboardLayer layer)
        {
        }

        protected virtual void OnLayerAccessed(StoryboardLayer layer)
        {
        }

        private void handleLayerCreated(StoryboardLayer layer)
        {
            OnLayerCreated(layer);
            OnLayerAccessed(layer);
        }

        public abstract double AudioDuration { get; }
        public abstract float[] GetFft(double time, string path = null, bool splitChannels = false);
        public abstract float GetFftFrequency(string path = null);

        public abstract bool Multithreaded { get; set; }
        public abstract CancellationToken CancellationToken { get; }
    }
}
