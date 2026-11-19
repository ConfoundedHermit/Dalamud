using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Dalamud.Utility.Timing;

/// <summary>Analysis and plot data for one selected range in a recorder session.</summary>
/// <remarks>The range is inclusive at the start and exclusive at the end. Build the view outside rendering guards.</remarks>
internal sealed class FrameTimingRange
{
    /// <summary>The maximum number of measured records in the hitch breakdown.</summary>
    internal const int MaxHitchRecords = 128;

    private FrameTimingRange(
        FrameTimingSummary summary, long startTick, long endTick, double targetRate, (int Cadence, int Phase, int Age)? plotBuckets)
    {
        if (!double.IsFinite(targetRate) || targetRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRate));

        this.Summary = summary;
        this.StartTick = Math.Clamp(startTick, summary.RetainedFromTick, summary.CutoffTick);
        this.EndTick = Math.Clamp(endTick, this.StartTick, summary.CutoffTick);
        this.BudgetMilliseconds = 1000 / targetRate;
        this.CoverageComplete = summary.HasRecordCoverage(this.StartTick, this.EndTick);
        this.DurationSeconds = (double)(this.EndTick - this.StartTick) / summary.ClockFrequency;

        var completedOperations = this.CoverageComplete ? new HashSet<long>() : null;
        var phaseCounts = new Dictionary<(FrameTimingSpan Span, FrameTimingSource Source), int>();
        var overlayOutcomes = new Dictionary<(FrameTimingSource Source, FrameTimingOutcome Outcome), int>();
        var reuse = new Dictionary<long, int>();
        var knownReplayAges = 0;
        var recordBlocks = summary.Records.Blocks;

        // Count samples first so each series can allocate buffers sized for its plot.
        foreach (var block in recordBlocks)
        {
            foreach (ref readonly var record in block.AsSpan())
            {
                if (!this.Includes(record.StartTick, record.EndTick))
                    continue;

                switch (record.Kind)
                {
                    case FrameTimingRecordKind.Elapsed:
                        var phase = (record.Span, record.Source);
                        phaseCounts[phase] = phaseCounts.GetValueOrDefault(phase) + 1;
                        if (completedOperations is not null && IsRootSpan(record.Span))
                            completedOperations.Add(record.OperationId);

                        if (record.Span == FrameTimingSpan.OverlayCallback)
                        {
                            var outcome = (record.Source, record.Outcome);
                            overlayOutcomes[outcome] = overlayOutcomes.GetValueOrDefault(outcome) + 1;
                        }

                        if (record.Span == FrameTimingSpan.MainReplay)
                        {
                            if (HasReplayAge(record))
                                knownReplayAges++;
                            else
                                this.UnknownReplayAges++;

                            if (record.Outcome == FrameTimingOutcome.Completed)
                            {
                                this.CompletedReplays++;
                                if (record.SnapshotId is > 0 and var snapshotId)
                                    reuse[snapshotId] = reuse.GetValueOrDefault(snapshotId) + 1;
                                else
                                    this.UnknownReplaySnapshots++;
                            }
                        }

                        break;
                    case FrameTimingRecordKind.Publication:
                        this.Publications++;
                        if (record.SnapshotId is > 0 and var publishedId)
                            reuse.TryAdd(publishedId, 0);

                        break;
                    case FrameTimingRecordKind.Point when record.Span == FrameTimingSpan.MainReadyAttempt:
                        this.ReadyAttempts++;
                        break;
                }
            }
        }

        var phasePlots = new Dictionary<(FrameTimingSpan Span, FrameTimingSource Source), PlotBuilder>(phaseCounts.Count);
        using var agePlot = new PlotBuilder(knownReplayAges, this.DurationSeconds, plotBuckets?.Age);

