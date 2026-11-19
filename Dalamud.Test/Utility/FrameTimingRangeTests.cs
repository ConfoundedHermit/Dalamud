using System;
using System.Diagnostics;
using System.Linq;

using Dalamud.Interface.Internal;
using Dalamud.Utility.Timing;

using Xunit;

namespace Dalamud.Test.Utility;

public class FrameTimingRangeTests
{
    [Fact]
    public void KnownCadenceUsesNearestRankAndKeepsTrailingGapSeparate()
    {
        var summary = ReadTimeline(new(32));
        var range = FrameTimingRange.Create(summary, At(0), At(120), targetRate: 60);
        var cadence = Source(range, FrameTimingSource.FrameworkUpdate);

        Assert.Equal(5, cadence.EntryCount);
        Assert.True(cadence.CoverageComplete);
        Assert.Equal(0, cadence.UnconfirmedIntervalCount);
        Assert.Equal(new FrameTimingStatistics(4, 10, 50, 50, 50, 2), cadence.Samples.Statistics);
        Assert.Equal<double>([10, 20, 10, 50], cadence.Samples.Milliseconds);
        Assert.Equal<double>([0.020, 0.040, 0.050, 0.100], cadence.Samples.Seconds);
        Assert.Equal(20, cadence.TrailingGapMilliseconds);
        Assert.True(cadence.TrailingGapIsLowerBound);
        Assert.Equal(At(100), cadence.UnfinishedEntry?.StartTick);
        Assert.Equal(1000.0 / 60, range.BudgetMilliseconds);

        var empty = Source(range, FrameTimingSource.ReShadeOverlay);
        Assert.Equal(0, empty.EntryCount);
        Assert.Equal(default, empty.Samples.Statistics);
        Assert.Null(empty.TrailingGapMilliseconds);

        var exactBudget = Source(FrameTimingRange.Create(summary, At(0), At(120), targetRate: 50), FrameTimingSource.FrameworkUpdate);
        Assert.Equal(1, exactBudget.Samples.Statistics.OverBudget);
    }

    [Fact]
    public void BoundedPlotsKeepTheSameExtremaHitchesAndFullStatistics()
    {
        var buffer = new FrameTimingBuffer(32);
        int[] times = [10, 30, 40, 70, 80, 95, 110, 120, 150, 160, 170, 185];
        int[] durations = [3, 1, 3, 8, 5, 8, 2, 5, 2, 10, 10, 6];
        int[] ages = [5, 7, 3, 4, 9, 4, 5, 10, 6, 2, 6, 6];
        for (var i = 0; i < times.Length; i++)
        {
            Write(buffer, Entry(i + 1, times[i]));
            Write(buffer, Elapsed(i + 1, times[i], times[i] + durations[i], 1, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay) with
            {
                SnapshotId = i + 1,
                SnapshotAgeTicks = Ticks(ages[i]),
            });
        }

        Write(buffer, Elapsed(1, 11, 12, 1, FrameTimingSource.KernelPresent, FrameTimingSpan.Step));
        var summary = buffer.Read(1, At(0), At(200));
        var full = FrameTimingRange.CreateRecent(summary, 1, 60);
        var bounded = FrameTimingRange.CreateRecent(summary, 1, 60, plotBuckets: (2, 2, 3));
        var reference = new FrameTimingPlotReadout(full, new(2, 2, 3));
        var fullCadence = Source(full, FrameTimingSource.FrameworkUpdate);
        var boundedCadence = Source(bounded, FrameTimingSource.FrameworkUpdate);
        Assert.Equal(3, boundedCadence.Samples.Statistics.OverBudget);
        Assert.Equal(fullCadence.TrailingGapMilliseconds, boundedCadence.TrailingGapMilliseconds);
        AssertPlot(fullCadence.Samples, boundedCadence.Samples);
        AssertPlot(full.MainUiAge, bounded.MainUiAge);
        foreach (var phase in full.Phases)
        {
            var boundedPhase = Assert.Single(bounded.Phases, item => item.Span == phase.Span && item.Source == phase.Source);
            AssertPlot(phase.Samples, boundedPhase.Samples);
        }

        void AssertPlot(FrameTimingPlotSeries all, FrameTimingPlotSeries reduced)
        {
            var expected = reference.GetDisplay(all);
            Assert.Equal(all.Statistics, reduced.Statistics);
            Assert.Equal<double>(expected.Seconds.ToArray(), reduced.Seconds);
            Assert.Equal<double>(expected.Milliseconds.ToArray(), reduced.Milliseconds);
            var hitches = expected.SourceIndices.IsEmpty
                              ? all.Hitches
                              : expected.SourceIndices.ToArray().Select(index => all.Hitches[index]);
            Assert.Equal<FrameTimingHitch>(hitches, reduced.Hitches);
        }
    }

