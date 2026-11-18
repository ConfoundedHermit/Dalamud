using System.Collections.Immutable;
using System.Diagnostics;

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
    ImmutableArray<FrameTimingRecord> Records,
    ImmutableArray<FrameTimingCadence> Cadence)
{
    /// <summary>Gets the frequency for all raw ticks in the view.</summary>
    internal long ClockFrequency => Stopwatch.Frequency;
}

/// <summary>Time-ordered callback entries and intervals with confirmed adjacency for one source.</summary>
/// <remarks>Coverage may be incomplete even when some intervals are valid.</remarks>
internal sealed record FrameTimingCadence(
    FrameTimingSource Source,
    bool CoverageComplete,
    ImmutableArray<FrameTimingRecord> Entries,
    ImmutableArray<FrameTimingInterval> Intervals);

/// <summary>The endpoints of one observed cadence interval in the common clock domain.</summary>
internal readonly record struct FrameTimingInterval(long StartTick, long EndTick);