        try
        {
            foreach (var (phase, count) in phaseCounts)
                phasePlots.Add(phase, new(count, this.DurationSeconds, plotBuckets?.Phase));

            foreach (var block in recordBlocks)
            {
                foreach (ref readonly var record in block.AsSpan())
                {
                    if (record.Kind != FrameTimingRecordKind.Elapsed || !this.Includes(record.StartTick, record.EndTick))
                        continue;

                    var hitch = new FrameTimingHitch(record.Source, record.StartTick, record.EndTick, record.Span, record.OperationId);
                    var seconds = this.SecondsFromStart(record.StartTick);
                    phasePlots[(record.Span, record.Source)].Add(hitch, seconds, this.ToMilliseconds(record.EndTick - record.StartTick));
                    if (record.Span == FrameTimingSpan.MainReplay && HasReplayAge(record))
                        agePlot.Add(hitch, seconds, this.ToMilliseconds(record.SnapshotAgeTicks.GetValueOrDefault()));
                }
            }

            this.MainUiAge = agePlot.Build();
            this.Phases = phasePlots.OrderBy(static item => item.Key)
                                    .Select(static item => new FrameTimingPhaseView(item.Key.Source, item.Key.Span, item.Value.Build()))
                                    .ToImmutableArray();
        }
        finally
        {
            foreach (var plot in phasePlots.Values)
                plot.Dispose();
        }