    [Theory]
    [InlineData(75, 3, 1, true)]
    [InlineData(110, 4, 2, false)]
    public void LocalizedCadenceLossBreaksOnlyAffectedIntervals(
        int lostMilliseconds, int intervals, int overBudget, bool trailingCoverageComplete)
    {
        // Fill the ring before publishing the delayed entry so it is dropped without evicting retained records.
        var buffer = new FrameTimingBuffer(10);
        buffer.BeginWrite(out var delayed);
        var summary = ReadTimeline(buffer, () => buffer.Write(delayed, Entry(99, lostMilliseconds)));
        Assert.Equal(1, summary.DroppedRecords);
        Assert.Equal(0, summary.UnreadRecords);
        Assert.Equal(0, summary.EvictedRecords);

        var range = FrameTimingRange.Create(summary, At(0), At(120), targetRate: 60);
        var cadence = Source(range, FrameTimingSource.FrameworkUpdate);
        Assert.Equal(5, cadence.EntryCount);
        Assert.False(range.CoverageComplete);
        Assert.False(cadence.CoverageComplete);
        Assert.Equal(4 - intervals, cadence.UnconfirmedIntervalCount);
        Assert.Equal(intervals, cadence.Samples.Statistics.Count);
        Assert.Equal(overBudget, cadence.Samples.Statistics.OverBudget);
        Assert.Equal(20, cadence.TrailingGapMilliseconds);
        Assert.Equal(trailingCoverageComplete, cadence.TrailingGapIsLowerBound);
        Assert.Null(cadence.UnfinishedEntry);

        var beforeLoss = FrameTimingRange.Create(summary, At(0), At(lostMilliseconds), targetRate: 60);
        Assert.True(beforeLoss.CoverageComplete);
        var beforeLossCadence = Source(beforeLoss, FrameTimingSource.FrameworkUpdate);
        Assert.True(beforeLossCadence.CoverageComplete);
        Assert.True(beforeLossCadence.TrailingGapIsLowerBound);
    }

    [Fact]
    public void EarlierCadenceLossKeepsTrailingRecordCoverageAndUnfinishedEntry()
    {
        var buffer = new FrameTimingBuffer(10);
        buffer.BeginWrite(out var delayed);
        var summary = ReadTimeline(buffer, () => buffer.Write(delayed, Entry(99, 10)));
        Assert.False(summary.CoverageComplete);
        Assert.Equal(1, summary.DroppedRecords);
        Assert.Equal(0, summary.UnreadRecords);
        Assert.False(FrameTimingRange.Create(summary, At(10), At(120), targetRate: 60).CoverageComplete);

        var range = FrameTimingRange.Create(summary, At(50), At(120), targetRate: 60);
        var cadence = Source(range, FrameTimingSource.FrameworkUpdate);
        Assert.True(cadence.CoverageComplete);
        Assert.True(cadence.TrailingGapIsLowerBound);
        Assert.True(range.CoverageComplete);
        Assert.Equal(At(100), cadence.UnfinishedEntry?.StartTick);
    }

