using StorybrewCommon.Storyboarding;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StorybrewEditor.Storyboarding
{
    internal sealed class LayerCommandAggregator
    {
        private readonly object syncRoot = new object();

        private readonly Dictionary<Guid, Contributor> contributors = new Dictionary<Guid, Contributor>();
        private readonly Dictionary<StoryboardObject, TrackedEntry> entriesByObject = new Dictionary<StoryboardObject, TrackedEntry>();
        private readonly List<TrackedEntry> entries = new List<TrackedEntry>();

        private long sequence;
        private int contributorSequence;

        private static readonly Contributor defaultContributor = new Contributor(Guid.Empty, "Layer", int.MaxValue, int.MaxValue);

        public LayerCommandAggregator()
        {
            contributors[defaultContributor.Id] = defaultContributor;
        }

        public void RegisterContributor(Effect contributor)
        {
            if (contributor == null)
                return;

            lock (syncRoot)
            {
                if (contributors.ContainsKey(contributor.Guid))
                    return;

                contributors[contributor.Guid] = new Contributor(contributor.Guid, contributor.Name, contributorSequence++, 0);
            }
        }

        public void SetContributorPriority(Guid contributorId, int priority)
        {
            lock (syncRoot)
            {
                if (!contributors.TryGetValue(contributorId, out var contributor))
                    return;

                contributors[contributorId] = contributor.WithPriority(priority);
            }
        }

        public void TrackObject(StoryboardObject storyboardObject)
        {
            if (storyboardObject == null)
                return;

            lock (syncRoot)
            {
                if (entriesByObject.ContainsKey(storyboardObject))
                    return;

                var contributorId = resolveContributorId(EditorGeneratorContext.Current);
                var entry = new TrackedEntry(storyboardObject, contributorId, sequence++);
                entriesByObject[storyboardObject] = entry;
                entries.Add(entry);
            }
        }

        public void UntrackObject(StoryboardObject storyboardObject)
        {
            if (storyboardObject == null)
                return;

            lock (syncRoot)
            {
                if (!entriesByObject.TryGetValue(storyboardObject, out var entry))
                    return;

                entriesByObject.Remove(storyboardObject);
                entries.Remove(entry);
            }
        }

        public void ApplyOrdering(EditorStoryboardSegment segment)
        {
            if (segment == null)
                return;

            lock (syncRoot)
            {
                var currentObjects = segment.RawObjects;
                if (currentObjects.Count == 0)
                    return;

                var relevantEntries = new List<TrackedEntry>(currentObjects.Count);
                var contributorSet = new HashSet<Guid>();
                var requiresReorder = false;

                foreach (var storyboardObject in currentObjects)
                {
                    if (!entriesByObject.TryGetValue(storyboardObject, out var entry))
                    {
                        var contributorId = resolveContributorId(null);
                        entry = new TrackedEntry(storyboardObject, contributorId, sequence++);
                        entriesByObject[storyboardObject] = entry;
                        entries.Add(entry);
                        requiresReorder = true;
                    }

                    entry.UpdatePrimaryTime(sanitize(storyboardObject.StartTime));
                    relevantEntries.Add(entry);
                    contributorSet.Add(entry.ContributorId);
                }

                if (contributorSet.Count <= 1 && !requiresReorder)
                    return;

                var contributorCache = contributors.ToDictionary(pair => pair.Key, pair => pair.Value);

                var ordered = relevantEntries
                    .OrderBy(entry => entry.PrimaryTime)
                    .ThenBy(entry => getContributor(contributorCache, entry.ContributorId).Priority)
                    .ThenBy(entry => getContributor(contributorCache, entry.ContributorId).Order)
                    .ThenBy(entry => entry.Sequence)
                    .Select(entry => entry.Object)
                    .ToList();

                if (!requiresReorder)
                {
                    for (var i = 0; i < currentObjects.Count; i++)
                    {
                        if (!ReferenceEquals(currentObjects[i], ordered[i]))
                        {
                            requiresReorder = true;
                            break;
                        }
                    }
                }

                if (requiresReorder)
                    segment.ReorderObjects(ordered);
            }
        }

        private static double sanitize(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0;

            return value;
        }

        private Guid resolveContributorId(EditorGeneratorContext context)
        {
            if (context?.Effect == null)
                return defaultContributor.Id;

            if (!contributors.ContainsKey(context.Effect.Guid))
                contributors[context.Effect.Guid] = new Contributor(context.Effect.Guid, context.Effect.Name, contributorSequence++, 0);

            return context.Effect.Guid;
        }

        private static Contributor getContributor(Dictionary<Guid, Contributor> cache, Guid id)
            => cache.TryGetValue(id, out var contributor) ? contributor : defaultContributor;

        private sealed class TrackedEntry
        {
            public TrackedEntry(StoryboardObject storyboardObject, Guid contributorId, long sequence)
            {
                Object = storyboardObject;
                ContributorId = contributorId;
                Sequence = sequence;
            }

            public StoryboardObject Object { get; }
            public Guid ContributorId { get; }
            public long Sequence { get; }
            public double PrimaryTime { get; private set; }

            public void UpdatePrimaryTime(double value)
            {
                PrimaryTime = value;
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
