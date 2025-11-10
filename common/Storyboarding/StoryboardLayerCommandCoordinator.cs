using System;
using System.Collections.Generic;

namespace StorybrewCommon.Storyboarding
{
    /// <summary>
    /// Coordinates storyboard object contributions collected from multiple scripts before export.
    /// Maintains per-contributor ordering and deterministically merges command timelines.
    /// </summary>
    public sealed class StoryboardLayerCommandCoordinator
    {
        private static readonly Contributor defaultContributor = new Contributor(Guid.Empty, "Default", int.MaxValue, int.MaxValue);

        private readonly object syncRoot = new object();

        private readonly Dictionary<Guid, Contributor> contributors = new Dictionary<Guid, Contributor>();
        private readonly Dictionary<StoryboardObject, Entry> entriesByObject = new Dictionary<StoryboardObject, Entry>();
        private readonly List<Entry> scratchEntries = new List<Entry>();

        private long nextSequence;
        private int nextContributorOrder;

        public StoryboardLayerCommandCoordinator()
        {
            contributors[defaultContributor.Id] = defaultContributor;
        }

        /// <summary>
        /// Registers a contributor that may add storyboard objects to the layer.
        /// </summary>
        public void RegisterContributor(Guid contributorId, string contributorName, int priority = 0)
        {
            if (contributorId == Guid.Empty)
                return;

            lock (syncRoot)
            {
                if (contributors.ContainsKey(contributorId))
                    return;

                contributors[contributorId] = new Contributor(contributorId, contributorName ?? string.Empty, nextContributorOrder++, priority);
            }
        }

        /// <summary>
        /// Updates the ordering priority for a registered contributor.
        /// </summary>
        public void UpdateContributorPriority(Guid contributorId, int priority)
        {
            if (contributorId == Guid.Empty)
                return;

            lock (syncRoot)
            {
                if (contributors.TryGetValue(contributorId, out var contributor))
                    contributors[contributorId] = contributor.WithPriority(priority);
            }
        }

        /// <summary>
        /// Tracks a storyboard object contribution attributed to a contributor.
        /// </summary>
        public void Track(StoryboardObject storyboardObject, Guid contributorId)
        {
            if (storyboardObject == null)
                return;

            lock (syncRoot)
            {
                ensureContributor(contributorId);

                if (!entriesByObject.TryGetValue(storyboardObject, out var entry))
                {
                    entry = new Entry(storyboardObject, contributorId, nextSequence++);
                    entriesByObject.Add(storyboardObject, entry);
                }
                else entry.ContributorId = contributorId;
            }
        }

        /// <summary>
        /// Stops tracking the storyboard object contribution.
        /// </summary>
        public void Untrack(StoryboardObject storyboardObject)
        {
            if (storyboardObject == null)
                return;

            lock (syncRoot)
                entriesByObject.Remove(storyboardObject);
        }

        /// <summary>
        /// Builds a deterministically ordered list of storyboard objects.
        /// </summary>
        /// <param name="objects">The live list of storyboard objects to evaluate.</param>
        /// <param name="ordered">The ordered list, when reordering is required.</param>
        /// <returns>True when the caller should replace the original ordering with <paramref name="ordered"/>.</returns>
        public bool TryBuildOrdered(IReadOnlyList<StoryboardObject> objects, out List<StoryboardObject> ordered)
        {
            ordered = null;
            if (objects == null || objects.Count == 0)
                return false;

            lock (syncRoot)
            {
                scratchEntries.Clear();
                scratchEntries.Capacity = Math.Max(scratchEntries.Capacity, objects.Count);

                for (var i = 0; i < objects.Count; i++)
                {
                    var storyboardObject = objects[i];

                    if (!entriesByObject.TryGetValue(storyboardObject, out var entry))
                    {
                        entry = new Entry(storyboardObject, defaultContributor.Id, nextSequence++);
                        entriesByObject.Add(storyboardObject, entry);
                    }

                    if (!contributors.ContainsKey(entry.ContributorId))
                        entry.ContributorId = defaultContributor.Id;

                    entry.RefreshTimings();
                    scratchEntries.Add(entry);
                }

                scratchEntries.Sort(compareEntries);

                var differs = false;
                for (var i = 0; i < scratchEntries.Count; i++)
                {
                    if (!ReferenceEquals(objects[i], scratchEntries[i].StoryboardObject))
                    {
                        differs = true;
                        break;
                    }
                }

                if (!differs)
                {
                    scratchEntries.Clear();
                    return false;
                }

                ordered = new List<StoryboardObject>(scratchEntries.Count);
                foreach (var entry in scratchEntries)
                    ordered.Add(entry.StoryboardObject);

                scratchEntries.Clear();
                return true;
            }
        }

