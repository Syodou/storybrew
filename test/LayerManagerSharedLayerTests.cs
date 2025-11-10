using Microsoft.VisualStudio.TestTools.UnitTesting;
using StorybrewEditor.Storyboarding;
using System.Collections.Generic;
using System.Linq;

namespace Storybrew.Tests
{
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
}
