using Microsoft.VisualStudio.TestTools.UnitTesting;
using StorybrewCommon.Storyboarding;
using StorybrewCommon.Storyboarding.CommandValues;
using StorybrewCommon.Storyboarding.Commands;
using StorybrewEditor.Storyboarding;
using System;
using System.Collections.Generic;
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
    }
}
