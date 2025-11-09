using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenTK;
using Storybrew.Tests.Helpers;
using StorybrewCommon.Storyboarding;
using System.Collections.Generic;
using System.Linq;

namespace Storybrew.Tests
{
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
}
