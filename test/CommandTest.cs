using StorybrewCommon.Storyboarding;
using System.Linq;

namespace Test
{
    [TestClass]
    public class CommandTest
    {
        [TestMethod]
        public void TestBooleanPointCommand()
        {
            var sprite = new OsbSprite();

            sprite.Additive(1000, 1000);

            Assert.IsTrue(sprite.AdditiveAt(0), "before point command");
            Assert.IsTrue(sprite.AdditiveAt(1000), "at point command");
            Assert.IsTrue(sprite.AdditiveAt(2000), "after point command");
        }

        [TestMethod]
        public void TestBooleanCommand()
        {
            var sprite = new OsbSprite();

            sprite.Additive(1000, 2000);

            Assert.IsFalse(sprite.AdditiveAt(0), "before command");
            Assert.IsTrue(sprite.AdditiveAt(1000), "command start");
            Assert.IsTrue(sprite.AdditiveAt(1500), "during");
            Assert.IsTrue(sprite.AdditiveAt(2000), "command end");
            Assert.IsFalse(sprite.AdditiveAt(3000), "after command");
        }

        [TestMethod]
        public void TestBooleanCommandAfterPoint()
        {
            var sprite = new OsbSprite();

            sprite.Additive(1000, 1000);
            sprite.Additive(2000, 3000);

            Assert.IsTrue(sprite.AdditiveAt(0), "before commands");
            Assert.IsTrue(sprite.AdditiveAt(1000), "at point command");
            Assert.IsTrue(sprite.AdditiveAt(1500), "between commands");
            Assert.IsTrue(sprite.AdditiveAt(2000), "command start");
            Assert.IsTrue(sprite.AdditiveAt(2500), "during");
            Assert.IsTrue(sprite.AdditiveAt(3000), "command end");
            Assert.IsFalse(sprite.AdditiveAt(4000), "after commands");
        }

        [TestMethod]
        public void TestBooleanCommandBeforePoint()
        {
            var sprite = new OsbSprite();

            sprite.Additive(1000, 2000);
            sprite.Additive(3000, 3000);

            Assert.IsFalse(sprite.AdditiveAt(0), "before commands");
            Assert.IsTrue(sprite.AdditiveAt(1000), "command start");
            Assert.IsTrue(sprite.AdditiveAt(1500), "during");
            Assert.IsTrue(sprite.AdditiveAt(2000), "command end");
            Assert.IsFalse(sprite.AdditiveAt(2500), "between commands");
            Assert.IsTrue(sprite.AdditiveAt(3000), "at point command");
            Assert.IsTrue(sprite.AdditiveAt(4000), "after commands");
        }

        [TestMethod]
        public void TestBooleanCommandsInLoop()
        {
            const int loopCount = 3;
            const int loopDuration = 300;

            var sprite = new OsbSprite();

            sprite.StartLoopGroup(0, loopCount);
            sprite.FlipH(0, 200);
            sprite.FlipV(100, 300);
            sprite.EndGroup();

            Assert.IsFalse(sprite.FlipHAt(-1000), "before loops, H");
            Assert.IsFalse(sprite.FlipVAt(-1000), "before loops, V");
            Assert.IsFalse(sprite.FlipHAt(loopDuration * loopCount + 1000), "after loops, H");
            Assert.IsFalse(sprite.FlipVAt(loopDuration * loopCount + 1000), "after loops, V");

            var indices = Enumerable.Range(0, loopCount).ToArray();

            CollectionAssert.AreEqual(
                new[] { true, false, false },
                indices.Select(i => (bool)sprite.FlipHAt(i * loopDuration)).ToArray(),
                "loop boundary at H start");

            CollectionAssert.AreEqual(
                new[] { true, true, true },
                indices.Select(i => (bool)sprite.FlipHAt(i * loopDuration + 200)).ToArray(),
                "loop boundary at H end");

            CollectionAssert.AreEqual(
                new[] { true, false, false },
                indices.Select(i => (bool)sprite.FlipVAt(i * loopDuration + 100)).ToArray(),
                "loop boundary at V start");

            CollectionAssert.AreEqual(
                new[] { true, true, true },
                indices.Select(i => (bool)sprite.FlipVAt(i * loopDuration + 300)).ToArray(),
                "loop boundary at V end");

            foreach (var i in indices)
            {
                Assert.IsTrue(sprite.FlipHAt(i * loopDuration + 100), $"loop {i}, during H");
                Assert.IsFalse(sprite.FlipHAt(i * loopDuration + 250), $"loop {i}, after H");

                Assert.IsTrue(sprite.FlipVAt(i * loopDuration + 200), $"loop {i}, during V");
                Assert.IsFalse(sprite.FlipVAt(i * loopDuration + 50), $"loop {i}, before V");
            }
        }

        [TestMethod]
        public void TestBooleanCommandsChannelOverlap()
        {
        }
    }
}