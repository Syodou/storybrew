using OpenTK;
using System;
using System.Collections.Generic;

namespace StorybrewCommon.Storyboarding
{
    public abstract class StoryboardSegment : StoryboardObject
    {
        public abstract string Identifier { get; }

        public abstract Vector2 Origin { get; set; }
        public abstract Vector2 Position { get; set; }
        public abstract double Rotation { get; set; }
        public double RotationDegrees
        {
            get => MathHelper.RadiansToDegrees(Rotation);
            set => Rotation = MathHelper.DegreesToRadians(value);
        }
        public abstract double Scale { get; set; }

        public abstract bool ReverseDepth { get; set; }

        /// <summary>
        /// Provides a read-only view of the storyboard objects that belong directly to this segment.
        /// The collection reflects new additions while preventing external modifications to the internal list.
        /// </summary>
        public virtual IReadOnlyList<StoryboardObject> Objects => Array.Empty<StoryboardObject>();

        /// <summary>
        /// Enumerates the objects in this segment that match the requested storyboard object type.
        /// </summary>
        public IEnumerable<TStoryboardObject> ObjectsOfType<TStoryboardObject>() where TStoryboardObject : StoryboardObject
        {
            foreach (var storyboardObject in Objects)
                if (storyboardObject is TStoryboardObject typedObject)
                    yield return typedObject;
        }

        public abstract IEnumerable<StoryboardSegment> NamedSegments { get; }
        public abstract StoryboardSegment CreateSegment(string identifier = null);
        public abstract StoryboardSegment GetSegment(string identifier);

        public abstract OsbSprite CreateSprite(string path, OsbOrigin origin, Vector2 initialPosition);
        public abstract OsbSprite CreateSprite(string path, OsbOrigin origin = OsbOrigin.Centre);

        public abstract OsbAnimation CreateAnimation(string path, int frameCount, double frameDelay, OsbLoopType loopType, OsbOrigin origin, Vector2 initialPosition);
        public abstract OsbAnimation CreateAnimation(string path, int frameCount, double frameDelay, OsbLoopType loopType, OsbOrigin origin = OsbOrigin.Centre);

        public abstract OsbSample CreateSample(string path, double time, double volume = 100);

        public abstract void Discard(StoryboardObject storyboardObject);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IReadOnlyList<StoryboardObject> snapshotList(IReadOnlyList<StoryboardObject> source)
        {
            if (source == null || source.Count == 0)
                return Array.Empty<StoryboardObject>();

            var copy = new StoryboardObject[source.Count];
            for (var i = 0; i < copy.Length; i++)
                copy[i] = source[i];
            return copy;
        }
    }
}
