using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace Dalamud.Utility.Timing;

/// <summary>An immutable view of records and coverage for one live-history session.</summary>
/// <remarks>
/// Cadence covers <c>[RetainedFromTick, CutoffTick)</c>. The cutoff is the read tick, capped at the tick by which
/// admission was known to have closed, when available. <c>AdmissionConfirmedThroughTick</c> bounds confirmed open
/// admission within that window, with zero for an unavailable bound. <c>Collecting</c> indicates confirmed open
/// admission at the read sample.
/// <c>Records</c> includes retained measurements that ended before the read sample. In-flight operations can
/// publish their elapsed spans after collection stops.
/// <c>CoverageComplete</c> describes the retained record stream. Cadence coverage is evaluated per source.
/// <c>DroppedRecords</c> counts confirmed write losses. <c>UnreadRecords</c> counts slots the read could not validate.
/// </remarks>
internal sealed record FrameTimingSummary(
    long SessionId,
    long StartedTick,
    long RetainedFromTick,
    long CutoffTick,
    long AdmissionConfirmedThroughTick,
    bool Collecting,
    int Capacity,
    long DroppedRecords,
    long EvictedRecords,
    int UnreadRecords,
    bool CoverageComplete,
    FrameTimingHistory Records,
    ImmutableArray<FrameTimingCadence> Cadence)
{
    /// <summary>Gets the frequency for all raw ticks in the view.</summary>
    internal long ClockFrequency => Stopwatch.Frequency;

    /// <summary>Gets whether the stopped session retains a confirmed range before its uncertain stop boundary.</summary>
    internal bool CanExcludeUncertainStopBoundary => !this.Collecting &&
        this.AdmissionConfirmedThroughTick > this.RetainedFromTick && this.AdmissionConfirmedThroughTick < this.CutoffTick;

    /// <summary>Gets whether the recorder confirmed the stop bounds and completion of all admitted probes before the read.</summary>
    internal bool IsFinal { get; init; }

    /// <summary>Gets whether the sampled slots and loss metadata support checking coverage of a subrange.</summary>
    internal bool ReadValidated { get; init; }

    /// <summary>Gets the endpoint bounds of lost spans and events when the read validated the loss metadata.</summary>
    internal ImmutableArray<FrameTimingRecordLoss> NonCadenceLosses { get; init; } = [];

    /// <summary>Checks record coverage for a selection with an inclusive start and an exclusive end.</summary>
    /// <param name="startTick">The inclusive selection start.</param>
    /// <param name="endTick">The exclusive selection end.</param>
    /// <returns>Whether the read establishes complete coverage for records with both endpoints in the selection.</returns>
    internal bool HasRecordCoverage(long startTick, long endTick)
    {
        if (startTick >= endTick || startTick < this.RetainedFromTick ||
            endTick > this.CutoffTick || endTick > this.AdmissionConfirmedThroughTick)
            return false;

        return this.CoverageComplete || (this.ReadValidated &&
               !this.NonCadenceLosses.Any(loss => loss.LatestStartTick >= startTick && loss.EarliestEndTick < endTick) &&
               !this.Cadence.Any(series => series.LossRanges.Any(loss => loss.StartTick < endTick && loss.EndTick >= startTick)));
    }
}

/// <summary>The endpoint bounds of an individual record loss or a group of losses after tracking overflow.</summary>
/// <remarks>After overflow, the latest start and earliest end can belong to different records.</remarks>
internal readonly record struct FrameTimingRecordLoss(long LatestStartTick, long EarliestEndTick);

/// <summary>Time-ordered callback entries and intervals with confirmed adjacency for one source.</summary>
/// <remarks>Coverage may be incomplete even when some intervals are valid.</remarks>
internal sealed record FrameTimingCadence(
    FrameTimingSource Source,
    bool CoverageComplete,
    ImmutableArray<FrameTimingRecord> Entries,
    ImmutableArray<FrameTimingInterval> Intervals)
{
    /// <summary>Gets whether the read supports checking cadence coverage of a subrange.</summary>
    internal bool ReadValidated { get; init; }

    /// <summary>Gets the inclusive tick bounds where cadence entries are known or suspected to have been lost.</summary>
    internal ImmutableArray<FrameTimingInterval> LossRanges { get; init; } = [];
}

/// <summary>The endpoints of one observed cadence interval in the common clock domain.</summary>
internal readonly record struct FrameTimingInterval(long StartTick, long EndTick);
