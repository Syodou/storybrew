using StorybrewCommon.Storyboarding.Commands;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Diagnostics;

namespace StorybrewCommon.Storyboarding
{
    /// <summary>
    /// Coordinates storyboard object contributions collected from multiple scripts before export.
    /// Maintains contributor and object ordering, exposes deterministic fusion of overlapping commands,
    /// and rebuilds sprite state without mutating the incoming command collections.
    /// <para>
    /// Fusion always runs once per layer before editor post-processing/export and never leaks editor-only
    /// state back into the runtime objects.
    /// </para>
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
        private readonly List<CommandOutput> fusionOutputs = new List<CommandOutput>();

        private const double MergeTolerance = 0.0001d;

        /// <summary>
        /// Canonical command comparer used by legacy callers that do not provide ordering metadata.
        /// </summary>
        public static IComparer<ICommand> CommandOrderingComparer { get; } = new FusionCommandComparer();

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

        /// <summary>
        /// Replays the sprite state for the supplied storyboard objects and merges supported command types in-place.
        /// Invoked once per layer before editor post-processing/export to ensure the runtime objects stay in sync.
        /// </summary>
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

        /// <summary>
        /// Fuses overlapping or contiguous commands while keeping unrelated commands intact.
        /// Always returns freshly cloned command instances ordered deterministically.
        /// </summary>
        /// <remarks>
        /// Fusion obeys layer object ordering, command type sorting, timeline order, and contributor metadata.
        /// The result never mutates the incoming commands and can safely be re-fused without changing its shape.
        /// Runs in both the editor and export pipeline once per layer immediately before serialization.
        /// Each invocation is scoped to a single storyboard object, preserving cross-object isolation.
        /// </remarks>
        public IReadOnlyList<ICommand> FuseCommands(IEnumerable<ICommand> commands)
            => FuseCommands(commands, FusionOrderingContext.Default);

        internal IReadOnlyList<ICommand> FuseCommands(IEnumerable<ICommand> commands, FusionOrderingContext context)
        {
            // Fusion pipeline summary:
            //  1. Clone every incoming command to keep the call side immutable.
            //  2. Group commands per storyboard object & type (context scopes to a single storyboard object) and sort by time.
            //  3. Merge only overlapping or edge-touching segments with matching command types.
            //  4. Prefer the earliest easing when conflicts arise; unrelated commands keep their order untouched.
            //  5. Produce a deterministic ordering using object order, type, start, end, and contributor metadata.
            if (commands == null)
                return Array.Empty<ICommand>();

            fusionOutputs.Clear();

            var recordsByType = buildCommandRecords(commands, context);
            var anyMerged = false;

            if (recordsByType != null)
            {
                foreach (var typeRecords in recordsByType.Values)
                    processCommandTypeRecords(typeRecords, ref anyMerged);
            }

            if (fusionOutputs.Count == 0)
                return Array.Empty<ICommand>();

            fusionOutputs.Sort(CommandOutputComparer);

#if DEBUG
            validateFusionOutputs();
#endif

#if DEBUG
            if (anyMerged)
                Debug.WriteLine($"[FusionDebug] Fused {fusionOutputs.Count} commands (context object order {context.ObjectOrder}).");
#endif

            var fused = flushFusionOutputs();
            return fused;
        }

        /// <summary>
        /// Clones commands that cannot be merged and groups mergeable commands by their concrete type.
        /// </summary>
        private Dictionary<Type, List<CommandRecord>> buildCommandRecords(IEnumerable<ICommand> commands, FusionOrderingContext context)
        {
            Dictionary<Type, List<CommandRecord>> recordsByType = null;
            var snapshotIndex = 0;

            foreach (var command in commands)
            {
                if (command == null)
                    continue;

#if DEBUG
                Debug.Assert(!double.IsNaN(command.StartTime) && !double.IsInfinity(command.StartTime), "Command start time must be finite");
                Debug.Assert(!double.IsNaN(command.EndTime) && !double.IsInfinity(command.EndTime), "Command end time must be finite");
#endif

                if (command is CommandGroup commandGroup)
                {
                    var clonedGroup = cloneGroup(commandGroup);
                    fusionOutputs.Add(new CommandOutput(clonedGroup, command.GetType().Name, sanitize(commandGroup.StartTime), sanitize(commandGroup.EndTime), context.ObjectOrder, context.ContributorPriority, context.ContributorOrder, context.SnapshotBase + snapshotIndex, wasMerged: false));
                    snapshotIndex++;
                    continue;
                }

                var accessor = getAccessor(command.GetType());
                if (!accessor.IsSupported)
                {
                    var clone = accessor.Clone(command) ?? cloneViaMemberwise(command);
                    fusionOutputs.Add(new CommandOutput(clone, accessor.TypeKey, sanitize(command.StartTime), sanitize(command.EndTime), context.ObjectOrder, context.ContributorPriority, context.ContributorOrder, context.SnapshotBase + snapshotIndex, wasMerged: false));
                    snapshotIndex++;
                    continue;
                }

                recordsByType ??= new Dictionary<Type, List<CommandRecord>>();
                if (!recordsByType.TryGetValue(command.GetType(), out var recordsForType))
                    recordsByType[command.GetType()] = recordsForType = new List<CommandRecord>();

                recordsForType.Add(new CommandRecord(command, snapshotIndex++, accessor, context));
            }

            return recordsByType;
        }