    [Theory]
    [InlineData(30, 51, false)]
    [InlineData(30, 50, true)]
    [InlineData(31, 70, true)]
    public void DroppedElapsedRecordAffectsCoverageWhenBothEndpointsAreSelected(int start, int end, bool complete)
    {
        var buffer = new FrameTimingBuffer(10);
        buffer.BeginWrite(out var delayed);
        var dropped = Elapsed(99, 30, 50, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate);
        var summary = ReadTimeline(buffer, () => buffer.Write(delayed, dropped));
        Assert.Equal(1, summary.DroppedRecords);
        Assert.Equal(0, summary.UnreadRecords);

        var range = FrameTimingRange.Create(summary, At(start), At(end), targetRate: 60);
        Assert.True(Source(range, FrameTimingSource.FrameworkUpdate).CoverageComplete);
        Assert.Equal(complete, range.CoverageComplete);
    }

    [Fact]
    public void UnvalidatedCadenceDoesNotSupplyAFalseTrailingLowerBound()
    {
        var buffer = new FrameTimingBuffer(32);
        var summary = ReadTimeline(buffer, () => buffer.BeginCadenceWrite());
        buffer.CancelCadenceWrite();
        Assert.Equal(0, summary.DroppedRecords);
        var range = FrameTimingRange.CreateRecent(summary, 0.12, 60);
        var cadence = Source(range, FrameTimingSource.FrameworkUpdate);

        Assert.Equal(5, cadence.EntryCount);
        Assert.False(range.CoverageComplete);
        Assert.False(cadence.CoverageComplete);
        Assert.Equal(4, cadence.UnconfirmedIntervalCount);
        Assert.Empty(cadence.Samples.Seconds);
        Assert.Equal(20, cadence.TrailingGapMilliseconds);
        Assert.False(cadence.TrailingGapIsLowerBound);
    }

    [Fact]
    public void SelectedRecordCoverageEndsAtTheConfirmedAdmissionBound()
    {
        var buffer = new FrameTimingBuffer(4);
        Write(buffer, Entry(1, 20));
        var summary = buffer.Read(1, At(0), () => (false, At(80), At(120)));

        Assert.False(summary.CoverageComplete);
        Assert.True(FrameTimingRange.Create(summary, At(10), At(80), targetRate: 60).CoverageComplete);
        Assert.False(FrameTimingRange.Create(summary, At(10), At(81), targetRate: 60).CoverageComplete);
    }

    [Fact]
    public void RecentStoppedHistoryUsesConfirmedAdmission()
    {
        var buffer = new FrameTimingBuffer(8);
        Write(buffer, Entry(1, 30));
        Write(buffer, Entry(2, 50));
        Write(buffer, Entry(3, 60));
        Write(buffer, Entry(4, 80));
        Write(buffer, Elapsed(1, 30, 35, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate));
        Write(buffer, Elapsed(2, 50, 55, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate));
        Write(buffer, Elapsed(3, 60, 90, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate));
        var summary = buffer.Read(1, At(0), () => (false, At(80), At(120)));

        Assert.False(summary.CoverageComplete);
        Assert.Equal(0, summary.DroppedRecords);
        Assert.Equal(0, summary.UnreadRecords);
        var range = FrameTimingRange.CreateRecent(summary, 0.05, 60);
        var cadence = Source(range, FrameTimingSource.FrameworkUpdate);
        Assert.Equal((At(30), At(80)), (range.StartTick, range.EndTick));
        Assert.True(range.CoverageComplete);
        Assert.True(cadence.CoverageComplete);
        Assert.Equal(3, cadence.EntryCount);
        Assert.Equal(20, cadence.TrailingGapMilliseconds);
        Assert.True(cadence.TrailingGapIsLowerBound);
        Assert.Equal(At(60), cadence.UnfinishedEntry?.StartTick);
    }

