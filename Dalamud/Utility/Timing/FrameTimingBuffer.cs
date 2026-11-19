using System;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;

namespace Dalamud.Utility.Timing;

/// <summary>A fixed-size history ring with nonblocking publication and validated reads.</summary>
internal sealed class FrameTimingBuffer
{
    private const int MaxTrackedDrops = 256;
    private static readonly FrameTimingSource[] Sources = Enum.GetValues<FrameTimingSource>();

    private readonly Slot[] slots;
    // A failed reservation leaves the previous record in its slot. Track the latest failure so readers can recover it.
    private readonly long[] droppedSequences;
    // Retain individual cadence loss ticks up to the limit, then widen the affected source's loss range.
    private readonly CadenceDrop[] cadenceDrops;
    private readonly DropRange[] cadenceDropOverflow = CreateDropRanges();
    // Span and event losses have their own tracking limit, preserving cadence loss detail.
    private readonly FrameTimingRecordLoss[] nonCadenceDrops;
    // Ignore sequence stamps at or below this boundary when reading reused storage.
    private readonly long sequenceBase;
    private long nextSequence;
    private int activeWrites;
    private long droppedRecords;
    private long latestDroppedStartTick;
    private long cadenceDropCount;
    private long nonCadenceDropCount;
    private long latestNonCadenceOverflowStart;
    private long earliestNonCadenceOverflowEnd = long.MaxValue;
    private long evictedRecords;
    private long latestEvictedStartTick;