        /// <summary>
        /// Merges a list of sorted command records for a specific type, honouring zero-duration exclusions and merge tolerance.
        /// </summary>
        private void processCommandTypeRecords(List<CommandRecord> recordsForType, ref bool anyMerged)
        {
            if (recordsForType == null || recordsForType.Count == 0)
                return;

            recordsForType.Sort(CommandRecordComparer);

            CommandFusionGroup currentGroup = null;

            void flushCurrentGroup()
            {
                if (currentGroup == null)
                    return;

                appendGroup(fusionOutputs, currentGroup, ref anyMerged);
                currentGroup = null;
            }

            for (var i = 0; i < recordsForType.Count; i++)
            {
                var record = recordsForType[i];

                if (record.IsZeroDuration)
                {
                    flushCurrentGroup();
                    var zeroDurationGroup = new CommandFusionGroup(record, record.Context);
                    appendGroup(fusionOutputs, zeroDurationGroup, ref anyMerged);
#if DEBUG
                    Debug.WriteLine($"[FusionDebug] Preserved zero-duration {record.Accessor.TypeKey} at {record.StartTime}.");
#endif
                    continue;
                }

                if (currentGroup == null)
                {
                    currentGroup = new CommandFusionGroup(record, record.Context);
                    continue;
                }

                if (!currentGroup.TryInclude(record))
                {
                    flushCurrentGroup();
                    currentGroup = new CommandFusionGroup(record, record.Context);
                }
            }

            flushCurrentGroup();
            recordsForType.Clear();
        }

        /// <summary>
        /// Materialises the fused command list and clears intermediate buffers.
        /// </summary>
        private List<ICommand> flushFusionOutputs()
        {
            var fused = new List<ICommand>(fusionOutputs.Count);
            for (var i = 0; i < fusionOutputs.Count; i++)
                fused.Add(fusionOutputs[i].Command);

            fusionOutputs.Clear();
            return fused;
        }

#if DEBUG
        /// <summary>
        /// Validates fused command invariants in DEBUG builds to catch ordering or timing regressions early.
        /// </summary>
        private void validateFusionOutputs()
        {
            for (var i = 0; i < fusionOutputs.Count; i++)
            {
                var output = fusionOutputs[i];
                Debug.Assert(output.StartTime <= output.EndTime + MergeTolerance, "Fused command must have non-negative duration");
                Debug.Assert(!double.IsNaN(output.Command.StartTime) && !double.IsInfinity(output.Command.StartTime), "Fused command start time must be finite");
                Debug.Assert(!double.IsNaN(output.Command.EndTime) && !double.IsInfinity(output.Command.EndTime), "Fused command end time must be finite");
                Debug.Assert(!double.IsNaN(output.StartTime) && !double.IsInfinity(output.StartTime), "Ordering start time must be finite");
                Debug.Assert(!double.IsNaN(output.EndTime) && !double.IsInfinity(output.EndTime), "Ordering end time must be finite");
            }
        }
#endif