    [Fact]
    public void RecentStoppedHistoryKeepsObservedEntriesWithoutARetainedConfirmedRange()
    {
        var buffer = new FrameTimingBuffer(4);
        Write(buffer, Entry(1, 80));
        var summary = buffer.Read(1, At(80), () => (false, At(80), At(120)));

        var range = FrameTimingRange.CreateRecent(summary, 1, 60);
        Assert.Equal((At(80), At(120)), (range.StartTick, range.EndTick));
        Assert.False(range.CoverageComplete);
        Assert.Equal(1, Source(range, FrameTimingSource.FrameworkUpdate).EntryCount);
    }

    [Fact]
    public void SelectedSpikeShowsOverlappingSpansThreadsAndIndependentOutcomes()
    {
        var range = FrameTimingRange.CreateRecent(ReadTimeline(new(32)), 0.12, 60);
        var cadence = Source(range, FrameTimingSource.FrameworkUpdate);
        var selected = Assert.Single(cadence.Samples.Hitches, sample => sample.EndTick - sample.StartTick == Ticks(50));
        var records = range.GetHitch(selected);

        Assert.Equal(At(50), selected.StartTick);
        Assert.Equal(At(100), selected.EndTick);
        var parent = Assert.Single(records, record => record.Span == FrameTimingSpan.FrameworkUpdate);
        Assert.Equal((4L, 1, FrameTimingOutcome.Completed), (parent.OperationId, parent.ThreadId, parent.Outcome));
        var step = Assert.Single(records, record => record.Span == FrameTimingSpan.Step);
        Assert.Equal((1, FrameTimingOutcome.Failed, Ticks(15)), (step.ThreadId, step.Outcome, step.EndTick - step.StartTick));
        var native = Assert.Single(records, record => record.Span == FrameTimingSpan.KernelNativePresent);
        Assert.Equal((1, FrameTimingOutcome.Completed, Ticks(20)), (native.ThreadId, native.Outcome, native.EndTick - native.StartTick));
        var replay = Assert.Single(records, record => record.Span == FrameTimingSpan.MainReplay);
        Assert.Equal((7, FrameTimingOutcome.Completed, 17L), (replay.ThreadId, replay.Outcome, replay.SnapshotId.GetValueOrDefault()));
        var stepSeries = Assert.Single(range.Phases, phase => phase.Span == FrameTimingSpan.Step);
        Assert.Equal(15, stepSeries.Samples.Statistics.Max);
        Assert.Equal(44, Assert.Single(range.MainUiAge.Milliseconds));
    }

