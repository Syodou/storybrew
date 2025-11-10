using StorybrewCommon.Storyboarding.Commands;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Diagnostics;

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
        private readonly SpriteStateTracker spriteStateTracker = new SpriteStateTracker();
        private readonly List<CommandFusionResult> fusionResults = new List<CommandFusionResult>();

        private const double MergeTolerance = 0.0001d;

        private static readonly MethodInfo MemberwiseCloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

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

        public IReadOnlyList<CommandFusionResult> MergeCommands(IReadOnlyList<StoryboardObject> objects)
        {
            if (objects == null || objects.Count == 0)
                return Array.Empty<CommandFusionResult>();

            fusionResults.Clear();

            lock (syncRoot)
            {
                for (var i = 0; i < objects.Count; i++)
                    mergeCommandsRecursive(objects[i]);
            }

            if (fusionResults.Count == 0)
                return Array.Empty<CommandFusionResult>();

            var results = fusionResults.ToArray();
            fusionResults.Clear();
            return results;
        }

        public IReadOnlyList<ICommand> FuseCommands(IEnumerable<ICommand> commands)
        {
            if (commands == null)
                return Array.Empty<ICommand>();

            var enumerated = new List<ICommand>();
            foreach (var command in commands)
            {
                if (command != null)
                    enumerated.Add(command);
            }

            if (enumerated.Count == 0)
                return Array.Empty<ICommand>();

            var outputs = new List<CommandOutput>(enumerated.Count);
            var mergeBuckets = new Dictionary<Type, List<CommandRecord>>();

            for (var i = 0; i < enumerated.Count; i++)
            {
                var command = enumerated[i];

                Debug.Assert(!double.IsNaN(command.StartTime) && !double.IsInfinity(command.StartTime), "Command start time must be finite");
                Debug.Assert(!double.IsNaN(command.EndTime) && !double.IsInfinity(command.EndTime), "Command end time must be finite");

                if (command is CommandGroup group)
                {
                    var clonedGroup = cloneGroup(group);
                    outputs.Add(new CommandOutput(clonedGroup, command.GetType().Name, sanitize(group.StartTime), i));
                    continue;
                }

                var accessor = getAccessor(command.GetType());
                if (!accessor.IsSupported)
                {
                    var clone = accessor.Clone(command) ?? cloneViaMemberwise(command);
                    outputs.Add(new CommandOutput(clone, accessor.TypeKey, sanitize(command.StartTime), i));
                    continue;
                }

                if (!mergeBuckets.TryGetValue(command.GetType(), out var bucket))
                    mergeBuckets[command.GetType()] = bucket = new List<CommandRecord>();

                bucket.Add(new CommandRecord(command, i, accessor));
            }

            foreach (var bucket in mergeBuckets.Values)
            {
                if (bucket.Count == 0)
                    continue;

                var accessor = bucket[0].Accessor;
                bucket.Sort(CommandRecordComparer);

                var group = new CommandFusionGroup(bucket[0]);
                for (var i = 1; i < bucket.Count; i++)
                {
                    var record = bucket[i];
                    if (!group.TryInclude(record))
                    {
                        appendGroup(outputs, accessor, group);
                        group = new CommandFusionGroup(record);
                    }
                }
                appendGroup(outputs, accessor, group);
                bucket.Clear();
            }

            outputs.Sort(CommandOutputComparer);

            var fused = new List<ICommand>(outputs.Count);
            for (var i = 0; i < outputs.Count; i++)
                fused.Add(outputs[i].Command);

            return fused;
        }

        private void appendGroup(List<CommandOutput> outputs, CommandAccessor accessor, CommandFusionGroup group)
        {
            if (group == null || group.Count == 0)
                return;

            if (group.Count == 1)
            {
                var record = group.First;
                var clone = accessor.Clone(record.Command) ?? record.Command;
                outputs.Add(new CommandOutput(clone, accessor.TypeKey, record.StartTime, record.OriginalIndex));
                return;
            }

            var easing = group.First?.Easing ?? OsbEasing.None;
            var fused = accessor.Create(easing, group.StartTime, group.EndTime, group.First.StartValue, group.Last.EndValue);
            if (fused == null)
            {
                foreach (var record in group.Records)
                {
                    var clone = accessor.Clone(record.Command) ?? record.Command;
                    outputs.Add(new CommandOutput(clone, accessor.TypeKey, record.StartTime, record.OriginalIndex));
                }
                return;
            }

            outputs.Add(new CommandOutput(fused, accessor.TypeKey, group.StartTime, group.First.OriginalIndex));
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

            getOrCreateEntry(storyboardObject);

            if (storyboardObject is OsbSprite sprite)
            {
                mergeSpriteCommands(sprite);
                return;
            }

            if (storyboardObject is StoryboardSegment segment)
            {
                var children = segment.Objects;
                if (children == null || children.Count == 0)
                    return;

                for (var i = 0; i < children.Count; i++)
                    mergeCommandsRecursive(children[i]);
            }
        }

        private void mergeSpriteCommands(OsbSprite sprite)
        {
            var existing = spriteStateTracker.GetCommands(sprite);
            if (existing == null || existing.Count == 0)
                return;

            var snapshot = new List<ICommand>(existing);
            var fused = FuseCommands(snapshot);
            var fusedList = fused as List<ICommand> ?? fused.ToList();

            spriteStateTracker.ApplyCommands(sprite, fusedList);

            fusionResults.Add(new CommandFusionResult(sprite, snapshot.Count, fusedList.Count));
        }

        private CommandAccessor getAccessor(Type commandType)
        {
            if (!commandAccessors.TryGetValue(commandType, out var accessor))
                commandAccessors[commandType] = accessor = CommandAccessor.Create(commandType);

            return accessor;
        }

        private ICommand cloneGroup(CommandGroup group)
        {
            switch (group)
            {
                case LoopCommand loop:
                    var loopClone = new LoopCommand(loop.StartTime, loop.LoopCount);
                    foreach (var command in loop.Commands)
                        loopClone.Add(cloneCommand(command));
                    return loopClone;
                case TriggerCommand trigger:
                    var triggerClone = new TriggerCommand(trigger.TriggerName, trigger.StartTime, trigger.EndTime, trigger.Group);
                    foreach (var command in trigger.Commands)
                        triggerClone.Add(cloneCommand(command));
                    return triggerClone;
                default:
                    var clone = (CommandGroup)Activator.CreateInstance(group.GetType());
                    foreach (var command in group.Commands)
                        clone.Add(cloneCommand(command));
                    return clone;
            }
        }

        private ICommand cloneCommand(ICommand command)
        {
            if (command is CommandGroup group)
                return cloneGroup(group);

            var accessor = getAccessor(command.GetType());
            return accessor.Clone(command) ?? cloneViaMemberwise(command);
        }

        private static ICommand cloneViaMemberwise(ICommand command)
            => (ICommand)MemberwiseCloneMethod.Invoke(command, null);

        private Entry getOrCreateEntry(StoryboardObject storyboardObject)
        {
            if (!entriesByObject.TryGetValue(storyboardObject, out var entry))
            {
                entry = new Entry(storyboardObject, defaultContributor.Id, nextSequence++);
                entriesByObject[storyboardObject] = entry;
            }
            return entry;
        }

        private sealed class CommandRecord
        {
            public CommandRecord(ICommand command, int originalIndex, CommandAccessor accessor)
            {
                Command = command;
                OriginalIndex = originalIndex;
                Accessor = accessor;
                StartTime = sanitize(command.StartTime);
                EndTime = sanitize(command.EndTime);
                StartValue = accessor.GetStartValue(command);
                EndValue = accessor.GetEndValue(command);
                Easing = accessor.GetEasing(command);

#if DEBUG
                Debug.Assert(EndTime + MergeTolerance >= StartTime, "Command duration must be non-negative");
#endif
            }

            public ICommand Command { get; }
            public int OriginalIndex { get; }
            public CommandAccessor Accessor { get; }
            public double StartTime { get; }
            public double EndTime { get; }
            public object StartValue { get; }
            public object EndValue { get; }
            public OsbEasing Easing { get; }
        }

        private sealed class CommandFusionGroup
        {
            public CommandFusionGroup(CommandRecord seed)
            {
                records = new List<CommandRecord> { seed };
                StartTime = seed.StartTime;
                EndTime = seed.EndTime;
                First = seed;
                Last = seed;
            }

            private readonly List<CommandRecord> records;
            public IReadOnlyList<CommandRecord> Records => records;
            public double StartTime { get; private set; }
            public double EndTime { get; private set; }
            public CommandRecord First { get; private set; }
            public CommandRecord Last { get; private set; }
            public int Count => records.Count;

            public bool TryInclude(CommandRecord candidate)
            {
                if (candidate.StartTime > EndTime + MergeTolerance)
                    return false;

                records.Add(candidate);
                StartTime = Math.Min(StartTime, candidate.StartTime);
                EndTime = Math.Max(EndTime, candidate.EndTime);

                if (candidate.EndTime > Last.EndTime || (Math.Abs(candidate.EndTime - Last.EndTime) <= MergeTolerance && candidate.OriginalIndex > Last.OriginalIndex))
                    Last = candidate;

                if (candidate.StartTime < First.StartTime || (Math.Abs(candidate.StartTime - First.StartTime) <= MergeTolerance && candidate.OriginalIndex < First.OriginalIndex))
                    First = candidate;

                return true;
            }
        }

        private readonly struct CommandOutput
        {
            public CommandOutput(ICommand command, string typeKey, double startTime, int originalIndex)
            {
                Command = command;
                TypeKey = typeKey;
                StartTime = startTime;
                OriginalIndex = originalIndex;
            }

            public ICommand Command { get; }
            public string TypeKey { get; }
            public double StartTime { get; }
            public int OriginalIndex { get; }
        }

        public sealed class CommandFusionResult
        {
            internal CommandFusionResult(StoryboardObject storyboardObject, int originalCount, int fusedCount)
            {
                StoryboardObject = storyboardObject;
                OriginalCount = originalCount;
                FusedCount = fusedCount;
            }

            public StoryboardObject StoryboardObject { get; }
            public int OriginalCount { get; }
            public int FusedCount { get; }
            public bool HasFusion => FusedCount < OriginalCount;
        }

        private static readonly Comparison<CommandRecord> CommandRecordComparer = (left, right) =>
        {
            var start = left.StartTime.CompareTo(right.StartTime);
            if (start != 0)
                return start;

            return left.OriginalIndex.CompareTo(right.OriginalIndex);
        };

        private static readonly Comparison<CommandOutput> CommandOutputComparer = (left, right) =>
        {
            var typeComparison = string.CompareOrdinal(left.TypeKey, right.TypeKey);
            if (typeComparison != 0)
                return typeComparison;

            var startComparison = left.StartTime.CompareTo(right.StartTime);
            if (startComparison != 0)
                return startComparison;

            return left.OriginalIndex.CompareTo(right.OriginalIndex);
        };

        private sealed class SpriteStateTracker
        {
            private static readonly Type SpriteType = typeof(OsbSprite);

            private readonly FieldInfo commandsField = SpriteType.GetField("commands", BindingFlags.Instance | BindingFlags.NonPublic);
            private readonly FieldInfo displayTimelinesField = SpriteType.GetField("displayTimelines", BindingFlags.Instance | BindingFlags.NonPublic);
            private readonly FieldInfo currentCommandGroupField = SpriteType.GetField("currentCommandGroup", BindingFlags.Instance | BindingFlags.NonPublic);
            private readonly FieldInfo groupEndActionField = SpriteType.GetField("groupEndAction", BindingFlags.Instance | BindingFlags.NonPublic);
            private readonly MethodInfo initializeDisplayTimelinesMethod = SpriteType.GetMethod("initializeDisplayTimelines", BindingFlags.Instance | BindingFlags.NonPublic);
            private readonly MethodInfo addDisplayCommandMethod = SpriteType.GetMethod("addDisplayCommand", BindingFlags.Instance | BindingFlags.NonPublic);
            private readonly MethodInfo startLoopDisplayGroupMethod = SpriteType.GetMethod("startDisplayGroup", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(LoopCommand) }, null);
            private readonly MethodInfo startTriggerDisplayGroupMethod = SpriteType.GetMethod("startDisplayGroup", BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(TriggerCommand) }, null);
            private readonly MethodInfo endDisplayGroupMethod = SpriteType.GetMethod("endDisplayGroup", BindingFlags.Instance | BindingFlags.NonPublic);
            private readonly MethodInfo clearStartEndTimesMethod = SpriteType.GetMethod("clearStartEndTimes", BindingFlags.Instance | BindingFlags.NonPublic);
            private readonly PropertyInfo hasTriggerProperty = SpriteType.GetProperty("HasTrigger", BindingFlags.Instance | BindingFlags.Public);

            public List<ICommand> GetCommands(OsbSprite sprite)
                => commandsField?.GetValue(sprite) as List<ICommand>;

            public void ApplyCommands(OsbSprite sprite, IReadOnlyList<ICommand> commands)
            {
                if (sprite == null)
                    return;

                if (commandsField?.GetValue(sprite) is List<ICommand> target)
                {
                    target.Clear();
                    if (commands != null && commands.Count > 0)
                    {
                        for (var i = 0; i < commands.Count; i++)
                            target.Add(commands[i]);
                    }

                    rebuild(sprite, target);
                    return;
                }

                rebuild(sprite, null);
            }

            private void rebuild(OsbSprite sprite, IList<ICommand> commands)
            {
                if (sprite == null)
                    return;

                if (displayTimelinesField?.GetValue(sprite) is IList displayTimelines)
                    displayTimelines.Clear();

                currentCommandGroupField?.SetValue(sprite, null);
                groupEndActionField?.SetValue(sprite, null);
                initializeDisplayTimelinesMethod?.Invoke(sprite, null);

                var hasTrigger = false;
                if (commands != null)
                {
                    for (var i = 0; i < commands.Count; i++)
                        rebuildCommand(sprite, commands[i], ref hasTrigger);
                }

                if (hasTriggerProperty != null)
                {
                    var setter = hasTriggerProperty.GetSetMethod(true);
                    setter?.Invoke(sprite, new object[] { hasTrigger });
                }

                clearStartEndTimesMethod?.Invoke(sprite, null);
            }

            private void rebuildCommand(OsbSprite sprite, ICommand command, ref bool hasTrigger)
            {
                if (command is TriggerCommand trigger)
                {
                    hasTrigger = true;
                    startTriggerDisplayGroupMethod?.Invoke(sprite, new object[] { trigger });
                    foreach (var nested in trigger.Commands)
                        rebuildCommand(sprite, nested, ref hasTrigger);
                    endDisplayGroupMethod?.Invoke(sprite, null);
                    return;
                }

                if (command is LoopCommand loop)
                {
                    startLoopDisplayGroupMethod?.Invoke(sprite, new object[] { loop });
                    foreach (var nested in loop.Commands)
                        rebuildCommand(sprite, nested, ref hasTrigger);
                    endDisplayGroupMethod?.Invoke(sprite, null);
                    return;
                }

                addDisplayCommandMethod?.Invoke(sprite, new object[] { command });
            }
        }

        private sealed class CommandAccessor
        {
            private readonly PropertyInfo easingProperty;
            private readonly PropertyInfo startValueProperty;
            private readonly PropertyInfo endValueProperty;
            private readonly Func<OsbEasing, double, double, object, object, ICommand> factory;
            private readonly bool requiresMatchingValues;

            private CommandAccessor(Type commandType, PropertyInfo startValueProperty, PropertyInfo endValueProperty, PropertyInfo easingProperty, Func<OsbEasing, double, double, object, object, ICommand> factory, bool requiresMatchingValues)
            {
                CommandType = commandType;
                this.easingProperty = easingProperty;
                this.startValueProperty = startValueProperty;
                this.endValueProperty = endValueProperty;
                this.factory = factory;
                this.requiresMatchingValues = requiresMatchingValues;

                IsSupported = factory != null && startValueProperty != null && endValueProperty != null && easingProperty != null;
                TypeKey = commandType.Name;
            }

            public bool IsSupported { get; }
            public string TypeKey { get; }
            public Type CommandType { get; }

            public static CommandAccessor Create(Type commandType)
            {
                var easingProperty = commandType.GetProperty("Easing", BindingFlags.Instance | BindingFlags.Public);
                var startValueProperty = commandType.GetProperty("StartValue", BindingFlags.Instance | BindingFlags.Public);
                var endValueProperty = commandType.GetProperty("EndValue", BindingFlags.Instance | BindingFlags.Public);

                if (easingProperty == null || startValueProperty == null || endValueProperty == null)
                    return new CommandAccessor(commandType, startValueProperty, endValueProperty, easingProperty, null, false);

                Func<OsbEasing, double, double, object, object, ICommand> factory = null;
                var constructors = commandType.GetConstructors(BindingFlags.Instance | BindingFlags.Public);

                foreach (var constructor in constructors)
                {
                    var parameters = constructor.GetParameters();
                    if (parameters.Length == 5 &&
                        parameters[0].ParameterType == typeof(OsbEasing) &&
                        parameters[1].ParameterType == typeof(double) &&
                        parameters[2].ParameterType == typeof(double) &&
                        parameters[3].ParameterType == startValueProperty.PropertyType &&
                        parameters[4].ParameterType == endValueProperty.PropertyType)
                    {
                        factory = (easing, start, end, startValue, endValue)
                            => (ICommand)constructor.Invoke(new[] { easing, start, end, startValue, endValue });
                        return new CommandAccessor(commandType, startValueProperty, endValueProperty, easingProperty, factory, false);
                    }

                    if (parameters.Length == 4 &&
                        parameters[0].ParameterType == typeof(OsbEasing) &&
                        parameters[1].ParameterType == typeof(double) &&
                        parameters[2].ParameterType == typeof(double) &&
                        parameters[3].ParameterType == startValueProperty.PropertyType)
                    {
                        factory = (easing, start, end, startValue, endValue)
                        {
                            if (!Equals(startValue, endValue))
                                return null;
                            return (ICommand)constructor.Invoke(new[] { easing, start, end, startValue });
                        };
                        return new CommandAccessor(commandType, startValueProperty, endValueProperty, easingProperty, factory, true);
                    }
                }

                return new CommandAccessor(commandType, startValueProperty, endValueProperty, easingProperty, null, false);
            }

            public ICommand Create(OsbEasing easing, double startTime, double endTime, object startValue, object endValue)
            {
                if (!IsSupported)
                    return null;

                if (requiresMatchingValues && !Equals(startValue, endValue))
                    return null;

                return factory(easing, startTime, endTime, startValue, endValue);
            }

            public ICommand Clone(ICommand command)
            {
                if (!IsSupported)
                    return null;

                var easing = GetEasing(command);
                var startTime = sanitize(command.StartTime);
                var endTime = sanitize(command.EndTime);
                var startValue = GetStartValue(command);
                var endValue = GetEndValue(command);
                return Create(easing, startTime, endTime, startValue, endValue);
            }

            public object GetStartValue(ICommand command) => startValueProperty?.GetValue(command);
            public object GetEndValue(ICommand command) => endValueProperty?.GetValue(command);
            public OsbEasing GetEasing(ICommand command) => easingProperty != null ? (OsbEasing)easingProperty.GetValue(command) : OsbEasing.None;
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
