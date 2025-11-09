using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK;
using StorybrewCommon.Mapset;
using StorybrewCommon.Storyboarding;
using StorybrewEditor.Storyboarding;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Storybrew.Tests
{
    [TestClass]
    public class StoryboardContextTests
    {
        [TestMethod]
        public void GetLayer_UsesFactoryOnlyOnce()
        {
            var context = new StoryboardContext();
            var created = new List<StoryboardLayer>();
            var calls = 0;

            StoryboardLayer Factory(string identifier)
            {
                calls++;
                var layer = new TestStoryboardLayer(identifier);
                created.Add(layer);
                return layer;
            }

            var first = context.GetLayer("alpha", Factory);
            var second = context.GetLayer("alpha", Factory);

            Assert.AreSame(first, second);
            Assert.AreEqual(1, calls, "Factory should only be invoked for the initial creation.");
            CollectionAssert.AreEqual(created, new List<StoryboardLayer> { first });
        }

        [TestMethod]
        public void GetLayer_UnnamedLayerIsSingleton()
        {
            var context = new StoryboardContext();
            var calls = 0;

            var first = context.GetLayer(null, id =>
            {
                calls++;
                return new TestStoryboardLayer(id);
            });
            var second = context.GetLayer(null, id =>
            {
                calls++;
                return new TestStoryboardLayer(id);
            });

            Assert.AreSame(first, second);
            Assert.AreEqual(1, calls, "Unnamed layer factory should only run once.");
        }

        [TestMethod]
        public void Reset_ClearsAllLayers()
        {
            var context = new StoryboardContext();
            context.GetLayer("alpha", id => new TestStoryboardLayer(id));
            context.GetLayer(null, id => new TestStoryboardLayer(id));

            context.Reset();

            Assert.IsFalse(context.TryGetLayer("alpha", out _));
            Assert.IsFalse(context.TryGetLayer(null, out _));
        }

        [TestMethod]
        public void EnumerateLayers_SnapshotDoesNotReflectLaterAdditions()
        {
            var context = new StoryboardContext();
            context.GetLayer("a", id => new TestStoryboardLayer(id));
            var snapshot = context.SnapshotLayers();

            context.GetLayer("b", id => new TestStoryboardLayer(id));

            Assert.AreEqual(1, snapshot.Count);
            Assert.AreEqual("a", snapshot[0].Identifier);
            Assert.AreEqual(2, context.SnapshotLayers().Count);
        }

        [TestMethod]
        public void LayerCreatedEventFiresForAllSubscribers()
        {
            var context = new StoryboardContext();
            var generatorA = new TestGeneratorContext(id => new TestStoryboardLayer(id));
            var generatorB = new TestGeneratorContext(id => new TestStoryboardLayer(id));

            generatorA.UseSharedContext(context);
            generatorB.UseSharedContext(context);

            var layer = generatorA.GetLayer("shared");

            Assert.AreSame(layer, generatorB.GetLayer("shared"));
            Assert.AreEqual(1, generatorA.CreatedLayers.Count, "Creator should observe its own creation.");
            Assert.AreEqual(1, generatorB.CreatedLayers.Count, "Other generators must observe shared layer creation.");
        }

        [TestMethod]
        public void DetachingSharedContextStopsEventPropagation()
        {
            var context = new StoryboardContext();
            var generatorA = new TestGeneratorContext(id => new TestStoryboardLayer(id));
            var generatorB = new TestGeneratorContext(id => new TestStoryboardLayer(id));

            generatorA.UseSharedContext(context);
            generatorB.UseSharedContext(context);

            generatorA.GetLayer("first");
            generatorA.UseSharedContext(null);

            generatorB.GetLayer("second");

            Assert.AreEqual(1, generatorA.CreatedLayers.Count, "Detached generator should ignore subsequent creations.");
            Assert.AreEqual(2, generatorB.CreatedLayers.Count, "Active generator tracks every creation.");
        }
    }

    [TestClass]
    public class StoryboardSegmentEnumerationTests
    {
        [TestMethod]
        public void EnumerateObjects_RecursesIntoNestedSegments()
        {
            var root = new TestStoryboardLayer("root");
            var sprite = root.CreateSprite("sprite.png", OsbOrigin.Centre);
            var child = (TestStoryboardLayer)root.CreateSegment("child");
            var sample = child.CreateSample("hit.wav", 100);
            var grandChild = (TestStoryboardLayer)child.CreateSegment("grandchild");
            var animation = grandChild.CreateAnimation("anim.png", 10, 50, OsbLoopType.LoopForever, OsbOrigin.Centre);

            var objects = root.EnumerateObjects(includeNestedSegments: true).ToList();

            CollectionAssert.Contains(objects, sprite);
            CollectionAssert.Contains(objects, child);
            CollectionAssert.Contains(objects, sample);
            CollectionAssert.Contains(objects, grandChild);
            CollectionAssert.Contains(objects, animation);

            var animations = root.ObjectsOfType<OsbAnimation>(includeNestedSegments: true).ToList();
            Assert.AreEqual(1, animations.Count);
            Assert.AreSame(animation, animations[0]);
        }

        [TestMethod]
        public void EnumerateObjects_SnapshotPreventsConcurrentMutationVisibility()
        {
            var root = new TestStoryboardLayer("root");
            root.CreateSprite("sprite.png", OsbOrigin.Centre);
            root.CreateSample("hit.wav", 0);

            var liveEnumerator = root.EnumerateObjects().GetEnumerator();
            Assert.IsTrue(liveEnumerator.MoveNext());
            var snapshotEnumerator = root.EnumerateObjects(snapshot: true).GetEnumerator();
            Assert.IsTrue(snapshotEnumerator.MoveNext());

            var lateSample = root.CreateSample("late.wav", 100);

            var liveObjects = new List<StoryboardObject> { liveEnumerator.Current };
            while (liveEnumerator.MoveNext())
                liveObjects.Add(liveEnumerator.Current);
            Assert.IsTrue(liveObjects.Contains(lateSample), "Live enumeration should observe later additions.");

            var snapshotObjects = new List<StoryboardObject> { snapshotEnumerator.Current };
            while (snapshotEnumerator.MoveNext())
                snapshotObjects.Add(snapshotEnumerator.Current);
            CollectionAssert.DoesNotContain(snapshotObjects, lateSample, "Snapshot enumeration must remain stable.");
        }

        [TestMethod]
        public void TypedEnumeratorsExposeShallowCollections()
        {
            var root = new TestStoryboardLayer("root");
            var sprite = root.CreateSprite("sprite.png", OsbOrigin.Centre);
            root.CreateSample("hit.wav", 0);

            var sprites = root.Sprites.ToList();
            Assert.AreEqual(1, sprites.Count);
            Assert.AreSame(sprite, sprites[0]);
        }
    }

    [TestClass]
    public class LayerManagerSharedLayerTests
    {
        [TestMethod]
        public void ReplaceList_ReusesExistingSharedLayer()
        {
            var manager = new LayerManager();
            var shared = new EditorStoryboardLayer("shared", effect: null);
            manager.Add(shared);

            var oldLayers = new List<EditorStoryboardLayer> { shared };
            var newLayers = new List<EditorStoryboardLayer> { shared };
            manager.Replace(oldLayers, newLayers);

            Assert.AreEqual(1, manager.LayersCount);
            Assert.AreSame(shared, manager.Layers.Single());
        }

        [TestMethod]
        public void ReplacePlaceholderWithMultipleLayers()
        {
            var manager = new LayerManager();
            var placeholder = new EditorStoryboardLayer(string.Empty, effect: null);
            manager.Add(placeholder);

            var first = new EditorStoryboardLayer("A", effect: null);
            var second = new EditorStoryboardLayer("B", effect: null);

            manager.Replace(placeholder, new List<EditorStoryboardLayer> { first, second });

            CollectionAssert.AreEqual(new[] { first, second }, manager.Layers.ToArray());
        }

        [TestMethod]
        public void ReplaceList_SwapsLayerWithMatchingIdentifier()
        {
            var manager = new LayerManager();
            var original = new EditorStoryboardLayer("layer", effect: null);
            manager.Add(original);

            var replacement = new EditorStoryboardLayer("layer", effect: null);
            manager.Replace(new List<EditorStoryboardLayer> { original }, new List<EditorStoryboardLayer> { replacement });

            Assert.AreSame(replacement, manager.Layers.Single());
            Assert.AreEqual("layer", manager.Layers.Single().Identifier);
        }
    }

    internal sealed class TestGeneratorContext : GeneratorContext
    {
        private readonly Func<string, StoryboardLayer> factory;
        private readonly Dictionary<string, StoryboardLayer> layers = new Dictionary<string, StoryboardLayer>(StringComparer.Ordinal);
        private StoryboardLayer unnamedLayer;
        private bool multithreaded;

        public TestGeneratorContext(Func<string, StoryboardLayer> factory)
        {
            this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
        }

        public List<StoryboardLayer> CreatedLayers { get; } = new List<StoryboardLayer>();
        public List<StoryboardLayer> AccessedLayers { get; } = new List<StoryboardLayer>();

        public int LocalFactoryCalls { get; private set; }
            = 0;

        public override string ProjectPath => "project";
        public override string ProjectAssetPath => "assets";
        public override string MapsetPath => "mapset";

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
                if (unnamedLayer == null)
                {
                    unnamedLayer = createLayer(null);
                }
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
        {
            CreatedLayers.Add(layer);
        }

        protected override void OnLayerAccessed(StoryboardLayer layer)
        {
            AccessedLayers.Add(layer);
        }

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
    }

    internal sealed class TestStoryboardLayer : StoryboardLayer
    {
        private readonly List<StoryboardObject> objects = new List<StoryboardObject>();
        private readonly Dictionary<string, TestStoryboardLayer> segments = new Dictionary<string, TestStoryboardLayer>(StringComparer.Ordinal);

        public TestStoryboardLayer(string identifier)
            : base(identifier)
        {
        }

        public override double StartTime => 0;
        public override double EndTime => 0;

        public override Vector2 Origin { get; set; }
            = Vector2.Zero;
        public override Vector2 Position { get; set; }
            = Vector2.Zero;
        public override double Rotation { get; set; }
            = 0;
        public override double Scale { get; set; }
            = 1;
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