    [Fact]
    public void LongHitchKeepsRelatedRecordsAndOverlappingReplayEvidence()
    {
        const int intervalStart = 20;
        const int intervalEnd = 1020;
        var buffer = new FrameTimingBuffer(512);
        var startingUpdate = Elapsed(1, intervalStart, 1015, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate);
        var startingGuard = Elapsed(1, 350, 400, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkGuard);
        var startingNativeUpdate = Elapsed(1, 400, 1014, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkNativeUpdate);
        var resumedUpdate = Elapsed(2, intervalEnd, 1030, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate);
        var resumedTick = Elapsed(2, 1021, 1029, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkManagedTick);
        var earlyFailure = Elapsed(4, 28, 30, 7, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay, FrameTimingOutcome.Failed);
        var failedReplay = Elapsed(3, 1004, 1006, 7, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay, FrameTimingOutcome.Failed);

        Write(buffer, Entry(1, intervalStart));
        for (var start = intervalStart + 4; start < intervalEnd; start += 4)
        {
            var operationId = 4 + start;
            Write(buffer, Elapsed(operationId, start, start + 4, 7, FrameTimingSource.DxgiPresent, FrameTimingSpan.OverlayCallback));
            Write(buffer, Elapsed(operationId, start + 1, start + 3, 7, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay));
        }

        Write(buffer, startingGuard);
        Write(buffer, startingNativeUpdate);
        Write(buffer, startingUpdate);
        Write(buffer, earlyFailure);
        Write(buffer, failedReplay);
        Write(buffer, Entry(2, intervalEnd));
        Write(buffer, resumedTick);
        Write(buffer, resumedUpdate);

        var summary = buffer.Read(1, At(0), At(1050));
        var range = FrameTimingRange.Create(summary, At(0), At(1050), 60);
        var cadence = Source(range, FrameTimingSource.FrameworkUpdate);
        var selected = Assert.Single(cadence.Samples.Hitches);
        var records = range.GetHitch(selected);

        Assert.Equal(1000, Assert.Single(cadence.Samples.Milliseconds));
        Assert.Equal(FrameTimingRange.MaxHitchRecords, records.Length);
        Assert.Contains(startingUpdate, records);
        Assert.Contains(startingGuard, records);
        Assert.Contains(startingNativeUpdate, records);
        Assert.Contains(earlyFailure, records);
        Assert.Contains(failedReplay, records);
        Assert.Contains(resumedUpdate, records);
        Assert.Contains(resumedTick, records);
        Assert.Contains(records, record => record.Span == FrameTimingSpan.MainReplay && record.StartTick == At(intervalEnd - 3));
        Assert.DoesNotContain(records, record => record.Span == FrameTimingSpan.MainReplay && record.StartTick == At(intervalStart + 5));
        Assert.Equal<FrameTimingRecord>(records.OrderBy(record => record.StartTick).ThenByDescending(record => record.EndTick), records);

        var updates = Assert.Single(range.Phases, phase => phase.Span == FrameTimingSpan.FrameworkUpdate);
        var selectedUpdate = Assert.Single(updates.Samples.Hitches, sample => sample.OperationId == startingUpdate.OperationId);
        var phaseRecords = range.GetHitch(selectedUpdate);

        Assert.Equal(FrameTimingRange.MaxHitchRecords, phaseRecords.Length);
        Assert.Contains(startingUpdate, phaseRecords);
        Assert.Contains(startingNativeUpdate, phaseRecords);
        Assert.Contains(earlyFailure, phaseRecords);
        Assert.Contains(failedReplay, phaseRecords);
        Assert.DoesNotContain(resumedUpdate, phaseRecords);
        Assert.Contains(phaseRecords, record => IsRoutineOverlap(record) && record.StartTick < At(100));
        Assert.Contains(phaseRecords, record => IsRoutineOverlap(record) && record.StartTick >= At(480) && record.StartTick < At(560));
        Assert.Contains(phaseRecords, record => IsRoutineOverlap(record) && record.StartTick > At(950));
        Assert.Equal<FrameTimingRecord>(phaseRecords.OrderBy(record => record.StartTick).ThenByDescending(record => record.EndTick), phaseRecords);

        static bool IsRoutineOverlap(FrameTimingRecord record) =>
            record is { Source: FrameTimingSource.DxgiPresent, Outcome: FrameTimingOutcome.Completed };
    }

    [Fact]
    public void CadenceHitchKeepsLongOverlapsWhenShortSpansFillTheEnd()
    {
        const int intervalStart = 1010;
        const int intervalEnd = 2010;
        var buffer = new FrameTimingBuffer(256);
        var longReplay = Elapsed(3, 1050, 1450, 7, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay);
        var mostlyOutsideReplay = Elapsed(4, 10, 1011, 8, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay);
        var latestReplay = Elapsed(5, intervalEnd - 1, intervalEnd, 7, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay);

        Write(buffer, Entry(1, intervalStart));
        Write(buffer, Elapsed(1, intervalStart, intervalStart + 1, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate));
        Write(buffer, longReplay);
        Write(buffer, mostlyOutsideReplay);
        for (var start = intervalEnd - 150; start < intervalEnd - 1; start++)
            Write(buffer, Elapsed(start, start, start + 1, 7, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay));

        Write(buffer, latestReplay);
        Write(buffer, Entry(2, intervalEnd));
        Write(buffer, Elapsed(2, intervalEnd, intervalEnd + 1, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate));

        var range = FrameTimingRange.Create(buffer.Read(1, At(0), At(2020)), At(0), At(2020), 60);
        var cadence = Source(range, FrameTimingSource.FrameworkUpdate);
        var selected = Assert.Single(cadence.Samples.Hitches);
        var records = range.GetHitch(selected);

        Assert.Equal(1000, Assert.Single(cadence.Samples.Milliseconds));
        Assert.Equal(FrameTimingRange.MaxHitchRecords, records.Length);
        Assert.Contains(longReplay, records);
        Assert.Contains(latestReplay, records);
        Assert.DoesNotContain(mostlyOutsideReplay, records);
    }

