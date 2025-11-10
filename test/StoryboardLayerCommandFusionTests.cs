using Microsoft.VisualStudio.TestTools.UnitTesting;
using StorybrewCommon.Storyboarding;
using StorybrewCommon.Storyboarding.CommandValues;
using StorybrewCommon.Storyboarding.Commands;
using StorybrewEditor.Storyboarding;
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
            Assert.AreEqual(0, moveCommands[0].StartTime, "Fused command must start at the earliest start time.");
            Assert.AreEqual(1500, moveCommands[0].EndTime, "Fused command must end at the latest end time.");
            Assert.AreEqual(new CommandPosition(0, 0), moveCommands[0].StartValue, "Fused command must preserve the earliest start value.");
            Assert.AreEqual(new CommandPosition(200, 200), moveCommands[0].EndValue, "Fused command must use the final end value.");
            Assert.AreEqual(OsbEasing.None, moveCommands[0].Easing, "Fused command must keep the earliest easing.");
        }

        [TestMethod]
        public void MergeCommands_PreservesNonOverlappingCommands()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/nonoverlap.png", OsbOrigin.Centre);

            sprite.Move(0, 1000, 0, 0, 100, 100);
            sprite.Move(1100, 1500, 200, 200, 300, 300);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var moveCommands = sprite.Commands.OfType<MoveCommand>().ToList();
            Assert.AreEqual(2, moveCommands.Count, "Non-overlapping moves must remain separate.");
            Assert.AreEqual(0, moveCommands[0].StartTime);
            Assert.AreEqual(1100, moveCommands[1].StartTime);
        }

        [TestMethod]
        public void MergeCommands_DoesNotMixDifferentCommandTypes()
        {
            var layer = new EditorStoryboardLayer("layer", effect: null);
            var sprite = layer.CreateSprite("sb/mixed.png", OsbOrigin.Centre);

            sprite.Move(0, 1000, 0, 0, 100, 100);
            sprite.Fade(0, 1000, 0, 1);

            layer.WriteOsb(TextWriter.Null, ExportSettings.Default);

            var moveCommands = sprite.Commands.OfType<MoveCommand>().ToList();
            var fadeCommands = sprite.Commands.OfType<FadeCommand>().ToList();

            Assert.AreEqual(1, moveCommands.Count, "Move commands should remain after mixing with fade.");
            Assert.AreEqual(1, fadeCommands.Count, "Fade commands should not merge with move commands.");
        }

        [TestMethod]
        public void MergeCommands_DoesNotMergeAcrossObjects()
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
    }
}