        this.Cadence = summary.Cadence.Select(series => this.AnalyzeCadence(series, completedOperations, targetRate, plotBuckets?.Cadence))
                              .ToImmutableArray();
        this.OverlayOutcomes = overlayOutcomes.OrderBy(static item => item.Key)
                                              .Select(static item => new FrameTimingOverlayOutcomeCount(item.Key.Source, item.Key.Outcome, item.Value))
                                              .ToImmutableArray();
        this.SnapshotReuse = reuse.OrderBy(static item => item.Key)
                                  .Select(static item => new FrameTimingSnapshotReuse(item.Key, item.Value))
                                  .ToImmutableArray();
    }

    /// <summary>Gets the immutable session read used to create this range.</summary>
    internal FrameTimingSummary Summary { get; }

    /// <summary>Gets the first tick in the selected retained range.</summary>
    internal long StartTick { get; }

    /// <summary>Gets the exclusive end tick in the selected retained range.</summary>
    internal long EndTick { get; }

    /// <summary>Gets the duration of the selected retained range in seconds.</summary>
    internal double DurationSeconds { get; }

    /// <summary>Gets the target-rate budget in milliseconds.</summary>
    internal double BudgetMilliseconds { get; }

    /// <summary>Gets whether all record types have complete coverage in the selected range.</summary>
    internal bool CoverageComplete { get; }

    /// <summary>Gets the cadence statistics, plot samples and coverage for each callback source.</summary>
    internal ImmutableArray<FrameTimingCadenceView> Cadence { get; }

    /// <summary>Gets inclusive elapsed measurements grouped by measured boundary and source.</summary>
    internal ImmutableArray<FrameTimingPhaseView> Phases { get; }

    /// <summary>Gets main UI age at replay entry for measured renderer calls with same-session snapshot metadata.</summary>
    internal FrameTimingPlotSeries MainUiAge { get; }

    /// <summary>Gets the number of renderer calls with unknown UI age.</summary>
    internal int UnknownReplayAges { get; }

    /// <summary>Gets the count of observed ready attempts, before queues and rendering.</summary>
    internal int ReadyAttempts { get; }

    /// <summary>Gets the count of observed successful snapshot publications.</summary>
    internal int Publications { get; }

    /// <summary>Gets the count of observed normally returned renderer calls.</summary>
    internal int CompletedReplays { get; }

    /// <summary>Gets the count of completed replays with unknown snapshot identity.</summary>
    internal int UnknownReplaySnapshots { get; }

    /// <summary>Gets counts of recorded overlay callback exits by source and outcome.</summary>
    internal ImmutableArray<FrameTimingOverlayOutcomeCount> OverlayOutcomes { get; }

    /// <summary>Gets completed replay counts within the range for observed snapshots.</summary>
    /// <remarks>A count of zero indicates a publication with no observed completion in the range.</remarks>
    internal ImmutableArray<FrameTimingSnapshotReuse> SnapshotReuse { get; }

    /// <summary>Builds the analysis of the available part of a requested range.</summary>
    /// <param name="summary">The immutable session read.</param>
    /// <param name="startTick">The requested inclusive range start.</param>
    /// <param name="endTick">The requested exclusive range end.</param>
    /// <param name="targetRate">The target cadence in entries per second.</param>
    /// <returns>The range limited to retained history.</returns>
    internal static FrameTimingRange Create(
        FrameTimingSummary summary, long startTick, long endTick, double targetRate) =>
        new(summary, startTick, endTick, targetRate, null);

    /// <summary>Builds the analysis of trailing history, ending at confirmed admission when possible after a stop.</summary>
    /// <param name="summary">The immutable session read.</param>
    /// <param name="seconds">The requested trailing duration in seconds.</param>
    /// <param name="targetRate">The target cadence in entries per second.</param>
    /// <param name="includeUncertainStopBoundary">Whether to use the sampled cutoff even when the stop boundary is uncertain.</param>
    /// <param name="plotBuckets">Optional chart resolutions for retaining only the extrema of dense plots.</param>
    /// <returns>The recent history analysis.</returns>
    internal static FrameTimingRange CreateRecent(
        FrameTimingSummary summary,
        double seconds,
        double targetRate,
        bool includeUncertainStopBoundary = false,
        (int Cadence, int Phase, int Age)? plotBuckets = null)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));

        var endTick = !includeUncertainStopBoundary && summary.CanExcludeUncertainStopBoundary
                          ? summary.AdmissionConfirmedThroughTick
                          : summary.CutoffTick;
        var retainedTicks = endTick - summary.RetainedFromTick;
        var requestedTicks = seconds * summary.ClockFrequency;
        var startTick = requestedTicks >= retainedTicks
                            ? summary.RetainedFromTick
                            : endTick - (long)Math.Ceiling(requestedTicks);
        return new(summary, startTick, endTick, targetRate, plotBuckets);
    }

    /// <summary>Finds spans and events associated with the hitch's operations or overlapping its time window.</summary>
    /// <remarks>Related records can come from anywhere in retained history, including outside the selected range.</remarks>
    /// <param name="hitch">The selected cadence interval or measured span.</param>
    /// <returns>At most <see cref="MaxHitchRecords"/> records, ordered by tick.</returns>
    internal ImmutableArray<FrameTimingRecord> GetHitch(FrameTimingHitch hitch)
    {
        var isCadence = hitch.OperationId == 0;
        var operationIds = isCadence
                               ? this.Summary.Cadence.Where(series => series.Source == hitch.Source)
                                     .SelectMany(static series => series.Entries)
                                     .Where(entry => entry.StartTick == hitch.StartTick || entry.StartTick == hitch.EndTick)
                                     .Select(static entry => entry.OperationId)
                                     .ToHashSet()
                               : [hitch.OperationId];

        var candidates = this.Summary.Records.Where(record => record.Kind != FrameTimingRecordKind.Cadence &&
                                                               (operationIds.Contains(record.OperationId) || Overlaps(record, hitch)));
        var selected = isCadence
                           ? SelectCadenceRecords(candidates, hitch, operationIds)
                           : SelectPhaseRecords(candidates, hitch);
        return OrderByTime(selected).ToImmutableArray();
    }

    private static IEnumerable<FrameTimingRecord> SelectCadenceRecords(
        IEnumerable<FrameTimingRecord> candidates, FrameTimingHitch hitch, HashSet<long> operationIds)
    {
        var indexed = candidates.Select(static (record, index) => (Record: record, Index: index));
        var nearby = indexed.OrderBy(candidate => GetCadencePriority(candidate.Record))
                            .ThenBy(static candidate => candidate.Record.StartTick)
                            .ThenByDescending(static candidate => candidate.Record.EndTick)
                            .Take(MaxHitchRecords + 1)
                            .ToArray();
        var priorityCount = nearby.TakeWhile(candidate => HasPriority(candidate.Record)).Count();
        if (nearby.Length <= MaxHitchRecords || priorityCount >= MaxHitchRecords)
            return nearby.Take(MaxHitchRecords).Select(static candidate => candidate.Record);

        // Reserve some rows for long overlaps so short spans near the endpoint cannot crowd them out.
        var overlapSlots = Math.Max(1, (MaxHitchRecords - priorityCount) / 4);
        var longest = indexed.Where(candidate => candidate.Record.Kind == FrameTimingRecordKind.Elapsed &&
                                                 !HasPriority(candidate.Record))
                             .Select(candidate => (candidate.Record, candidate.Index, Ticks: OverlapTicks(candidate.Record, hitch)))
                             .Where(static candidate => candidate.Ticks > 0)
                             .OrderByDescending(static candidate => candidate.Ticks)
                             .ThenBy(candidate => DistanceToBoundary(candidate.Record, hitch.EndTick))
                             .ThenBy(static candidate => candidate.Record.StartTick)
                             .ThenByDescending(static candidate => candidate.Record.EndTick)
                             .Take(overlapSlots)
                             .Select(static candidate => (candidate.Record, candidate.Index))
                             .ToArray();
        var longestIndices = longest.Select(static candidate => candidate.Index).ToHashSet();

        return nearby.Take(priorityCount)
                     .Concat(longest)
                     .Concat(nearby.Skip(priorityCount).Where(candidate => !longestIndices.Contains(candidate.Index)))
                     .Take(MaxHitchRecords)
                     .Select(static candidate => candidate.Record);

        bool HasPriority(FrameTimingRecord record) =>
            operationIds.Contains(record.OperationId) || record.Outcome == FrameTimingOutcome.Failed;

        (int Rank, long Distance) GetCadencePriority(FrameTimingRecord record)
        {
            var distanceFromEnd = DistanceToBoundary(record, hitch.EndTick);
            if (operationIds.Contains(record.OperationId))
            {
                // Put completed root callbacks at both endpoints ahead of their child spans.
                var rank = record.Kind == FrameTimingRecordKind.Elapsed && IsRootSpan(record.Span) ? 0 : 1;
                var distance = Math.Min(distanceFromEnd, DistanceToBoundary(record, hitch.StartTick));
                return (rank, distance);
            }

            return (record.Outcome == FrameTimingOutcome.Failed ? 2 : 3, distanceFromEnd);
        }
    }

    private static List<FrameTimingRecord> SelectPhaseRecords(IEnumerable<FrameTimingRecord> candidates, FrameTimingHitch hitch)
    {
        var related = candidates.Where(record => record.OperationId == hitch.OperationId);
        var selected = related.OrderByDescending(record => record.Kind == FrameTimingRecordKind.Elapsed &&
                                                            record.Span == hitch.Span &&
                                                            record.StartTick == hitch.StartTick &&
                                                            record.EndTick == hitch.EndTick)
                              .ThenBy(static record => record.StartTick)
                              .ThenByDescending(static record => record.EndTick)
                              .Take(MaxHitchRecords)
                              .ToList();
        var overlaps = candidates.Where(record => record.OperationId != hitch.OperationId);
        AddOverlaps(overlaps.Where(static record => record.Outcome == FrameTimingOutcome.Failed));
        AddOverlaps(overlaps.Where(static record => record.Outcome != FrameTimingOutcome.Failed));
        return selected;

        void AddOverlaps(IEnumerable<FrameTimingRecord> records)
        {
            var slots = MaxHitchRecords - selected.Count;
            if (slots == 0)
                return;

            // Give each occupied time bucket a row before taking more from any one bucket.
            var bucketsPerTick = slots / (double)Math.Max(1, hitch.EndTick - hitch.StartTick);
            var buckets = OrderByTime(records).GroupBy(record =>
            {
                var offset = Math.Max(0, record.StartTick - hitch.StartTick);
                return Math.Min(slots - 1, (int)(offset * bucketsPerTick));
            });
            selected.AddRange(buckets.SelectMany(static bucket => bucket.Select(static (record, index) => (Record: record, Index: index)))
                                     .OrderBy(static item => item.Index)
                                     .Take(slots)
                                     .Select(static item => item.Record));
        }
    }

    private static IOrderedEnumerable<FrameTimingRecord> OrderByTime(IEnumerable<FrameTimingRecord> records) =>
        records.OrderBy(static record => record.StartTick).ThenByDescending(static record => record.EndTick);

    private static bool IsRootSpan(FrameTimingSpan span) =>
        span is FrameTimingSpan.FrameworkUpdate or FrameTimingSpan.KernelPresent or FrameTimingSpan.OverlayCallback;

    private static bool HasReplayAge(in FrameTimingRecord record) => record is { SnapshotId: > 0, SnapshotAgeTicks: >= 0 };

    private static long DistanceToBoundary(FrameTimingRecord record, long tick) =>
        Math.Min(Math.Abs(record.StartTick - tick), Math.Abs(record.EndTick - tick));

    private static long OverlapTicks(FrameTimingRecord record, FrameTimingHitch hitch) =>
        Math.Min(record.EndTick, hitch.EndTick) - Math.Max(record.StartTick, hitch.StartTick);

    private static bool Overlaps(FrameTimingRecord record, FrameTimingHitch hitch)
    {
        if (record.StartTick == record.EndTick)
            return record.StartTick >= hitch.StartTick && record.StartTick <= hitch.EndTick;

        return record.StartTick <= hitch.EndTick && record.EndTick > hitch.StartTick;
    }

    private static bool HasCadenceCoverage(FrameTimingCadence cadence, long start, long end, long admissionConfirmedThrough)
    {
        if (start >= end || end > admissionConfirmedThrough)
            return false;

        if (cadence.CoverageComplete)
            return true;

        return cadence.ReadValidated && !cadence.LossRanges.Any(loss => loss.StartTick < end && loss.EndTick >= start);
    }

    private bool Includes(long start, long end) => start >= this.StartTick && end < this.EndTick;

    private FrameTimingCadenceView AnalyzeCadence(
        FrameTimingCadence series, HashSet<long>? completedOperations, double targetRate, int? plotBuckets)
    {
        FrameTimingRecord? lastEntry = null;
        FrameTimingRecord? unfinished = null;
        var entryCount = 0;
        foreach (var entry in series.Entries)
        {
            if (entry.StartTick < this.StartTick || entry.StartTick >= this.EndTick)
                continue;

            entryCount++;
            lastEntry = entry;
            if (completedOperations is not null && !completedOperations.Contains(entry.OperationId))
                unfinished = entry;
        }

        var intervalCount = 0;
        foreach (var interval in series.Intervals)
        {
            if (this.Includes(interval.StartTick, interval.EndTick))
                intervalCount++;
        }

        using var plot = new PlotBuilder(intervalCount, this.DurationSeconds, plotBuckets);
        var budgetTicks = this.Summary.ClockFrequency / targetRate;
        var overBudget = 0;
        foreach (var interval in series.Intervals)
        {
            if (!this.Includes(interval.StartTick, interval.EndTick))
                continue;

            var ticks = interval.EndTick - interval.StartTick;
            plot.Add(new(series.Source, interval.StartTick, interval.EndTick),
                this.SecondsFromStart(interval.EndTick), this.ToMilliseconds(ticks));
            if (ticks > budgetTicks)
                overBudget++;
        }

        var samples = plot.Build(overBudget);
        var confirmedThrough = this.Summary.AdmissionConfirmedThroughTick;
        var coverageComplete = HasCadenceCoverage(series, this.StartTick, this.EndTick, confirmedThrough);
        var trailingGap = lastEntry is { } last ? this.ToMilliseconds(this.EndTick - last.StartTick) : (double?)null;
        var trailingGapIsLowerBound = lastEntry is { } endpoint &&
                                      HasCadenceCoverage(series, endpoint.StartTick, this.EndTick, confirmedThrough);
        return new(series.Source, samples, entryCount, coverageComplete, trailingGap, trailingGapIsLowerBound, unfinished,
            Math.Max(0, entryCount - 1 - samples.Statistics.Count));
    }

    private double SecondsFromStart(long tick) => (double)(tick - this.StartTick) / this.Summary.ClockFrequency;

    private double ToMilliseconds(long ticks) => ticks * 1000.0 / this.Summary.ClockFrequency;

    private sealed class PlotBuilder : IDisposable
    {
        private readonly PlotCoordinates? coordinates;
        private readonly int capacity;
        private readonly int bucketCount;
        private readonly double bucketsPerSecond;
        private double[]? denseValues;
        private PlotSample[]? reducedSamples;
        private int reducedCount;
        private int sampleCount;
        private int currentBucket = -1;
        private PlotSample minimum;
        private PlotSample maximum;

        public PlotBuilder(int capacity, double durationSeconds, int? plotBuckets)
        {
            this.capacity = capacity;
            var requestedBuckets = Math.Max(1, plotBuckets.GetValueOrDefault());
            this.bucketCount = plotBuckets.HasValue && capacity > 2L * requestedBuckets ? requestedBuckets : 0;
            this.bucketsPerSecond = this.bucketCount / Math.Max(0.001, durationSeconds);
            if (this.bucketCount == 0)
                this.coordinates = new(capacity);
        }

        public void Add(FrameTimingHitch hitch, double seconds, double milliseconds)
        {
            var sample = new PlotSample(hitch, seconds, milliseconds, this.sampleCount++);
            if (this.coordinates is { } coordinates)
            {
                coordinates.Add(sample);
                return;
            }

            this.denseValues ??= ArrayPool<double>.Shared.Rent(this.capacity);
            this.denseValues[sample.Index] = milliseconds;
            // Samples arrive in time order, so only the current bucket needs to hold extrema.
            var bucket = Math.Min(this.bucketCount - 1, (int)(seconds * this.bucketsPerSecond));
            if (bucket != this.currentBucket)
            {
                this.FlushBucket();
                this.currentBucket = bucket;
                this.minimum = this.maximum = sample;
                return;
            }

            if (milliseconds < this.minimum.Milliseconds)
                this.minimum = sample;
            if (milliseconds > this.maximum.Milliseconds)
                this.maximum = sample;
        }

        public FrameTimingPlotSeries Build(int overBudget = 0)
        {
            var count = this.sampleCount;
            if (count == 0)
                return new([], [], [], default);

            this.FlushBucket();

            // Dense plots retain bucket extrema. Compute nearest ranks from all original values.
            var coordinates = this.coordinates;
            var sorted = this.denseValues ?? ArrayPool<double>.Shared.Rent(count);
            this.denseValues = null;
            try
            {
                if (coordinates is null)
                {
                    coordinates = new(this.reducedCount);
                    foreach (ref readonly var sample in this.reducedSamples.AsSpan(0, this.reducedCount))
                        coordinates.Add(sample);
                }
                else
                {
                    coordinates.CopyMillisecondsTo(sorted);
                }

                sorted.AsSpan(0, count).Sort();
                var statistics = new FrameTimingStatistics(
                    count, sorted[NearestRank(50)], sorted[NearestRank(95)], sorted[NearestRank(99)], sorted[count - 1], overBudget);
                return coordinates.Build(statistics);
            }
            finally
            {
                ArrayPool<double>.Shared.Return(sorted);
            }

            int NearestRank(int percentile) => (int)(((long)count * percentile + 99) / 100) - 1;
        }

        public void Dispose()
        {
            if (this.denseValues is { } values)
            {
                this.denseValues = null;
                ArrayPool<double>.Shared.Return(values);
            }

            if (this.reducedSamples is { } samples)
            {
                this.reducedSamples = null;
                ArrayPool<PlotSample>.Shared.Return(samples);
            }
        }

        private void AppendExtremum(in PlotSample sample)
        {
            this.reducedSamples ??= ArrayPool<PlotSample>.Shared.Rent(2 * this.bucketCount);
            this.reducedSamples[this.reducedCount++] = sample;
        }

        private void FlushBucket()
        {
            if (this.currentBucket < 0)
                return;

            // Preserve ascending timestamps for hit testing.
            if (this.minimum.Index <= this.maximum.Index)
            {
                this.AppendExtremum(this.minimum);
                if (this.minimum.Index < this.maximum.Index)
                    this.AppendExtremum(this.maximum);
            }
            else
            {
                this.AppendExtremum(this.maximum);
                this.AppendExtremum(this.minimum);
            }
        }

        private readonly record struct PlotSample(FrameTimingHitch Hitch, double Seconds, double Milliseconds, int Index);

        private sealed class PlotCoordinates(int count)
        {
            private readonly ImmutableArray<double>.Builder seconds = ImmutableArray.CreateBuilder<double>(count);
            private readonly ImmutableArray<double>.Builder milliseconds = ImmutableArray.CreateBuilder<double>(count);
            private readonly ImmutableArray<FrameTimingHitch>.Builder hitches = ImmutableArray.CreateBuilder<FrameTimingHitch>(count);

            public void Add(in PlotSample sample)
            {
                this.seconds.Add(sample.Seconds);
                this.milliseconds.Add(sample.Milliseconds);
                this.hitches.Add(sample.Hitch);
            }

            public void CopyMillisecondsTo(double[] values) => this.milliseconds.CopyTo(values, 0);

            public FrameTimingPlotSeries Build(FrameTimingStatistics statistics) =>
                new(this.seconds.MoveToImmutable(), this.milliseconds.MoveToImmutable(), this.hitches.MoveToImmutable(), statistics);
        }
    }
}