    [Fact]
    public void SelectedRangeUsesBothEndpointsAndKeepsZeroDurationSamples()
    {
        var buffer = new FrameTimingBuffer(16);
        Write(buffer, Entry(1, 10));
        Write(buffer, Entry(2, 20));
        Write(buffer, Entry(3, 20));
        Write(buffer, Entry(4, 40));
        Write(buffer, Entry(5, 50));
        Write(buffer, Elapsed(1, 10, 30, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate));
        Write(buffer, Elapsed(2, 20, 20, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate));
        Write(buffer, Elapsed(3, 20, 22, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate));
        Write(buffer, Elapsed(4, 40, 50, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate));

        var summary = buffer.Read(sessionId: 1, startedTick: At(5), closedTick: At(80));
        var range = FrameTimingRange.Create(summary, At(20), At(50), targetRate: 50);
        var cadence = Source(range, FrameTimingSource.FrameworkUpdate);
        Assert.Equal<double>([0, 20], cadence.Samples.Milliseconds);
        Assert.Equal(new FrameTimingStatistics(2, 0, 20, 20, 20, 0), cadence.Samples.Statistics);
        Assert.Equal(10, cadence.TrailingGapMilliseconds);
        Assert.Equal(At(40), cadence.UnfinishedEntry?.StartTick);
        Assert.Equal<double>([0, 2], Assert.Single(range.Phases).Samples.Milliseconds.Order());

        var clamped = FrameTimingRange.Create(summary, At(0), At(100), targetRate: 50);
        Assert.Equal(At(5), clamped.StartTick);
        Assert.Equal(At(80), clamped.EndTick);
    }

