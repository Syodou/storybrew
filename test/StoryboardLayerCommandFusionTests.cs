using Microsoft.VisualStudio.TestTools.UnitTesting;
using StorybrewCommon.Storyboarding;
using StorybrewCommon.Storyboarding.CommandValues;
using StorybrewCommon.Storyboarding.Commands;
using StorybrewEditor.Storyboarding;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Storybrew.Tests
{
    [TestClass]
    public class StoryboardLayerCommandFusionTests
    {
        [TestMethod]
        public void MergeCommands_FusesOverlappingMoveCommands()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/overlap.png", OsbOrigin.Centre);

            sprite.Move(0, 1000, 0, 0, 100, 100);
            sprite.Move(OsbEasing.Out, 900, 1500, new CommandPosition(100, 100), new CommandPosition(200, 200));

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var moveCommands = sprite.Commands.OfType<MoveCommand>().ToList();
            Assert.AreEqual(1, moveCommands.Count, "Overlapping moves should fuse into a single command.");
            Assert.AreEqual(0, moveCommands[0].StartTime, "Fused command must start at earliest start time.");
            Assert.AreEqual(1500, moveCommands[0].EndTime, "Fused command must end at latest end time.");
            Assert.AreEqual(new CommandPosition(0, 0), moveCommands[0].StartValue, "Start value should come from earliest command.");
            Assert.AreEqual(new CommandPosition(200, 200), moveCommands[0].EndValue, "End value should come from the latest command.");
        }

        [TestMethod]
        public void MergeWithDifferentEasings()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/easing.png", OsbOrigin.Centre);

            sprite.Move(OsbEasing.InOutSine, 0, 1000, new CommandPosition(0, 0), new CommandPosition(50, 50));
            sprite.Move(OsbEasing.OutCirc, 800, 1600, new CommandPosition(50, 50), new CommandPosition(200, 200));

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var moveCommands = sprite.Commands.OfType<MoveCommand>().ToList();
            Assert.AreEqual(1, moveCommands.Count, "Commands with different easing should still merge.");
            Assert.AreEqual(OsbEasing.InOutSine, moveCommands[0].Easing, "Merged command must use the earliest easing.");
        }

        [TestMethod]
        public void EasingConflictLogOnly()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/easinglog.png", OsbOrigin.Centre);

            sprite.Move(OsbEasing.In, 0, 1000, new CommandPosition(0, 0), new CommandPosition(10, 10));
            sprite.Move(OsbEasing.Out, 500, 1200, new CommandPosition(10, 10), new CommandPosition(20, 20));

            using var writer = new StringWriter();
            var listener = new TextWriterTraceListener(writer);
            Debug.Listeners.Add(listener);
            try
            {
                layer.WriteOsb(TextWriter.Null, ExportSettings.Default);
                listener.Flush();

                var output = writer.ToString();
#if DEBUG
                StringAssert.Contains(output, "Easing conflict resolved", "Easing mismatch should be logged in debug builds.");
#endif

                var moves = sprite.Commands.OfType<MoveCommand>().ToList();
                Assert.AreEqual(1, moves.Count, "Moves should still merge despite easing differences.");
                Assert.AreEqual(OsbEasing.In, moves[0].Easing, "Earliest easing must win.");
            }
            finally
            {
                Debug.Listeners.Remove(listener);
            }
        }

        [TestMethod]
        public void NonContiguousMoveSegmentsRemainSeparated()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/noncontiguous.png", OsbOrigin.Centre);

            sprite.Move(0, 1000, 0, 0, 100, 100);
            sprite.Move(1200, 2000, 200, 200, 300, 300);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var moveCommands = sprite.Commands.OfType<MoveCommand>().ToList();
            Assert.AreEqual(2, moveCommands.Count, "Commands separated by gaps must not merge.");
            Assert.IsTrue(moveCommands[0].EndTime < moveCommands[1].StartTime, "Merged output must preserve gap.");
        }

        [TestMethod]
        public void EdgeTouchMerge()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/edgetouch.png", OsbOrigin.Centre);

            sprite.Move(0, 1000, 0, 0, 100, 100);
            sprite.Move(1000, 2000, 100, 100, 200, 200);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var moves = sprite.Commands.OfType<MoveCommand>().ToList();
            Assert.AreEqual(1, moves.Count, "Touching segments should merge into a single move.");
            Assert.AreEqual(0, moves[0].StartTime);
            Assert.AreEqual(2000, moves[0].EndTime);
        }

        [TestMethod]
        public void MultiSegmentCollapse()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/multisegment.png", OsbOrigin.Centre);

            sprite.Move(0, 800, 0, 0, 50, 50);
            sprite.Move(600, 1400, 50, 50, 120, 120);
            sprite.Move(1300, 2000, 120, 120, 200, 200);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var moveCommands = sprite.Commands.OfType<MoveCommand>().ToList();
            Assert.AreEqual(1, moveCommands.Count, "Overlapping segments should collapse into a single command.");
            Assert.AreEqual(0, moveCommands[0].StartTime);
            Assert.AreEqual(2000, moveCommands[0].EndTime);
        }

        [TestMethod]
        public void ZeroDurationSafety()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/zeroduration.png", OsbOrigin.Centre);

            sprite.Fade(1000, 1000, 0, 1);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var fades = sprite.Commands.OfType<FadeCommand>().ToList();
            Assert.AreEqual(1, fades.Count, "Zero-duration command must persist as-is.");
            Assert.AreEqual(1000, fades[0].StartTime);
            Assert.AreEqual(1000, fades[0].EndTime);
        }

        [TestMethod]
        public void CommandTypeIsolation()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/mixedtypes.png", OsbOrigin.Centre);

            sprite.Move(0, 1000, 0, 0, 100, 100);
            sprite.Fade(0, 1000, 0, 1);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var moveCommands = sprite.Commands.OfType<MoveCommand>().Count();
            var fadeCommands = sprite.Commands.OfType<FadeCommand>().Count();

            Assert.AreEqual(1, moveCommands, "Move commands should remain after mixing with fade commands.");
            Assert.AreEqual(1, fadeCommands, "Fade commands must not merge with move commands.");
        }

        [TestMethod]
        public void MixedObjectsNeverMerge()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var first = layer.CreateSprite("sb/first.png", OsbOrigin.Centre);
            var second = layer.CreateSprite("sb/second.png", OsbOrigin.Centre);

            first.Move(0, 1000, 0, 0, 100, 100);
            first.Move(900, 1500, 100, 100, 200, 200);

            second.Move(0, 1000, 0, 0, -100, -100);
            second.Move(900, 1500, -100, -100, -200, -200);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            Assert.AreEqual(1, first.Commands.OfType<MoveCommand>().Count(), "First sprite commands should fuse independently.");
            Assert.AreEqual(1, second.Commands.OfType<MoveCommand>().Count(), "Second sprite commands should fuse independently.");
        }

        [TestMethod]
        public void MultiGroupIsolation()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var first = layer.CreateSprite("sb/isolation_first.png", OsbOrigin.Centre);
            var second = layer.CreateSprite("sb/isolation_second.png", OsbOrigin.Centre);

            first.Move(0, 500, 0, 0, 50, 50);
            first.Move(400, 900, 50, 50, 100, 100);

            second.Fade(0, 1000, 0, 1);
            second.Fade(1000, 2000, 1, 0);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var firstMoves = first.Commands.OfType<MoveCommand>().ToList();
            var secondFades = second.Commands.OfType<FadeCommand>().ToList();

            Assert.AreEqual(1, firstMoves.Count, "First sprite moves should merge.");
            Assert.AreEqual(2, secondFades.Count, "Second sprite fades should remain separate.");
            Assert.IsTrue(secondFades[0].StartTime <= secondFades[1].StartTime, "Secondary sprite command order must stay stable.");
        }

        [TestMethod]
        public void DeterministicOrdering()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/order.png", OsbOrigin.Centre);

            var commands = new List<ICommand>
            {
                new MoveCommand(OsbEasing.None, 600, 800, new CommandPosition(0, 0), new CommandPosition(10, 10)),
                new MoveCommand(OsbEasing.None, 200, 400, new CommandPosition(10, 10), new CommandPosition(20, 20)),
                new MoveCommand(OsbEasing.None, 400, 600, new CommandPosition(20, 20), new CommandPosition(30, 30)),
                new MoveCommand(OsbEasing.None, 0, 100, new CommandPosition(30, 30), new CommandPosition(40, 40))
            };

            var insertionOrder = new[] { 2, 0, 3, 1 };
            foreach (var index in insertionOrder)
                sprite.AddCommand(commands[index]);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var ordered = sprite.Commands.OfType<MoveCommand>().Select(c => c.StartTime).ToList();
            var sorted = ordered.OrderBy(t => t).ToList();

            CollectionAssert.AreEqual(sorted, ordered, "Command fusion must produce a deterministic ordering.");
        }

        [TestMethod]
        public void NoRegressionOnUnrelatedEffects()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/unrelated.png", OsbOrigin.Centre);

            sprite.Rotate(0, 1000, 0, Math.PI);
            sprite.FlipH(500);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            Assert.AreEqual(1, sprite.Commands.OfType<RotateCommand>().Count(), "Rotate commands must remain untouched.");
            Assert.AreEqual(1, sprite.Commands.OfType<ParameterCommand>().Count(), "Parameter commands should remain intact.");
        }

        [TestMethod]
        public void FuseIdempotency()
        {
            var coordinator = new StoryboardLayerCommandCoordinator();
            var commands = new ICommand[]
            {
                new MoveCommand(OsbEasing.None, 0, 400, new CommandPosition(0, 0), new CommandPosition(10, 10)),
                new MoveCommand(OsbEasing.InOutQuad, 300, 800, new CommandPosition(10, 10), new CommandPosition(40, 40)),
                new MoveCommand(OsbEasing.None, 800, 1200, new CommandPosition(40, 40), new CommandPosition(80, 80))
            };

            var fused = coordinator.FuseCommands(commands);
            var reFused = coordinator.FuseCommands(fused);

            Assert.AreEqual(fused.Count, reFused.Count, "Fusion should be idempotent.");
            for (var i = 0; i < fused.Count; i++)
                AssertMoveEquivalent((MoveCommand)fused[i], (MoveCommand)reFused[i], $"Mismatch at index {i}");
        }

        [TestMethod]
        public void RandomInputShuffle()
        {
            var coordinator = new StoryboardLayerCommandCoordinator();
            var baseCommands = new List<ICommand>
            {
                new MoveCommand(OsbEasing.None, 0, 200, new CommandPosition(0, 0), new CommandPosition(5, 5)),
                new MoveCommand(OsbEasing.In, 150, 350, new CommandPosition(5, 5), new CommandPosition(15, 15)),
                new MoveCommand(OsbEasing.Out, 300, 500, new CommandPosition(15, 15), new CommandPosition(30, 30)),
                new MoveCommand(OsbEasing.None, 500, 700, new CommandPosition(30, 30), new CommandPosition(60, 60))
            };

            var expected = coordinator.FuseCommands(baseCommands);
            var rng = new Random(12345);

            for (var iteration = 0; iteration < 8; iteration++)
            {
                var shuffled = baseCommands.OrderBy(_ => rng.Next()).ToList();
                var fused = coordinator.FuseCommands(shuffled);

                Assert.AreEqual(expected.Count, fused.Count, "Fusion should produce a consistent count regardless of input order.");
                for (var i = 0; i < fused.Count; i++)
                    AssertMoveEquivalent((MoveCommand)expected[i], (MoveCommand)fused[i], $"Mismatch in shuffled iteration {iteration} index {i}");
            }
        }

        private static void AssertMoveEquivalent(MoveCommand expected, MoveCommand actual, string message)
        {
            Assert.AreEqual(expected.StartTime, actual.StartTime, message + " (start)");
            Assert.AreEqual(expected.EndTime, actual.EndTime, message + " (end)");
            Assert.AreEqual(expected.StartValue, actual.StartValue, message + " (start value)");
            Assert.AreEqual(expected.EndValue, actual.EndValue, message + " (end value)");
            Assert.AreEqual(expected.Easing, actual.Easing, message + " (easing)");
        }
    }
}
