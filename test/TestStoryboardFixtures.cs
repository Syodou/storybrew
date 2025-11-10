using OpenTK;
using StorybrewCommon.Mapset;
using StorybrewCommon.Storyboarding;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Storybrew.Tests.Helpers
{
    internal sealed class TemporaryStoryboardPaths : IDisposable
    {
        public string RootPath { get; }
        public string AssetsPath { get; }
        public string MapsetPath { get; }

        private bool disposed;

        public TemporaryStoryboardPaths()
        {
            var basePath = Path.Combine(Path.GetTempPath(), "storybrew-tests", Guid.NewGuid().ToString("N"));
            RootPath = basePath;
            AssetsPath = Path.Combine(basePath, "assets");
            MapsetPath = Path.Combine(basePath, "mapset");

            Directory.CreateDirectory(AssetsPath);
            Directory.CreateDirectory(MapsetPath);
        }

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            try
            {
                if (Directory.Exists(RootPath))
                    Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal sealed class TestGeneratorContext : GeneratorContext, IDisposable
    {
        private readonly Func<string, StoryboardLayer> factory;
        private readonly Dictionary<string, StoryboardLayer> layers = new(StringComparer.Ordinal);
        private StoryboardLayer unnamedLayer;
        private readonly TemporaryStoryboardPaths paths = new();
        private bool disposed;
        private bool multithreaded;

        public TestGeneratorContext(Func<string, StoryboardLayer> factory)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public List<StoryboardLayer> CreatedLayers { get; } = new();
        public List<StoryboardLayer> AccessedLayers { get; } = new();

        public int LocalFactoryCalls { get; private set; }
            = 0;

        public override string ProjectPath => paths.RootPath;
        public override string ProjectAssetPath => paths.AssetsPath;
        public override string MapsetPath => paths.MapsetPath;

        public override void AddDependency(string path)
        {
        }

        public override void AppendLog(string message)
        {
        }

        public override Beatmap Beatmap => null;
        public override IEnumerable<Beatmap> Beatmaps => Array.Empty<Beatmap>();

        public void UseSharedContext(StoryboardContext context)
            => StoryboardContext = context;

        protected override StoryboardLayer GetOrCreateLayer(string identifier)
        {
            if (identifier == null)
            {
                unnamedLayer ??= createLayer(null);
                return unnamedLayer;
            }

            if (!layers.TryGetValue(identifier, out var layer))
            {
                layer = createLayer(identifier);
                layers.Add(identifier, layer);
            }
            return layer;
        }

        protected override IEnumerable<StoryboardLayer> EnumerateLocalLayers()
        {
            if (unnamedLayer != null)
                yield return unnamedLayer;

            foreach (var layer in layers.Values)
                yield return layer;
        }

        protected override void OnLayerCreated(StoryboardLayer layer)
            => CreatedLayers.Add(layer);

        protected override void OnLayerAccessed(StoryboardLayer layer)
            => AccessedLayers.Add(layer);

        protected override StoryboardLayer CreateLayerForContext(string identifier)
            => GetOrCreateLayer(identifier);

        private StoryboardLayer createLayer(string identifier)
        {
            LocalFactoryCalls++;
            return factory(identifier);
        }

        public override double AudioDuration => 0;

        public override float[] GetFft(double time, string path = null, bool splitChannels = false)
            => Array.Empty<float>();

        public override float GetFftFrequency(string path = null)
            => 0f;

        public override bool Multithreaded
        {
            get => multithreaded;
            set => multithreaded = value;
        }

        public override CancellationToken CancellationToken => CancellationToken.None;

        public void Dispose()
        {
            if (disposed)
                return;

            disposed = true;
            paths.Dispose();
        }
    }

    internal sealed class TestStoryboardLayer : StoryboardLayer
    {
        private readonly List<StoryboardObject> objects = new();
        private readonly Dictionary<string, TestStoryboardLayer> segments = new(StringComparer.Ordinal);

        public TestStoryboardLayer(string identifier)
            : base(identifier)
        {
        }

        public override double StartTime => 0;
        public override double EndTime => 0;

        public override Vector2 Origin { get; set; } = Vector2.Zero;
        public override Vector2 Position { get; set; } = Vector2.Zero;
        public override double Rotation { get; set; } = 0;
        public override double Scale { get; set; } = 1;
        public override bool ReverseDepth { get; set; }
            = false;

        public override IReadOnlyList<StoryboardObject> Objects => objects;

        public override IEnumerable<StoryboardSegment> NamedSegments => segments.Values;

        public override StoryboardSegment CreateSegment(string identifier = null)
        {
            if (identifier != null && segments.ContainsKey(identifier))
                throw new InvalidOperationException($"Duplicate segment identifier '{identifier}'.");

            var segment = new TestStoryboardLayer(identifier);
            objects.Add(segment);
            if (identifier != null)
                segments.Add(identifier, segment);
            return segment;
        }

        public override StoryboardSegment GetSegment(string identifier)
        {
            if (identifier == null)
                return null;
            segments.TryGetValue(identifier, out var segment);
            return segment;
        }

        public override OsbSprite CreateSprite(string path, OsbOrigin origin, Vector2 initialPosition)
        {
            var sprite = new OsbSprite
            {
                TexturePath = path,
                Origin = origin,
                InitialPosition = initialPosition,
            };
            objects.Add(sprite);
            return sprite;
        }

        public override OsbSprite CreateSprite(string path, OsbOrigin origin = OsbOrigin.Centre)
            => CreateSprite(path, origin, OsbSprite.DefaultPosition);

        public override OsbAnimation CreateAnimation(string path, int frameCount, double frameDelay, OsbLoopType loopType, OsbOrigin origin, Vector2 initialPosition)
        {
            var animation = new OsbAnimation
            {
                TexturePath = path,
                FrameCount = frameCount,
                FrameDelay = frameDelay,
                LoopType = loopType,
                Origin = origin,
                InitialPosition = initialPosition,
            };
            objects.Add(animation);
            return animation;
        }

        public override OsbAnimation CreateAnimation(string path, int frameCount, double frameDelay, OsbLoopType loopType, OsbOrigin origin = OsbOrigin.Centre)
            => CreateAnimation(path, frameCount, frameDelay, loopType, origin, OsbSprite.DefaultPosition);

        public override OsbSample CreateSample(string path, double time, double volume = 100)
        {
            var sample = new OsbSample
            {
                AudioPath = path,
                Time = time,
                Volume = volume,
            };
            objects.Add(sample);
            return sample;
        }

        public override void Discard(StoryboardObject storyboardObject)
        {
            objects.Remove(storyboardObject);
            if (storyboardObject is TestStoryboardLayer segment && segment.Identifier != null)
                segments.Remove(segment.Identifier);
        }

        public override void WriteOsb(TextWriter writer, ExportSettings exportSettings, OsbLayer layer, StoryboardTransform transform, CancellationToken token = default)
        {
        }
    }
}