        /// <summary>
        /// Emits either the untouched clone of a command group or the merged command built from its members.
        /// </summary>
        private void appendGroup(List<CommandOutput> outputs, CommandFusionGroup group, ref bool anyMerged)
        {
            if (group == null || group.Count == 0)
                return;

            var accessor = group.Accessor;
            if (group.Count == 1)
            {
                var record = group.First;
                var clone = accessor.Clone(record.Command) ?? cloneCommand(record.Command);
                outputs.Add(new CommandOutput(clone, accessor.TypeKey, record.StartTime, record.EndTime, record.Context.ObjectOrder, record.Context.ContributorPriority, record.Context.ContributorOrder, record.SnapshotIndex, false));
                return;
            }

            var easing = group.First.Easing;
            if (group.HasMixedEasing)
            {
#if DEBUG
                Debug.WriteLine($"[FusionDebug] Easing conflict resolved using earliest easing {easing}.");
#endif
            }

            var fused = accessor.Create(easing, group.StartTime, group.EndTime, group.First.StartValue, group.Last.EndValue);
            if (fused == null)
            {
                foreach (var record in group.Records)
                {
                    var clone = accessor.Clone(record.Command) ?? cloneCommand(record.Command);
                    outputs.Add(new CommandOutput(clone, accessor.TypeKey, record.StartTime, record.EndTime, record.Context.ObjectOrder, record.Context.ContributorPriority, record.Context.ContributorOrder, record.SnapshotIndex, false));
                }
                return;
            }

            anyMerged = true;
            outputs.Add(new CommandOutput(fused, accessor.TypeKey, group.StartTime, group.EndTime, group.Context.ObjectOrder, group.Context.ContributorPriority, group.Context.ContributorOrder, group.First.SnapshotIndex, true));
#if DEBUG
            Debug.WriteLine($"[FusionDebug] Merged {group.Count} {accessor.TypeKey} commands spanning {group.StartTime}→{group.EndTime}.");
#endif
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
            var context = buildFusionContext(sprite);
            var fused = FuseCommands(snapshot, context);

            spriteStateTracker.ApplyCommands(sprite, fused);

            fusionResults.Add(new CommandFusionResult(sprite, snapshot.Count, fused.Count));
        }

