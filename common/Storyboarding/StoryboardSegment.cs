using OpenTK;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
        /// Provides a live, read-only view of the storyboard objects that belong directly to this segment.
        /// The collection is never null and reflects new additions while preventing external modifications
        /// to the internal list.
        /// </summary>
        public virtual IReadOnlyList<StoryboardObject> Objects => Array.Empty<StoryboardObject>();

        /// <summary>
        /// Enumerates storyboard objects that belong to this segment using optional recursion and snapshotting.
        /// </summary>
        /// <param name="includeNestedSegments">
        /// When true, nested <see cref="StoryboardSegment"/> instances are expanded recursively.
        /// </param>
        /// <param name="snapshot">
        /// When true, each level creates a shallow copy before enumeration. This avoids invalid enumerators
        /// when scripts mutate the collection during iteration while still yielding live object references.
        /// </param>
        public IEnumerable<StoryboardObject> EnumerateObjects(bool includeNestedSegments = false, bool snapshot = false)
        {
            var source = Objects;
            if (source == null || source.Count == 0)
                yield break;

            var objects = snapshot ? snapshotList(source) : source;
            for (var i = 0; i < objects.Count; i++)
            {
                var storyboardObject = objects[i];
                yield return storyboardObject;

                if (!includeNestedSegments || storyboardObject is not StoryboardSegment segment)
                    continue;

                foreach (var nested in segment.EnumerateObjects(true, snapshot))
                    yield return nested;
            }
        }

        /// <summary>
        /// Enumerates the objects in this segment that match the requested storyboard object type.
        /// </summary>
        public IEnumerable<TStoryboardObject> ObjectsOfType<TStoryboardObject>(bool includeNestedSegments = false, bool snapshot = false)
            where TStoryboardObject : StoryboardObject
        {
            foreach (var storyboardObject in EnumerateObjects(includeNestedSegments, snapshot))
                if (storyboardObject is TStoryboardObject typedObject)
                    yield return typedObject;
        }

        /// <summary>
        /// Enumerates sprite instances attached to this segment. Equivalent to ObjectsOfType&lt;OsbSprite&gt;().
        /// </summary>
        public IEnumerable<OsbSprite> Sprites => ObjectsOfType<OsbSprite>();

        /// <summary>
        /// Enumerates animation instances attached to this segment. Equivalent to ObjectsOfType&lt;OsbAnimation&gt;().
        /// </summary>
        public IEnumerable<OsbAnimation> Animations => ObjectsOfType<OsbAnimation>();

        /// <summary>
        /// Enumerates sample instances attached to this segment. Equivalent to ObjectsOfType&lt;OsbSample&gt;().
        /// </summary>
        public IEnumerable<OsbSample> Samples => ObjectsOfType<OsbSample>();

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