    [Fact]
    public void UiAgeReuseAndCallbackOutcomesUseTheirOwnBoundaries()
    {
        var buffer = new FrameTimingBuffer(32);
        Write(buffer, Elapsed(1, 10, 10, 1, FrameTimingSource.KernelPresent, FrameTimingSpan.Step) with
        {
            Kind = FrameTimingRecordKind.Publication,
            SnapshotId = 17,
        });
        Write(buffer, Elapsed(2, 20, 21, 2, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay) with
        {
            SnapshotId = 17,
            SnapshotAgeTicks = Ticks(12),
        });
        Write(buffer, Elapsed(3, 30, 31, 2, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay) with
        {
            SnapshotId = 17,
            SnapshotAgeTicks = Ticks(22),
        });
        Write(buffer, Elapsed(4, 40, 40, 1, FrameTimingSource.KernelPresent, FrameTimingSpan.Step) with
        {
            Kind = FrameTimingRecordKind.Publication,
            SnapshotId = 18,
        });
        Write(buffer, Elapsed(5, 50, 51, 2, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay));
        Write(buffer, Elapsed(6, 60, 61, 2, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay, FrameTimingOutcome.Failed) with
        {
            SnapshotId = 18,
            SnapshotAgeTicks = Ticks(23),
        });
        Write(buffer, Elapsed(6, 59, 59, 2, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReadyAttempt) with
        {
            Kind = FrameTimingRecordKind.Point,
        });
        Write(buffer, Elapsed(6, 59, 62, 2, FrameTimingSource.DxgiPresent, FrameTimingSpan.OverlayCallback, FrameTimingOutcome.Failed));
        Write(buffer, Elapsed(7, 65, 66, 2, FrameTimingSource.DxgiPresent, FrameTimingSpan.OverlayCallback, FrameTimingOutcome.Test));
        Write(buffer, Elapsed(8, 70, 71, 2, FrameTimingSource.DxgiPresent, FrameTimingSpan.DxgiNativePresent) with { NativeStatus = 0 });
        Write(buffer, Elapsed(9, 75, 76, 2, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay, FrameTimingOutcome.Failed));

        var range = FrameTimingRange.CreateRecent(buffer.Read(1, At(0), At(80)), 0.08, 60);
        Assert.Equal(2, range.Publications);
        Assert.Equal(1, range.ReadyAttempts);
        Assert.Equal(3, range.CompletedReplays);
        Assert.Equal(2, range.UnknownReplayAges);
        Assert.Equal(1, range.UnknownReplaySnapshots);
        Assert.Equal<double>([12, 22, 23], range.MainUiAge.Milliseconds);
        Assert.Equal<FrameTimingSnapshotReuse>([new(17, 2), new(18, 0)], range.SnapshotReuse);
        Assert.Equal<FrameTimingOverlayOutcomeCount>(
            [new(FrameTimingSource.DxgiPresent, FrameTimingOutcome.Failed, 1),
             new(FrameTimingSource.DxgiPresent, FrameTimingOutcome.Test, 1)], range.OverlayOutcomes);
    }

    private static FrameTimingSummary ReadTimeline(FrameTimingBuffer buffer, Action? beforeRead = null)
    {
        Write(buffer, Entry(1, 10));
        Write(buffer, Entry(2, 20));
        Write(buffer, Entry(3, 40));
        Write(buffer, Entry(4, 50));
        Write(buffer, Entry(5, 100));
        Write(buffer, Elapsed(4, 50, 97, 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate));
        Write(buffer, Elapsed(6, 53, 96, 1, FrameTimingSource.KernelPresent, FrameTimingSpan.KernelPresent));
        Write(buffer, Elapsed(6, 55, 70, 1, FrameTimingSource.KernelPresent, FrameTimingSpan.Step, FrameTimingOutcome.Failed));
        Write(buffer, Elapsed(6, 72, 92, 1, FrameTimingSource.KernelPresent, FrameTimingSpan.KernelNativePresent));
        Write(buffer, Elapsed(7, 80, 83, 7, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay) with
        {
            SnapshotId = 17,
            SnapshotAgeTicks = Ticks(44),
        });
        beforeRead?.Invoke();
        return buffer.Read(sessionId: 1, startedTick: At(0), closedTick: At(120));
    }

    private static void Write(FrameTimingBuffer buffer, FrameTimingRecord record)
    {
        buffer.BeginWrite(out var sequence);
        buffer.Write(sequence, record);
    }

    private static FrameTimingCadenceView Source(FrameTimingRange range, FrameTimingSource source) =>
        Assert.Single(range.Cadence, series => series.Source == source);

    private static long At(int milliseconds) => Ticks(1000) + Ticks(milliseconds);

    private static long Ticks(int milliseconds) => Stopwatch.Frequency * milliseconds / 1000;

    private static FrameTimingRecord Entry(long id, int milliseconds) => new(
        id, At(milliseconds), At(milliseconds), 1, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate,
        FrameTimingRecordKind.Cadence);

    private static FrameTimingRecord Elapsed(
        long id, int start, int end, int thread, FrameTimingSource source, FrameTimingSpan span,
        FrameTimingOutcome outcome = FrameTimingOutcome.Completed) =>
        new(id, At(start), At(end), thread, source, span, FrameTimingRecordKind.Elapsed, outcome);
}