        private int compareEntries(Entry left, Entry right)
        {
            var start = left.StartTime.CompareTo(right.StartTime);
            if (start != 0)
                return start;

            var contributorComparison = compareContributors(left.ContributorId, right.ContributorId);
            if (contributorComparison != 0)
                return contributorComparison;

            var end = left.EndTime.CompareTo(right.EndTime);
            if (end != 0)
                return end;

            return left.Sequence.CompareTo(right.Sequence);
        }

        private int compareContributors(Guid leftId, Guid rightId)
        {
            var left = getContributor(leftId);
            var right = getContributor(rightId);

            var priority = left.Priority.CompareTo(right.Priority);
            if (priority != 0)
                return priority;

            return left.Order.CompareTo(right.Order);
        }

        private Contributor getContributor(Guid contributorId)
            => contributors.TryGetValue(contributorId, out var contributor) ? contributor : defaultContributor;

        private void ensureContributor(Guid contributorId)
        {
            if (contributorId == Guid.Empty || contributors.ContainsKey(contributorId))
                return;

            contributors[contributorId] = new Contributor(contributorId, string.Empty, nextContributorOrder++, 0);
        }

        private static double sanitize(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0d;

            return value;
        }

        private sealed class Entry
        {
            public Entry(StoryboardObject storyboardObject, Guid contributorId, long sequence)
            {
                StoryboardObject = storyboardObject;
                ContributorId = contributorId;
                Sequence = sequence;
            }

            public StoryboardObject StoryboardObject { get; }
            public Guid ContributorId { get; set; }
            public long Sequence { get; }
            public double StartTime { get; private set; }
            public double EndTime { get; private set; }

            public void RefreshTimings()
            {
                if (StoryboardObject is StoryboardSegment segment)
                {
                    computeSegmentBounds(segment, out var start, out var end);
                    StartTime = start;
                    EndTime = end;
                    return;
                }

                StartTime = sanitize(StoryboardObject.StartTime);
                EndTime = sanitize(StoryboardObject.EndTime);
            }

            private static void computeSegmentBounds(StoryboardSegment segment, out double start, out double end)
            {
                start = double.MaxValue;
                end = double.MinValue;

                var objects = segment.Objects;
                if (objects != null)
                {
                    for (var i = 0; i < objects.Count; i++)
                    {
                        var storyboardObject = objects[i];
                        var objectStart = sanitize(storyboardObject.StartTime);
                        var objectEnd = sanitize(storyboardObject.EndTime);

                        if (storyboardObject is StoryboardSegment nested)
                        {
                            computeSegmentBounds(nested, out var nestedStart, out var nestedEnd);
                            objectStart = Math.Min(objectStart, nestedStart);
                            objectEnd = Math.Max(objectEnd, nestedEnd);
                        }

                        start = Math.Min(start, objectStart);
                        end = Math.Max(end, objectEnd);
                    }
                }

                if (start == double.MaxValue)
                    start = sanitize(segment.StartTime);
                if (end == double.MinValue)
                    end = sanitize(segment.EndTime);
            }
        }

        private readonly struct Contributor
        {
            public Contributor(Guid id, string name, int order, int priority)
            {
                Id = id;
                Name = name;
                Order = order;
                Priority = priority;
            }

            public Guid Id { get; }
            public string Name { get; }
            public int Order { get; }
            public int Priority { get; }

            public Contributor WithPriority(int priority)
                => new Contributor(Id, Name, Order, priority);
        }
    }
}
