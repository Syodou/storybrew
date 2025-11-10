using StorybrewCommon.Storyboarding.Commands;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
        private readonly Dictionary<Type, CommandAccessor> commandAccessors = new Dictionary<Type, CommandAccessor>();
        private readonly Dictionary<string, List<CommandRecord>> fusionBuckets = new Dictionary<string, List<CommandRecord>>(StringComparer.Ordinal);
        private readonly List<CommandFusionGroup> fusionGroups = new List<CommandFusionGroup>();
        private readonly List<ICommand> commandsToRemove = new List<ICommand>();
        private readonly Dictionary<ICommand, int> originalCommandOrder = new Dictionary<ICommand, int>();

        private const double MergeTolerance = 0.0001d;

        private static readonly Type osbSpriteType = typeof(OsbSprite);
        private static readonly FieldInfo spriteCommandsField = osbSpriteType.GetField("commands", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo spriteDisplayTimelinesField = osbSpriteType.GetField("displayTimelines", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo spriteCurrentCommandGroupField = osbSpriteType.GetField("currentCommandGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo spriteGroupEndActionField = osbSpriteType.GetField("groupEndAction", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo spriteInitializeDisplayTimelinesMethod = osbSpriteType.GetMethod("initializeDisplayTimelines", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo spriteAddDisplayCommandMethod = osbSpriteType.GetMethod("addDisplayCommand", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo spriteStartLoopDisplayGroupMethod = osbSpriteType.GetMethod("startDisplayGroup", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(LoopCommand) }, null);
        private static readonly MethodInfo spriteStartTriggerDisplayGroupMethod = osbSpriteType.GetMethod("startDisplayGroup", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(TriggerCommand) }, null);
        private static readonly MethodInfo spriteEndDisplayGroupMethod = osbSpriteType.GetMethod("endDisplayGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo spriteClearStartEndTimesMethod = osbSpriteType.GetMethod("clearStartEndTimes", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly PropertyInfo spriteHasTriggerProperty = osbSpriteType.GetProperty("HasTrigger", BindingFlags.Instance | BindingFlags.Public);

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

        public void MergeCommands(IReadOnlyList<StoryboardObject> objects)
        {
            if (objects == null || objects.Count == 0)
                return;

            lock (syncRoot)
            {
                for (var i = 0; i < objects.Count; i++)
                    mergeCommandsRecursive(objects[i]);
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

        private void mergeCommandsRecursive(StoryboardObject storyboardObject)
        {
            if (storyboardObject == null)
                return;

            var entry = getOrCreateEntry(storyboardObject);

            if (storyboardObject is OsbSprite sprite)
            {
                mergeSpriteCommands(sprite, entry);
                return;
            }

            if (storyboardObject is StoryboardSegment segment)
            {
                var objects = segment.Objects;
                if (objects == null || objects.Count == 0)
                    return;

                for (var i = 0; i < objects.Count; i++)
                    mergeCommandsRecursive(objects[i]);
            }
        }

        private void mergeSpriteCommands(OsbSprite sprite, Entry entry)
        {
            if (spriteCommandsField?.GetValue(sprite) is not List<ICommand> commands || commands.Count == 0)
                return;

            originalCommandOrder.Clear();
            for (var i = 0; i < commands.Count; i++)
                originalCommandOrder[commands[i]] = i;

            fusionBuckets.Clear();
            fusionGroups.Clear();
            commandsToRemove.Clear();

            for (var i = 0; i < commands.Count; i++)
            {
                var command = commands[i];
                if (command == null || command is CommandGroup)
                    continue;

                var key = buildFusionKey(entry.ContributorId, command);
                if (!fusionBuckets.TryGetValue(key, out var bucket))
                    fusionBuckets[key] = bucket = new List<CommandRecord>();

                bucket.Add(new CommandRecord(command, originalCommandOrder[command]));
            }

            foreach (var bucket in fusionBuckets.Values)
            {
                if (bucket.Count <= 1)
                {
                    bucket.Clear();
                    continue;
                }

                bucket.Sort(CommandRecordComparer);

                var group = new CommandFusionGroup(bucket[0]);
                for (var i = 1; i < bucket.Count; i++)
                {
                    var record = bucket[i];
                    if (!group.Include(record))
                    {
                        fusionGroups.Add(group);
                        group = new CommandFusionGroup(record);
                    }
                }
                fusionGroups.Add(group);
                bucket.Clear();
            }

            fusionBuckets.Clear();

            var mutated = false;

            foreach (var group in fusionGroups)
            {
                if (group.Commands.Count <= 1)
                    continue;

                if (tryFuseGroup(group))
                    mutated = true;
            }

            fusionGroups.Clear();

            if (commandsToRemove.Count > 0)
            {
                foreach (var command in commandsToRemove)
                {
                    commands.Remove(command);
                    originalCommandOrder.Remove(command);
                }
                commandsToRemove.Clear();
                mutated = true;
            }

            if (commands.Count > 1 && reorderCommands(commands, entry))
                mutated = true;

            if (mutated)
                rebuildSpriteState(sprite, commands);

            originalCommandOrder.Clear();
        }

        private bool tryFuseGroup(CommandFusionGroup group)
        {
            var firstCommand = group.Commands[0].Command;
            var accessor = getAccessor(firstCommand.GetType());
            if (!accessor.IsSupported)
                return false;

            var startValue = accessor.GetStartValue(firstCommand);
            var easing = accessor.GetEasing(firstCommand);
            var endValue = accessor.GetEndValue(group.EndValueSource.Command);

            accessor.SetStartTime(firstCommand, group.StartTime);
            accessor.SetEndTime(firstCommand, group.EndTime);
            accessor.SetStartValue(firstCommand, startValue);
            accessor.SetEndValue(firstCommand, endValue);
            accessor.SetEasing(firstCommand, easing);

            for (var i = 1; i < group.Commands.Count; i++)
                commandsToRemove.Add(group.Commands[i].Command);

            return true;
        }

        private bool reorderCommands(List<ICommand> commands, Entry entry)
        {
            var snapshot = new List<ICommand>(commands);
            commands.Sort((left, right) => compareCommandOrdering(left, right, entry));

            for (var i = 0; i < commands.Count; i++)
            {
                if (!ReferenceEquals(snapshot[i], commands[i]))
                    return true;
            }
            return false;
        }

        private int compareCommandOrdering(ICommand left, ICommand right, Entry entry)
        {
            if (ReferenceEquals(left, right))
                return 0;

            var typeComparison = string.CompareOrdinal(getCommandTypeKey(left), getCommandTypeKey(right));
            if (typeComparison != 0)
                return typeComparison;

            var startComparison = sanitize(left.StartTime).CompareTo(sanitize(right.StartTime));
            if (startComparison != 0)
                return startComparison;

            var contributorComparison = compareContributors(entry.ContributorId, entry.ContributorId);
            if (contributorComparison != 0)
                return contributorComparison;

            var leftOrder = originalCommandOrder.TryGetValue(left, out var leftIndex) ? leftIndex : int.MaxValue;
            var rightOrder = originalCommandOrder.TryGetValue(right, out var rightIndex) ? rightIndex : int.MaxValue;
            return leftOrder.CompareTo(rightOrder);
        }

        private CommandAccessor getAccessor(Type commandType)
        {
            if (!commandAccessors.TryGetValue(commandType, out var accessor))
                commandAccessors[commandType] = accessor = CommandAccessor.Create(commandType);

            return accessor;
        }

        private void rebuildSpriteState(OsbSprite sprite, List<ICommand> commands)
        {
            if (spriteDisplayTimelinesField?.GetValue(sprite) is IList displayTimelines)
                displayTimelines.Clear();

            spriteCurrentCommandGroupField?.SetValue(sprite, null);
            spriteGroupEndActionField?.SetValue(sprite, null);
            spriteInitializeDisplayTimelinesMethod?.Invoke(sprite, null);

            var hasTrigger = false;
            for (var i = 0; i < commands.Count; i++)
                rebuildSpriteCommandState(sprite, commands[i], ref hasTrigger);

            if (spriteHasTriggerProperty != null)
            {
                var setter = spriteHasTriggerProperty.GetSetMethod(true);
                setter?.Invoke(sprite, new object[] { hasTrigger });
            }

            spriteClearStartEndTimesMethod?.Invoke(sprite, null);
        }

        private void rebuildSpriteCommandState(OsbSprite sprite, ICommand command, ref bool hasTrigger)
        {
            if (command is TriggerCommand trigger)
            {
                hasTrigger = true;
                spriteStartTriggerDisplayGroupMethod?.Invoke(sprite, new object[] { trigger });
                foreach (var nested in trigger.Commands)
                    rebuildSpriteCommandState(sprite, nested, ref hasTrigger);
                spriteEndDisplayGroupMethod?.Invoke(sprite, null);
                return;
            }

            if (command is LoopCommand loop)
            {
                spriteStartLoopDisplayGroupMethod?.Invoke(sprite, new object[] { loop });
                foreach (var nested in loop.Commands)
                    rebuildSpriteCommandState(sprite, nested, ref hasTrigger);
                spriteEndDisplayGroupMethod?.Invoke(sprite, null);
                return;
            }

            spriteAddDisplayCommandMethod?.Invoke(sprite, new object[] { command });
        }

        private Entry getOrCreateEntry(StoryboardObject storyboardObject)
        {
            if (!entriesByObject.TryGetValue(storyboardObject, out var entry))
            {
                entry = new Entry(storyboardObject, defaultContributor.Id, nextSequence++);
                entriesByObject[storyboardObject] = entry;
            }
            return entry;
        }

        private static string buildFusionKey(Guid contributorId, ICommand command)
            => string.Concat(contributorId.ToString("N"), ":", command.GetType().Name);

        private static readonly Comparison<CommandRecord> CommandRecordComparer = (left, right) =>
        {
            var start = left.StartTime.CompareTo(right.StartTime);
            if (start != 0)
                return start;

            return left.OriginalIndex.CompareTo(right.OriginalIndex);
        };

        private static string getCommandTypeKey(ICommand command)
            => command.GetType().Name;

        private sealed class CommandRecord
        {
            public CommandRecord(ICommand command, int originalIndex)
            {
                Command = command;
                OriginalIndex = originalIndex;
                StartTime = sanitize(command.StartTime);
                EndTime = sanitize(command.EndTime);
            }

            public ICommand Command { get; }
            public int OriginalIndex { get; }
            public double StartTime { get; }
            public double EndTime { get; }
        }

        private sealed class CommandFusionGroup
        {
            private CommandRecord endValueSource;

            public CommandFusionGroup(CommandRecord seed)
            {
                Commands = new List<CommandRecord> { seed };
                StartTime = seed.StartTime;
                EndTime = seed.EndTime;
                endValueSource = seed;
            }

            public List<CommandRecord> Commands { get; }
            public double StartTime { get; private set; }
            public double EndTime { get; private set; }
            public CommandRecord EndValueSource => endValueSource;

            public bool Include(CommandRecord candidate)
            {
                if (candidate.StartTime > EndTime + MergeTolerance)
                    return false;

                Commands.Add(candidate);
                StartTime = Math.Min(StartTime, candidate.StartTime);

                if (candidate.EndTime > EndTime)
                {
                    EndTime = candidate.EndTime;
                    endValueSource = candidate;
                }
                else EndTime = Math.Max(EndTime, candidate.EndTime);

                return true;
            }
        }

        private sealed class CommandAccessor
        {
            private readonly PropertyInfo startTimeProperty;
            private readonly PropertyInfo endTimeProperty;
            private readonly PropertyInfo easingProperty;
            private readonly PropertyInfo startValueProperty;
            private readonly PropertyInfo endValueProperty;

            private CommandAccessor(bool isSupported, PropertyInfo startTimeProperty, PropertyInfo endTimeProperty, PropertyInfo easingProperty, PropertyInfo startValueProperty, PropertyInfo endValueProperty)
            {
                IsSupported = isSupported;
                this.startTimeProperty = startTimeProperty;
                this.endTimeProperty = endTimeProperty;
                this.easingProperty = easingProperty;
                this.startValueProperty = startValueProperty;
                this.endValueProperty = endValueProperty;
            }

            public bool IsSupported { get; }

            public static CommandAccessor Create(Type commandType)
            {
                var startTimeProperty = commandType.GetProperty("StartTime", BindingFlags.Instance | BindingFlags.Public);
                var endTimeProperty = commandType.GetProperty("EndTime", BindingFlags.Instance | BindingFlags.Public);
                var easingProperty = commandType.GetProperty("Easing", BindingFlags.Instance | BindingFlags.Public);
                var startValueProperty = commandType.GetProperty("StartValue", BindingFlags.Instance | BindingFlags.Public);
                var endValueProperty = commandType.GetProperty("EndValue", BindingFlags.Instance | BindingFlags.Public);

                if (startTimeProperty == null || endTimeProperty == null || easingProperty == null || startValueProperty == null || endValueProperty == null)
                    return new CommandAccessor(false, null, null, null, null, null);

                return new CommandAccessor(true, startTimeProperty, endTimeProperty, easingProperty, startValueProperty, endValueProperty);
            }

            public object GetStartValue(ICommand command) => startValueProperty?.GetValue(command);
            public object GetEndValue(ICommand command) => endValueProperty?.GetValue(command);
            public OsbEasing GetEasing(ICommand command) => easingProperty != null ? (OsbEasing)easingProperty.GetValue(command) : OsbEasing.None;

            public void SetStartTime(ICommand command, double value) => startTimeProperty?.SetValue(command, value);
            public void SetEndTime(ICommand command, double value) => endTimeProperty?.SetValue(command, value);
            public void SetStartValue(ICommand command, object value) => startValueProperty?.SetValue(command, value);
            public void SetEndValue(ICommand command, object value) => endValueProperty?.SetValue(command, value);
            public void SetEasing(ICommand command, OsbEasing easing) => easingProperty?.SetValue(command, easing);
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
