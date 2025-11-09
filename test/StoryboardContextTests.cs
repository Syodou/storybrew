using Microsoft.VisualStudio.TestTools.UnitTesting;
using Storybrew.Tests.Helpers;
using StorybrewCommon.Storyboarding;
using System.Collections.Generic;

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
            using var generatorA = new TestGeneratorContext(id => new TestStoryboardLayer(id));
            using var generatorB = new TestGeneratorContext(id => new TestStoryboardLayer(id));

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
            using var generatorA = new TestGeneratorContext(id => new TestStoryboardLayer(id));
            using var generatorB = new TestGeneratorContext(id => new TestStoryboardLayer(id));

            generatorA.UseSharedContext(context);
            generatorB.UseSharedContext(context);

            generatorA.GetLayer("first");
            generatorA.UseSharedContext(null);

            generatorB.GetLayer("second");

            Assert.AreEqual(1, generatorA.CreatedLayers.Count, "Detached generator should ignore subsequent creations.");
            Assert.AreEqual(2, generatorB.CreatedLayers.Count, "Active generator tracks every creation.");
        }
    }
}