    /// <summary>Initializes a new instance of the <see cref="FrameTimingBuffer"/> class with its own ring storage.</summary>
    /// <param name="capacity">The maximum number of records the ring holds.</param>
    internal FrameTimingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.slots = new Slot[capacity];
        this.droppedSequences = new long[capacity];
        var trackedDropCapacity = Math.Min(capacity, MaxTrackedDrops);
        this.cadenceDrops = new CadenceDrop[trackedDropCapacity];
        this.nonCadenceDrops = new FrameTimingRecordLoss[trackedDropCapacity];
    }

    private FrameTimingBuffer(FrameTimingBuffer previous)
    {
        this.slots = previous.slots;
        this.droppedSequences = previous.droppedSequences;
        this.cadenceDrops = previous.cadenceDrops;
        this.nonCadenceDrops = previous.nonCadenceDrops;
        this.sequenceBase = previous.nextSequence;
        this.nextSequence = this.sequenceBase;
    }

    /// <summary>Starts a new history using the storage of a session that has no remaining writers or readers.</summary>
    /// <returns>A history that excludes all sequences written by the previous session.</returns>
    internal FrameTimingBuffer CreateSuccessor() => new(this);

    /// <summary>Reserves a write before sampling its completion or event tick.</summary>
    /// <param name="sequence">The reservation to complete with <see cref="Write"/>.</param>
    /// <returns>The timestamp sampled after reserving the sequence.</returns>
    internal long BeginWrite(out long sequence)
    {
        Interlocked.Increment(ref this.activeWrites);
        sequence = Interlocked.Increment(ref this.nextSequence);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>Samples a cadence tick while marking it pending until admission is resolved.</summary>
    /// <returns>The callback entry tick.</returns>
    /// <remarks>Resolve the pending entry with <see cref="WriteCadence"/> or <see cref="CancelCadenceWrite"/>.</remarks>
    internal long BeginCadenceWrite()
    {
        Interlocked.Increment(ref this.activeWrites);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>Reserves a sequence only for an accepted cadence entry and publishes it.</summary>
    /// <param name="record">The cadence entry for the pending tick.</param>
    internal void WriteCadence(in FrameTimingRecord record) =>
        this.Write(Interlocked.Increment(ref this.nextSequence), record);

    /// <summary>Clears a pending cadence write after admission closes.</summary>
    internal void CancelCadenceWrite() => Interlocked.Decrement(ref this.activeWrites);

    /// <summary>Publishes the record or counts the loss without waiting for an occupied slot.</summary>
    /// <param name="sequence">The reserved sequence.</param>
    /// <param name="record">The record sampled for this reservation.</param>
    internal void Write(long sequence, in FrameTimingRecord record)
    {
        var index = (int)((sequence - 1) % this.slots.Length);
        ref var slot = ref this.slots[index];
        var stamp = Volatile.Read(ref slot.Stamp);
        if (stamp < 0 || stamp >= sequence || Interlocked.CompareExchange(ref slot.Stamp, -sequence, stamp) != stamp)
        {
            MaintainMaximum(ref this.droppedSequences[index], sequence);
            MaintainMaximum(ref this.latestDroppedStartTick, record.StartTick);
            Interlocked.Increment(ref this.droppedRecords);
            if (record.Kind == FrameTimingRecordKind.Cadence && record.Source != FrameTimingSource.None)
            {
                var dropIndex = Interlocked.Increment(ref this.cadenceDropCount) - 1;
                if (dropIndex < this.cadenceDrops.Length)
                {
                    this.cadenceDrops[dropIndex] = new(record.Source, record.StartTick);
                }
                else
                {
                    ref var overflow = ref this.cadenceDropOverflow[(int)record.Source];
                    MaintainMinimum(ref overflow.FirstTick, record.StartTick);
                    MaintainMaximum(ref overflow.LastTick, record.StartTick);
                }
            }
            else
            {
                var dropIndex = Interlocked.Increment(ref this.nonCadenceDropCount) - 1;
                if (dropIndex < this.nonCadenceDrops.Length)
                {
                    this.nonCadenceDrops[dropIndex] = new(record.StartTick, record.EndTick);
                }
                else
                {
                    MaintainMaximum(ref this.latestNonCadenceOverflowStart, record.StartTick);
                    MaintainMinimum(ref this.earliestNonCadenceOverflowEnd, record.EndTick);
                }
            }
        }
        else
        {
            if (stamp > this.sequenceBase)
            {
                MaintainMaximum(ref this.latestEvictedStartTick, slot.Record.StartTick);
                Interlocked.Increment(ref this.evictedRecords);
            }

            slot.Record = record;
            Volatile.Write(ref slot.Stamp, sequence);
        }

        Interlocked.Decrement(ref this.activeWrites);
    }

    /// <summary>Reads retained records with a fixed admission state and closure tick.</summary>
    /// <param name="sessionId">The ID to attach to the summary.</param>
    /// <param name="startedTick">The inclusive session start tick.</param>
    /// <param name="closedTick">The exact admission closure tick, or zero for open admission.</param>
    /// <param name="afterCutoffSampled">An optional callback that receives the sampled read cutoff before copying begins.</param>
    /// <returns>The immutable summary and its coverage bounds.</returns>
    internal FrameTimingSummary Read(long sessionId, long startedTick, long closedTick, Action<long>? afterCutoffSampled = null)
        => this.Read(sessionId, startedTick, () => (closedTick == 0, closedTick, closedTick), afterCutoffSampled: afterCutoffSampled);

    /// <summary>Copies retained records and derives cadence coverage from a sampled read cutoff.</summary>
    /// <param name="sessionId">The ID to attach to the summary.</param>
    /// <param name="startedTick">The inclusive session start tick.</param>
    /// <param name="readAdmission">Reads admission and its closure bounds after the cutoff is sampled.</param>
    /// <param name="beforeCutoffSampled">An optional callback before sampling the cutoff and write counters.</param>
    /// <param name="afterCutoffSampled">An optional callback that receives the sampled read cutoff before copying begins.</param>
    /// <param name="previousSummary">A prior summary of this session whose immutable history may be reused.</param>
    /// <returns>The immutable summary and its coverage bounds.</returns>
    internal FrameTimingSummary Read(
        long sessionId,
        long startedTick,
        Func<(bool AdmitsProbes, long OpenThroughTick, long ClosedByTick)> readAdmission,
        Action? beforeCutoffSampled = null,
        Action<long>? afterCutoffSampled = null,
        FrameTimingSummary? previousSummary = null)
    {
        beforeCutoffSampled?.Invoke();
        // Bracket the cutoff and loss counters to detect reservations or pending writes that can obscure coverage.
        var before = Volatile.Read(ref this.nextSequence);
        var pendingBefore = Volatile.Read(ref this.activeWrites);
        var dropCount = Volatile.Read(ref this.droppedRecords);
        var droppedStartTick = Volatile.Read(ref this.latestDroppedStartTick);
        var cadenceDropCount = Volatile.Read(ref this.cadenceDropCount);
        Span<DropRange> overflow = cadenceDropCount > this.cadenceDrops.Length
                                       ? stackalloc DropRange[this.cadenceDropOverflow.Length]
                                       : [];
        for (var i = 0; i < overflow.Length; i++)
        {
            ref var sourceRange = ref this.cadenceDropOverflow[i];
            overflow[i] = new()
            {
                FirstTick = Volatile.Read(ref sourceRange.FirstTick),
                LastTick = Volatile.Read(ref sourceRange.LastTick),
            };
        }

        var nonCadenceDropCount = Volatile.Read(ref this.nonCadenceDropCount);
        var hasNonCadenceOverflow = nonCadenceDropCount > this.nonCadenceDrops.Length;
        var nonCadenceOverflow = hasNonCadenceOverflow
                                     ? new FrameTimingRecordLoss(
                                         Volatile.Read(ref this.latestNonCadenceOverflowStart),
                                         Volatile.Read(ref this.earliestNonCadenceOverflowEnd))
                                     : default;
        var evicted = Volatile.Read(ref this.evictedRecords);
        var evictedStartTick = Volatile.Read(ref this.latestEvictedStartTick);
        var readCutoff = Stopwatch.GetTimestamp();
        var pendingAfter = Volatile.Read(ref this.activeWrites);
        var last = Volatile.Read(ref this.nextSequence);
        afterCutoffSampled?.Invoke(readCutoff);

        var (admitsProbes, openThroughTick, closedByTick) = readAdmission();
        var cadenceCutoff = closedByTick == 0 ? readCutoff : Math.Min(readCutoff, closedByTick);
        var collecting = admitsProbes || readCutoff < openThroughTick;
        var confirmedThrough = admitsProbes ? cadenceCutoff : Math.Min(cadenceCutoff, openThroughTick);
        var canEstablishAdjacency = (admitsProbes || closedByTick != 0) &&
                                     pendingBefore == 0 && pendingAfter == 0 && before == last;
        var first = Math.Max(this.sequenceBase, last - this.slots.Length) + 1;
        // Start after the latest evicted start tick to exclude cadence entries that share that tick.
        var retainedFrom = Math.Max(startedTick, evictedStartTick + 1);
        var missing = 0;
        using var scratch = MemoryPool<FrameTimingRecord>.Shared.Rent((int)(last - first + 1));
        var records = scratch.Memory.Span;
        var recordCount = 0;

        for (var sequence = first; sequence <= last; sequence++)
        {
            var index = (int)((sequence - 1) % this.slots.Length);
            ref var slot = ref this.slots[index];
            var stamp = Volatile.Read(ref slot.Stamp);
            if (stamp > this.sequenceBase && stamp <= sequence &&
                (stamp == sequence || Volatile.Read(ref this.droppedSequences[index]) == sequence))
            {
                var record = slot.Record;
                // Validate the stamp after the entire copy to reject records overwritten during the read.
                Thread.MemoryBarrier();
                var validatedStamp = Volatile.Read(ref slot.Stamp);
                if (validatedStamp == stamp)
                {
                    if (record.StartTick >= retainedFrom)
                    {
                        // A writer outside the sampled sequence range can share the cutoff tick.
                        if (record.EndTick < readCutoff)
                            records[recordCount++] = record;

                        // An admitted entry at the exclusive boundary still confirms admission up to it.
                        if (record.Kind == FrameTimingRecordKind.Cadence && record.StartTick <= cadenceCutoff)
                            confirmedThrough = Math.Max(confirmedThrough, record.StartTick);
                    }

                    continue;
                }

                stamp = validatedStamp;
            }

            missing++;
            // A committed replacement beyond the sampled sequence range has already published its eviction tick.
            if (stamp <= last)
                canEstablishAdjacency = false;
        }

        if (missing > 0 && canEstablishAdjacency)
        {
            // Delayed writes can reorder records by tick, so trim every record before the new start.
            retainedFrom = Math.Max(retainedFrom, Volatile.Read(ref this.latestEvictedStartTick) + 1);
            var retainedCount = 0;
            foreach (ref readonly var record in records[..recordCount])
            {
                if (record.StartTick >= retainedFrom)
                    records[retainedCount++] = record;
            }

            recordCount = retainedCount;
        }

        var hasCadenceWindow = retainedFrom < cadenceCutoff;
        var readValidated = canEstablishAdjacency && missing == 0;
        var readComplete = readValidated && confirmedThrough == cadenceCutoff && hasCadenceWindow;
        var recordCoverageComplete = readComplete && (dropCount == 0 || droppedStartTick < retainedFrom);
        // Range coverage requires a validated set of sampled slots and loss endpoints.
        var nonCadenceLosses = ImmutableArray<FrameTimingRecordLoss>.Empty;
        if (readValidated && nonCadenceDropCount > 0)
        {
            var trackedCount = (int)Math.Min(nonCadenceDropCount, this.nonCadenceDrops.Length);
            var losses = ImmutableArray.CreateBuilder<FrameTimingRecordLoss>(trackedCount + (hasNonCadenceOverflow ? 1 : 0));
            losses.AddRange(this.nonCadenceDrops.AsSpan(0, trackedCount));
            if (hasNonCadenceOverflow)
                losses.Add(nonCadenceOverflow);

            nonCadenceLosses = losses.MoveToImmutable();
        }

        // With no pending writes at the cutoff, recorded drop ticks can rule out individual intervals.
        var trackedDrops = canEstablishAdjacency
                               ? this.cadenceDrops.AsSpan(0, (int)Math.Min(cadenceDropCount, this.cadenceDrops.Length))
                               : Span<CadenceDrop>.Empty;
        records = records[..recordCount];
        records.Sort(CompareRecordTime);
        var previous = previousSummary?.SessionId == sessionId ? previousSummary : null;
        var history = FrameTimingHistory.Create(records, previous?.Records);
        // Reuse intervals when both reads validate the same history without drops or a change in included entries.
        var cadence = previous is { ReadValidated: true, DroppedRecords: 0 } && readValidated && dropCount == 0 &&
                      ReferenceEquals(history, previous.Records) &&
                      (cadenceCutoff == previous.CutoffTick || records.IsEmpty ||
                       records[^1].StartTick < Math.Min(cadenceCutoff, previous.CutoffTick))
                          ? RefreshCadenceCoverage(previous.Cadence, readComplete)
                          : BuildCadence(records, cadenceCutoff, retainedFrom, canEstablishAdjacency,
                                         readValidated, readComplete, trackedDrops, overflow);

        return new(
            sessionId, startedTick, Math.Min(retainedFrom, cadenceCutoff), cadenceCutoff, confirmedThrough,
            collecting, this.slots.Length, dropCount, evicted, missing, recordCoverageComplete, history, cadence)
        {
            ReadValidated = readValidated,
            NonCadenceLosses = nonCadenceLosses,
        };
    }

    private static int CompareRecordTime(FrameTimingRecord left, FrameTimingRecord right)
    {
        var byStart = left.StartTick.CompareTo(right.StartTick);
        if (byStart != 0)
            return byStart;

        // Break timestamp ties consistently so otherwise unchanged history blocks can be shared.
        return (left.OperationId, left.EndTick, (byte)left.Kind, (byte)left.Source, (byte)left.Span, left.ThreadId,
                (byte)left.Outcome, (byte)left.Target, left.NativeStatus, left.SnapshotId, left.SnapshotAgeTicks)
            .CompareTo((right.OperationId, right.EndTick, (byte)right.Kind, (byte)right.Source, (byte)right.Span, right.ThreadId,
                        (byte)right.Outcome, (byte)right.Target, right.NativeStatus, right.SnapshotId, right.SnapshotAgeTicks));
    }

    private static ImmutableArray<FrameTimingCadence> RefreshCadenceCoverage(
        ImmutableArray<FrameTimingCadence> cadence, bool readComplete)
    {
        if (cadence[0].CoverageComplete == readComplete)
            return cadence;

        var updated = ImmutableArray.CreateBuilder<FrameTimingCadence>(cadence.Length);
        foreach (var series in cadence)
            updated.Add(series with { CoverageComplete = readComplete });

        return updated.MoveToImmutable();
    }

    private static ImmutableArray<FrameTimingCadence> BuildCadence(
        ReadOnlySpan<FrameTimingRecord> records,
        long cadenceCutoff,
        long retainedFrom,
        bool canEstablishAdjacency,
        bool readValidated,
        bool readComplete,
        ReadOnlySpan<CadenceDrop> trackedDrops,
        ReadOnlySpan<DropRange> overflow)
    {
        var cadence = ImmutableArray.CreateBuilder<FrameTimingCadence>(Sources.Length - 1);
        var hasCadenceWindow = retainedFrom < cadenceCutoff;
        Span<long> dropTickWorkspace = stackalloc long[trackedDrops.Length];
        Span<int> entryCounts = stackalloc int[(int)Sources[^1] + 1];
        entryCounts.Clear();
        foreach (ref readonly var record in records)
        {
            if (record.StartTick >= cadenceCutoff)
                break;

            if (record.Kind == FrameTimingRecordKind.Cadence && (int)record.Source < entryCounts.Length)
                entryCounts[(int)record.Source]++;
        }

        foreach (var source in Sources)
        {
            if (source == FrameTimingSource.None)
                continue;

            var overflowRange = overflow.IsEmpty ? DropRange.Empty : overflow[(int)source];
            var overflowInWindow = hasCadenceWindow && overflowRange.Intersects(retainedFrom, cadenceCutoff - 1);
            var dropTickCount = 0;
            foreach (var drop in trackedDrops)
            {
                if (drop.Source == source && drop.Tick >= retainedFrom && drop.Tick < cadenceCutoff)
                    dropTickWorkspace[dropTickCount++] = drop.Tick;
            }

            var droppedTicks = dropTickWorkspace[..dropTickCount];
            var entryCount = entryCounts[(int)source];
            var entries = ImmutableArray.CreateBuilder<FrameTimingRecord>(entryCount);
            if (entryCount > 0)
            {
                foreach (ref readonly var record in records)
                {
                    if (record.Kind == FrameTimingRecordKind.Cadence && record.Source == source && record.StartTick < cadenceCutoff)
                        entries.Add(record);
                }
            }

            var intervals = ImmutableArray.CreateBuilder<FrameTimingInterval>(canEstablishAdjacency ? Math.Max(0, entryCount - 1) : 0);
            if (canEstablishAdjacency)
            {
                droppedTicks.Sort();
                var nextDrop = 0;
                for (var i = 1; i < entries.Count; i++)
                {
                    var previousTick = entries[i - 1].StartTick;
                    var currentTick = entries[i].StartTick;
                    // A dropped entry sharing either endpoint's tick can make adjacency ambiguous.
                    while (nextDrop < droppedTicks.Length && droppedTicks[nextDrop] < previousTick)
                        nextDrop++;

                    if ((nextDrop == droppedTicks.Length || droppedTicks[nextDrop] > currentTick) &&
                        (!overflowInWindow || !overflowRange.Intersects(previousTick, currentTick)))
                        intervals.Add(new(previousTick, currentTick));
                }
            }

            var cadenceComplete = readComplete && droppedTicks.IsEmpty && !overflowInWindow;
            var lossRanges = ImmutableArray.CreateBuilder<FrameTimingInterval>(dropTickCount + (overflowInWindow ? 1 : 0));
            foreach (var tick in droppedTicks)
                lossRanges.Add(new(tick, tick));

            if (overflowInWindow)
                lossRanges.Add(new(overflowRange.FirstTick, overflowRange.LastTick));

            cadence.Add(new(source, cadenceComplete, entries.MoveToImmutable(), intervals.DrainToImmutable())
            {
                ReadValidated = readValidated,
                LossRanges = lossRanges.MoveToImmutable(),
            });
        }

        return cadence.MoveToImmutable();
    }

    private static DropRange[] CreateDropRanges()
    {
        var ranges = new DropRange[(int)Sources[^1] + 1];
        Array.Fill(ranges, DropRange.Empty);
        return ranges;
    }

    private static void MaintainMinimum(ref long minimum, long candidate)
    {
        var previous = Volatile.Read(ref minimum);
        while (candidate < previous)
        {
            var observed = Interlocked.CompareExchange(ref minimum, candidate, previous);
            if (observed == previous)
                break;

            previous = observed;
        }
    }

    private static void MaintainMaximum(ref long maximum, long candidate)
    {
        var previous = Volatile.Read(ref maximum);
        while (candidate > previous)
        {
            var observed = Interlocked.CompareExchange(ref maximum, candidate, previous);
            if (observed == previous)
                break;

            previous = observed;
        }
    }

    private readonly record struct CadenceDrop(FrameTimingSource Source, long Tick);

    private struct DropRange
    {
        public long FirstTick;
        public long LastTick;

        public static DropRange Empty => new() { FirstTick = long.MaxValue };

        public readonly bool Intersects(long firstTick, long lastTick) =>
            this.FirstTick <= this.LastTick && this.FirstTick <= lastTick && this.LastTick >= firstTick;
    }

    private struct Slot
    {
        // Zero is empty. A negative sequence reserves the slot. A positive sequence identifies a committed record.
        public long Stamp;
        public FrameTimingRecord Record;
    }
}