        private FusionOrderingContext buildFusionContext(StoryboardObject storyboardObject)
        {
            var entry = getOrCreateEntry(storyboardObject);
            var contributor = getContributor(entry.ContributorId);
            var snapshotBase = entry.Sequence << 32;
            return new FusionOrderingContext(entry.Sequence, contributor.Priority, contributor.Order, snapshotBase);
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

        internal readonly struct FusionOrderingContext
        {
            public static FusionOrderingContext Default => new FusionOrderingContext(0, 0, 0, 0);

            public FusionOrderingContext(long objectOrder, int contributorPriority, int contributorOrder, long snapshotBase)
            {
                ObjectOrder = objectOrder;
                ContributorPriority = contributorPriority;
                ContributorOrder = contributorOrder;
                SnapshotBase = snapshotBase;
            }

            public long ObjectOrder { get; }
            public int ContributorPriority { get; }
            public int ContributorOrder { get; }
            public long SnapshotBase { get; }
        }

        /// <summary>
        /// Snapshot of an individual command used during fusion. Captures ordering metadata and values without mutating the source.
        /// </summary>
        private sealed class CommandRecord
        {
            public CommandRecord(ICommand command, int originalIndex, CommandAccessor accessor, FusionOrderingContext context)
            {
                Command = command;
                OriginalIndex = originalIndex;
                Accessor = accessor;
                Context = context;
                StartTime = sanitize(command.StartTime);
                EndTime = sanitize(command.EndTime);
                StartValue = accessor.GetStartValue(command);
                EndValue = accessor.GetEndValue(command);
                Easing = accessor.GetEasing(command);
                SnapshotIndex = context.SnapshotBase + originalIndex;

#if DEBUG
                Debug.Assert(EndTime + MergeTolerance >= StartTime, "Command duration must be non-negative");
#endif
            }

            public ICommand Command { get; }
            public int OriginalIndex { get; }
            public CommandAccessor Accessor { get; }
            public FusionOrderingContext Context { get; }
            public double StartTime { get; }
            public double EndTime { get; }
            public object StartValue { get; }
            public object EndValue { get; }
            public OsbEasing Easing { get; }
            public long SnapshotIndex { get; }
            public bool IsZeroDuration => Math.Abs(EndTime - StartTime) <= MergeTolerance;
        }

        /// <summary>
        /// Collects temporally overlapping command records for a specific storyboard object/type pair and determines whether
        /// they can be merged safely.
        /// </summary>
        private sealed class CommandFusionGroup
        {
            public CommandFusionGroup(CommandRecord seed, FusionOrderingContext context)
            {
                records = new List<CommandRecord> { seed };
                Context = context;
                StartTime = seed.StartTime;
                EndTime = seed.EndTime;
                First = seed;
                Last = seed;
                easingSignature = seed.Easing;
                HasMixedEasing = false;
            }

            private readonly List<CommandRecord> records;
            private readonly OsbEasing easingSignature;

            public IReadOnlyList<CommandRecord> Records => records;
            public double StartTime { get; private set; }
            public double EndTime { get; private set; }
            public CommandRecord First { get; private set; }
            public CommandRecord Last { get; private set; }
            public int Count => records.Count;
            public FusionOrderingContext Context { get; }
            public CommandAccessor Accessor => First.Accessor;
            public bool HasMixedEasing { get; private set; }

            public bool TryInclude(CommandRecord candidate)
            {
                if (candidate.StartTime > EndTime + MergeTolerance)
                    return false;

#if DEBUG
                Debug.Assert(candidate.Context.ObjectOrder == Context.ObjectOrder, "Fusion groups must not span multiple objects.");
#endif

                records.Add(candidate);
                StartTime = Math.Min(StartTime, candidate.StartTime);
                EndTime = Math.Max(EndTime, candidate.EndTime);

                if (!HasMixedEasing && candidate.Easing != easingSignature)
                    HasMixedEasing = true;

                if (candidate.EndTime > Last.EndTime || (Math.Abs(candidate.EndTime - Last.EndTime) <= MergeTolerance && candidate.OriginalIndex > Last.OriginalIndex))
                    Last = candidate;

                if (candidate.StartTime < First.StartTime || (Math.Abs(candidate.StartTime - First.StartTime) <= MergeTolerance && candidate.OriginalIndex < First.OriginalIndex))
                    First = candidate;

                return true;
            }
        }

        private readonly struct CommandOutput
        {
            public CommandOutput(ICommand command, string typeKey, double startTime, double endTime, long objectOrder, int contributorPriority, int contributorOrder, long snapshotIndex, bool merged)
            {
                Command = command;
                TypeKey = typeKey;
                StartTime = startTime;
                EndTime = endTime;
                ObjectOrder = objectOrder;
                ContributorPriority = contributorPriority;
                ContributorOrder = contributorOrder;
                SnapshotIndex = snapshotIndex;
                WasMerged = merged;
            }

            public ICommand Command { get; }
            public string TypeKey { get; }
            public double StartTime { get; }
            public double EndTime { get; }
            public long ObjectOrder { get; }
            public int ContributorPriority { get; }
            public int ContributorOrder { get; }
            public long SnapshotIndex { get; }
            public bool WasMerged { get; }
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

        private sealed class FusionCommandComparer : IComparer<ICommand>
        {
            public int Compare(ICommand x, ICommand y)
            {
                if (ReferenceEquals(x, y))
                    return 0;
                if (x == null)
                    return -1;
                if (y == null)
                    return 1;

                var typeComparison = string.CompareOrdinal(x.GetType().Name, y.GetType().Name);
                if (typeComparison != 0)
                    return typeComparison;

                var startComparison = sanitize(x.StartTime).CompareTo(sanitize(y.StartTime));
                if (startComparison != 0)
                    return startComparison;

                var endComparison = sanitize(x.EndTime).CompareTo(sanitize(y.EndTime));
                if (endComparison != 0)
                    return endComparison;

                return 0;
            }
        }

        private static readonly Comparison<CommandRecord> CommandRecordComparer = (left, right) =>
        {
            var start = left.StartTime.CompareTo(right.StartTime);
            if (start != 0)
                return start;

            var end = left.EndTime.CompareTo(right.EndTime);
            if (end != 0)
                return end;

            return left.SnapshotIndex.CompareTo(right.SnapshotIndex);
        };

        /// <summary>
        /// Implements the final deterministic ordering: object creation order, command type, start, end, contributor priority,
        /// contributor order, then snapshot index.
        /// </summary>
        private static readonly Comparison<CommandOutput> CommandOutputComparer = (left, right) =>
        {
            var objectOrder = left.ObjectOrder.CompareTo(right.ObjectOrder);
            if (objectOrder != 0)
                return objectOrder;

            var typeComparison = string.CompareOrdinal(left.TypeKey, right.TypeKey);
            if (typeComparison != 0)
                return typeComparison;

            var startComparison = left.StartTime.CompareTo(right.StartTime);
            if (startComparison != 0)
                return startComparison;

            var endComparison = left.EndTime.CompareTo(right.EndTime);
            if (endComparison != 0)
                return endComparison;

            var priorityComparison = left.ContributorPriority.CompareTo(right.ContributorPriority);
            if (priorityComparison != 0)
                return priorityComparison;

            var contributorOrder = left.ContributorOrder.CompareTo(right.ContributorOrder);
            if (contributorOrder != 0)
                return contributorOrder;

            return left.SnapshotIndex.CompareTo(right.SnapshotIndex);
        };

        /// <summary>
        /// Reconstructs sprite command timelines after fusion using reflection, keeping runtime objects consistent without
        /// exposing editor-only state to callers.
        /// </summary>
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

            /// <summary>
            /// Rebuilds the sprite's internal command lists with the supplied fused commands.
            /// </summary>
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