/// <summary>A plotted timing measurement and its associated boundary.</summary>
/// <remarks>Cadence intervals have no operation ID. An elapsed measurement carries the ID of its root operation.</remarks>
internal readonly record struct FrameTimingHitch(
    FrameTimingSource Source, long StartTick, long EndTick, FrameTimingSpan Span = FrameTimingSpan.None, long OperationId = 0);

/// <summary>Nearest-rank percentiles and sample counts for an observed timing series.</summary>
internal readonly record struct FrameTimingStatistics(int Count, double? P50, double? P95, double? P99, double? Max, int OverBudget);

/// <summary>Time-ordered plot samples with matching hitch identities and statistics for all observations.</summary>
internal sealed record FrameTimingPlotSeries(
    ImmutableArray<double> Seconds,
    ImmutableArray<double> Milliseconds,
    ImmutableArray<FrameTimingHitch> Hitches,
    FrameTimingStatistics Statistics);

/// <summary>Confirmed cadence intervals, trailing gap, and coverage for one callback source in the selected range.</summary>
/// <remarks>
/// The trailing gap runs from the last observed entry to the range end. It is a lower bound when cadence coverage
/// is complete between those ticks. <c>UnfinishedEntry</c> is the latest entry without a matching completion in a range
/// with complete record coverage. <c>UnconfirmedIntervalCount</c> counts consecutive observed entry pairs without
/// a confirmed interval.
/// </remarks>
internal sealed record FrameTimingCadenceView(
    FrameTimingSource Source,
    FrameTimingPlotSeries Samples,
    int EntryCount,
    bool CoverageComplete,
    double? TrailingGapMilliseconds,
    bool TrailingGapIsLowerBound,
    FrameTimingRecord? UnfinishedEntry,
    int UnconfirmedIntervalCount);

/// <summary>Completed elapsed measurements for a phase and callback source.</summary>
internal sealed record FrameTimingPhaseView(FrameTimingSource Source, FrameTimingSpan Span, FrameTimingPlotSeries Samples);

/// <summary>Observed completed replays of one snapshot during the selected range.</summary>
internal readonly record struct FrameTimingSnapshotReuse(long SnapshotId, int CompletedReplays);

/// <summary>The number of recorded overlay callback exits for one source and outcome.</summary>
internal readonly record struct FrameTimingOverlayOutcomeCount(FrameTimingSource Source, FrameTimingOutcome Outcome, int Count);
