using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Utility.Timing;

using Newtonsoft.Json;

using Xunit;

namespace Dalamud.Test.Utility;

public class FrameTimingRecorderTests
{
    [Fact]
    public void StartupPreferenceDefaultsOffAndUsesNormalAdmission()
    {
        var recorder = new FrameTimingRecorder();
        var existing = JsonConvert.DeserializeObject<DalamudConfiguration>("{\"FrameworkUpdateHitch\":60}")!;
        Assert.False(new DalamudConfiguration().EnableFrameTimingOnStartup);
        Assert.False(existing.EnableFrameTimingOnStartup);
        Assert.True(recorder.ApplyStartupPreference(existing.EnableFrameTimingOnStartup));
        Assert.Equal(0, CompleteUpdate(recorder));
        Assert.Null(recorder.ReadSummary());

        existing.EnableFrameTimingOnStartup = true;
        var restored = JsonConvert.DeserializeObject<DalamudConfiguration>(JsonConvert.SerializeObject(existing))!;
        Assert.True(recorder.ApplyStartupPreference(restored.EnableFrameTimingOnStartup));
        var update = BeginUpdate(recorder, out _);
        var live = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary());
        Assert.True(live.Collecting);
        Assert.Equal(Stopwatch.Frequency, live.ClockFrequency);
        Assert.Equal(FrameTimingRecordKind.Cadence, Assert.Single(live.Records).Kind);
        Assert.True(update.End() > 0);
        Assert.Single(live.Records);
        Assert.Equal(2, recorder.ReadSummary()!.Records.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SummaryCoverageStartsAfterPublicationAndKeepsTheFirstProbe(bool reuseBuffer)
    {
        var recorder = new FrameTimingRecorder(capacity: 32);
        if (reuseBuffer)
        {
            Assert.True(recorder.SetEnabled(true));
            Assert.True(CompleteUpdate(recorder) > 0);
            Assert.True(recorder.SetEnabled(false));
        }

        var probeEarliestTick = 0L;
        Assert.True(recorder.SetEnabled(
            true,
            beforeEnableCommit: () => Assert.Equal(0, CompleteUpdate(recorder)),
            afterEnableCommit: () =>
            {
                var publicationObservedTick = Stopwatch.GetTimestamp();
                // Require a later tick to distinguish the session start from a sample taken before publication.
                while ((probeEarliestTick = Stopwatch.GetTimestamp()) <= publicationObservedTick)
                    Thread.SpinWait(1);

                Assert.True(CompleteUpdate(recorder) > 0);
            }));

        var summary = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary());
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        var entry = Assert.Single(cadence.Entries);
        Assert.InRange(summary.StartedTick, probeEarliestTick, entry.StartTick);
        Assert.Equal(summary.StartedTick, summary.RetainedFromTick);
        Assert.Equal(2, summary.Records.Length);
        Assert.True(summary.CoverageComplete);
        Assert.True(cadence.CoverageComplete);
    }

    [Fact]
    public void ShutdownCanCloseAPublishedSessionBeforeEnableSamplesItsStart()
    {
        var recorder = new FrameTimingRecorder(beforeStopCommit: _ =>
        {
            var hookTick = Stopwatch.GetTimestamp();
            while (Stopwatch.GetTimestamp() <= hookTick)
                Thread.SpinWait(1);
        });

        Assert.True(recorder.SetEnabled(true, afterEnableCommit: recorder.Shutdown));

        var summary = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary());
        Assert.True(summary.StartedTick > 0);
        Assert.Equal(summary.StartedTick, summary.RetainedFromTick);
        Assert.Equal(summary.StartedTick, summary.AdmissionConfirmedThroughTick);
        Assert.True(summary.StartedTick < summary.CutoffTick);
        Assert.False(summary.Collecting);
        Assert.False(summary.CoverageComplete);
        Assert.Empty(summary.Records);
    }

    [Fact]
    public void DisableReenableAndShutdownKeepLateWritesInTheirOriginalBuffers()
    {
        var recorder = new FrameTimingRecorder(capacity: 32);
        Assert.True(recorder.SetEnabled(true));
        var first = BeginUpdate(recorder, out _);
        var firstId = recorder.ReadSummary()!.SessionId;
        Assert.True(recorder.SetEnabled(false));
        Assert.Equal(0, CompleteUpdate(recorder));

        Assert.True(recorder.SetEnabled(true));
        var second = BeginUpdate(recorder, out _);
        var secondId = recorder.ReadSummary()!.SessionId;
        Assert.NotEqual(firstId, secondId);
        Assert.True(recorder.SetEnabled(false));
        Assert.False(recorder.SetEnabled(true));
        Assert.Equal(secondId, recorder.ReadSummary()!.SessionId);

        first.Begin(FrameTimingSpan.FrameworkNativeUpdate).End();
        first.End();
        var old = recorder.ReadSummary(firstId)!;
        Assert.False(old.Collecting);
        Assert.Equal(3, old.Records.Length);
        Assert.Single(recorder.ReadSummary(secondId)!.Records);

        Assert.True(recorder.SetEnabled(true));
        Assert.Null(recorder.ReadSummary(firstId));
        var third = BeginUpdate(recorder, out _);
        var thirdId = recorder.ReadSummary()!.SessionId;
        recorder.Shutdown();
        Assert.False(recorder.SetEnabled(true));
        Assert.Equal(0, CompleteUpdate(recorder));
        second.End();
        third.End();
        Assert.Equal(2, recorder.ReadSummary(secondId)!.Records.Length);
        Assert.Equal(2, recorder.ReadSummary(thirdId)!.Records.Length);
        Assert.False(recorder.ReadSummary()!.Collecting);
    }

    [Fact]
    public void RepeatedRootCompletionRecordsOneOutcomeAndAllowsBufferReuse()
    {
        var recorder = new FrameTimingRecorder(capacity: 32);
        for (var session = 0; session < 2; session++)
        {
            Assert.True(recorder.SetEnabled(true));
            var update = BeginUpdate(recorder, out _);
            var scope = new FrameTimingSpanScope(update);
            var copy = scope;

            Assert.True(scope.End(nativeStatus: 1) > 0);
            Assert.Equal(0, scope.End(FrameTimingOutcome.Failed));
            Assert.Equal(0, copy.End(FrameTimingOutcome.Failed));
            Assert.Equal(0, update.End(FrameTimingOutcome.Failed));
            copy.Dispose();
            scope.Dispose();

            Assert.True(recorder.SetEnabled(false));
            var summary = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary());
            var elapsed = Assert.Single(summary.Records, record => record.Kind == FrameTimingRecordKind.Elapsed);
            Assert.Equal(FrameTimingOutcome.Completed, elapsed.Outcome);
            Assert.Equal(1, elapsed.NativeStatus);
        }

        Assert.True(recorder.SetEnabled(true));
    }

    [Fact]
    public void DuplicateRootCompletionDoesNotReleaseAnotherInFlightRoot()
    {
        var recorder = new FrameTimingRecorder(capacity: 32);
        Assert.True(recorder.SetEnabled(true));
        var completed = BeginUpdate(recorder, out _);
        var pending = BeginUpdate(recorder, out _);
        var originalId = recorder.ReadSummary()!.SessionId;
        var copy = completed;
        var child = completed.Begin(FrameTimingSpan.FrameworkNativeUpdate);
        Assert.True(completed.End() > 0);
        Assert.Equal(0, copy.End());

        Assert.True(recorder.SetEnabled(false));
        Assert.True(recorder.SetEnabled(true));
        var other = BeginUpdate(recorder, out _);
        Assert.True(recorder.SetEnabled(false));
        Assert.False(recorder.SetEnabled(true));

        Assert.True(pending.End() > 0);
        var original = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary(originalId));
        Assert.Equal(4, original.Records.Length);
        Assert.True(recorder.SetEnabled(true));
        Assert.Null(recorder.ReadSummary(originalId));
        Assert.Equal(0, child.End());
        Assert.True(other.End() > 0);
    }

    [Fact]
    public async Task ShutdownWhileReenablingPreservesTheStoppedSession()
    {
        var recorder = new FrameTimingRecorder(capacity: 32);
        Assert.True(recorder.SetEnabled(true));
        Assert.True(CompleteUpdate(recorder) > 0);
        Assert.True(recorder.SetEnabled(false));
        var previous = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary());
        Assert.Equal(2, previous.Records.Length);

        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = TestContext.Current.CancellationToken;
        var readyToCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseEnable = new ManualResetEventSlim();
        var enable = Task.Factory.StartNew(
            () => recorder.SetEnabled(true, beforeEnableCommit: () =>
            {
                readyToCommit.SetResult();
                Assert.True(releaseEnable.Wait(timeout, cancellationToken), "Re-enable was not released.");
            }),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        bool enabled;
        try
        {
            await readyToCommit.Task.WaitAsync(timeout, cancellationToken);
            Assert.Equal(0, CompleteUpdate(recorder));
            await Task.Run(recorder.Shutdown, cancellationToken).WaitAsync(timeout, cancellationToken);
            Assert.False(enable.IsCompleted);
        }
        finally
        {
            releaseEnable.Set();
            enabled = await enable.WaitAsync(timeout, CancellationToken.None);
        }

        Assert.False(enabled);
        Assert.False(recorder.SetEnabled(true));
        Assert.Equal(0, CompleteUpdate(recorder));

        var latest = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary());
        Assert.Equal(previous.SessionId, latest.SessionId);
        Assert.Equal(previous.CutoffTick, latest.CutoffTick);
        Assert.False(latest.Collecting);
        Assert.Equal<FrameTimingRecord>(previous.Records, latest.Records);
        var byId = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary(previous.SessionId));
        Assert.Equal<FrameTimingRecord>(previous.Records, byId.Records);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StopPreservesCadencePublishedBeforeDetachment(bool shutdown, bool enableDuringStop)
    {
        FrameTimingSummary? beforeStop = null;
        var recorder = new FrameTimingRecorder(capacity: 32, beforeStopCommit: stopping =>
        {
            var hookTick = Stopwatch.GetTimestamp();
            while (Stopwatch.GetTimestamp() <= hookTick)
                Thread.SpinWait(1);

            if (enableDuringStop)
                Assert.True(stopping.SetEnabled(true));

            Assert.True(CompleteUpdate(stopping) > 0);
            Assert.True(CompleteUpdate(stopping) > 0);
            beforeStop = Assert.IsType<FrameTimingSummary>(stopping.ReadSummary());
            Assert.True(beforeStop.Collecting);
            while (Stopwatch.GetTimestamp() <= beforeStop.CutoffTick)
                Thread.SpinWait(1);
        });

        if (!enableDuringStop)
        {
            Assert.True(recorder.SetEnabled(true));
            Assert.True(CompleteUpdate(recorder) > 0);
        }

        if (shutdown)
            recorder.Shutdown();
        else
            Assert.True(recorder.SetEnabled(false));

        Assert.NotNull(beforeStop);
        Assert.Equal(0, CompleteUpdate(recorder));
        var stopped = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary());
        Assert.Equal(beforeStop.SessionId, stopped.SessionId);
        Assert.Equal<FrameTimingRecord>(beforeStop.Records, stopped.Records);
        Assert.False(stopped.Collecting);
        Assert.Equal(0, stopped.DroppedRecords);

        var liveCadence = beforeStop.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        var stoppedCadence = stopped.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(enableDuringStop ? 2 : 3, liveCadence.Entries.Length);
        Assert.Equal(liveCadence.Entries.Length - 1, liveCadence.Intervals.Length);
        Assert.Equal<FrameTimingRecord>(liveCadence.Entries, stoppedCadence.Entries);
        Assert.Equal<FrameTimingInterval>(liveCadence.Intervals, stoppedCadence.Intervals);
        Assert.Equal(stoppedCadence.Entries[^1].StartTick, stopped.AdmissionConfirmedThroughTick);
        Assert.True(stopped.AdmissionConfirmedThroughTick < stopped.CutoffTick);
        Assert.False(stopped.CoverageComplete);
        Assert.False(stoppedCadence.CoverageComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadOverlappingStopUsesTheClosedCadenceBoundary(bool shutdown)
    {
        var recorder = new FrameTimingRecorder(capacity: 32);
        Assert.True(recorder.SetEnabled(true));
        CompleteUpdate(recorder);
        FrameTimingSummary? stopped = null;

        var summary = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary(beforeCutoffSampled: () =>
        {
            var update = BeginUpdate(recorder, out _);
            if (shutdown)
                recorder.Shutdown();
            else
                Assert.True(recorder.SetEnabled(false));

            stopped = Assert.IsType<FrameTimingSummary>(recorder.ReadSummary());
            while (Stopwatch.GetTimestamp() <= stopped.CutoffTick)
                Thread.SpinWait(1);

            Assert.True(update.End() > stopped.CutoffTick);
            if (!shutdown)
                Assert.True(recorder.SetEnabled(true));
        }));

        Assert.NotNull(stopped);
        Assert.Equal(stopped.SessionId, summary.SessionId);
        Assert.Equal(stopped.CutoffTick, summary.CutoffTick);
        Assert.Equal(stopped.AdmissionConfirmedThroughTick, summary.AdmissionConfirmedThroughTick);
        Assert.False(summary.Collecting);
        Assert.Equal(summary.AdmissionConfirmedThroughTick == summary.CutoffTick, summary.CoverageComplete);
        Assert.Equal(4, summary.Records.Length);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(summary.CoverageComplete, cadence.CoverageComplete);
        Assert.Equal(2, cadence.Entries.Length);
        Assert.Single(cadence.Intervals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadCutoffIsCompleteOnlyWhenAdmissionWasConfirmed(bool cutoffInsideCloseBounds)
    {
        var buffer = new FrameTimingBuffer(capacity: 2);
        var tick = buffer.BeginWrite(out var sequence);
        buffer.Write(sequence, Entry(sequence, tick));
        while (Stopwatch.GetTimestamp() <= tick)
            Thread.SpinWait(1);

        var openThroughTick = 0L;
        var closedByTick = 0L;

        var summary = buffer.Read(1, tick, () => (false, openThroughTick, closedByTick), afterCutoffSampled: _ =>
        {
            var hookTick = Stopwatch.GetTimestamp();
            long sampledThrough;
            while ((sampledThrough = Stopwatch.GetTimestamp()) <= hookTick)
                Thread.SpinWait(1);

            while ((closedByTick = Stopwatch.GetTimestamp()) <= sampledThrough)
                Thread.SpinWait(1);

            // Place the read cutoff either inside the close bounds or within confirmed admission.
            openThroughTick = cutoffInsideCloseBounds ? tick : sampledThrough;
        });

        Assert.InRange(summary.CutoffTick, tick + 1, closedByTick - 1);
        Assert.Equal(!cutoffInsideCloseBounds, summary.Collecting);
        Assert.Equal(cutoffInsideCloseBounds ? tick : summary.CutoffTick, summary.AdmissionConfirmedThroughTick);
        Assert.Equal(!cutoffInsideCloseBounds, summary.CoverageComplete);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(!cutoffInsideCloseBounds, cadence.CoverageComplete);
        Assert.Single(cadence.Entries);
    }

    [Fact]
    public void DetachedAdmissionWithPendingCloseTickReportsIncompleteCoverage()
    {
        var buffer = new FrameTimingBuffer(capacity: 2);
        foreach (var tick in new long[] { 10, 20 })
        {
            buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, Entry(sequence, tick));
        }

        var summary = buffer.Read(1, 1, () => (AdmitsProbes: false, OpenThroughTick: 0, ClosedByTick: 0));

        Assert.False(summary.Collecting);
        Assert.False(summary.CoverageComplete);
        Assert.Equal(2, summary.Records.Length);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.False(cadence.CoverageComplete);
        Assert.Equal(2, cadence.Entries.Length);
        Assert.Empty(cadence.Intervals);
    }

    [Fact]
    public void LateCompletionDoesNotExtendStoppedCadenceCoverage()
    {
        var recorder = new FrameTimingRecorder();
        Assert.True(recorder.SetEnabled(true));
        var update = BeginUpdate(recorder, out _);
        Assert.True(recorder.SetEnabled(false));
        var stopped = recorder.ReadSummary()!;

        while (Stopwatch.GetTimestamp() <= stopped.CutoffTick)
            Thread.SpinWait(1);

        var ended = update.End();
        var summary = recorder.ReadSummary()!;
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        var elapsed = Assert.Single(summary.Records, record => record.Kind == FrameTimingRecordKind.Elapsed);
        Assert.Equal(ended, elapsed.EndTick);
        Assert.True(ended > stopped.CutoffTick);
        Assert.False(summary.Collecting);
        Assert.Equal(stopped.CutoffTick, summary.CutoffTick);
        Assert.Equal(stopped.AdmissionConfirmedThroughTick, summary.AdmissionConfirmedThroughTick);
        Assert.Equal(summary.AdmissionConfirmedThroughTick == summary.CutoffTick, summary.CoverageComplete);
        Assert.Equal(summary.CoverageComplete, cadence.CoverageComplete);
        Assert.Single(cadence.Entries);
    }

    [Fact]
    public void CadenceAwaitingAdmissionMakesReadsIncompleteAndCancellationLeavesNoGap()
    {
        var buffer = new FrameTimingBuffer(capacity: 1);
        var tick = buffer.BeginCadenceWrite();
        buffer.WriteCadence(Entry(1, tick));
        buffer.BeginCadenceWrite();

        var pending = buffer.Read(1, tick, closedTick: 0);
        Assert.False(pending.CoverageComplete);
        Assert.Single(pending.Records);

        buffer.CancelCadenceWrite();
        var settled = buffer.Read(1, tick, closedTick: 0);
        Assert.True(settled.CoverageComplete);
        Assert.Equal(0, settled.DroppedRecords);
        Assert.Equal(0, settled.EvictedRecords);
        Assert.Equal(tick, Assert.Single(settled.Records).StartTick);
    }

    [Fact]
    public void StoppedCadenceExcludesEntriesSampledAfterClose()
    {
        var buffer = new FrameTimingBuffer(capacity: 3);
        foreach (var tick in new long[] { 10, 20, 40 })
        {
            buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, Entry(sequence, tick));
        }

        var summary = buffer.Read(1, startedTick: 1, closedTick: 30);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(new long[] { 10, 20, 40 }, summary.Records.Select(record => record.StartTick));
        Assert.Equal(new long[] { 10, 20 }, cadence.Entries.Select(record => record.StartTick));
        Assert.Equal(new FrameTimingInterval(10, 20), Assert.Single(cadence.Intervals));
    }

    [Fact]
    public void LateWritesCanEvictAllStoppedCadenceCoverage()
    {
        const long closedTick = 30;
        var buffer = new FrameTimingBuffer(capacity: 1);
        var threadId = Environment.CurrentManagedThreadId;
        buffer.BeginWrite(out var entry);
        buffer.Write(entry, Entry(entry, 10));
        buffer.BeginWrite(out var managed);
        buffer.Write(managed, new(entry, closedTick, 40, threadId,
            FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkManagedTick, FrameTimingRecordKind.Elapsed));
        buffer.BeginWrite(out var native);
        buffer.Write(native, new(entry, 50, 60, threadId,
            FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkNativeUpdate, FrameTimingRecordKind.Elapsed));

        var summary = buffer.Read(1, startedTick: 1, closedTick);
        Assert.Equal(closedTick, summary.CutoffTick);
        Assert.Equal(summary.CutoffTick, summary.RetainedFromTick);
        Assert.Equal(60, Assert.Single(summary.Records).EndTick);
        Assert.False(summary.CoverageComplete);
        Assert.All(summary.Cadence, series =>
        {
            Assert.False(series.CoverageComplete);
            Assert.Empty(series.Entries);
            Assert.Empty(series.Intervals);
        });
    }

    [Fact]
    public void ConcurrentAndReentrantOperationsRetainTheirOwnRecords()
    {
        var recorder = new FrameTimingRecorder(capacity: 512);
        Assert.True(recorder.SetEnabled(true));
        Parallel.For(0, 64, iteration =>
        {
            var update = BeginUpdate(recorder, out _);
            var nested = recorder.Begin(FrameTimingSource.KernelPresent, FrameTimingSpan.KernelPresent, out _);
            nested.Begin(FrameTimingSpan.KernelNativePresent).End();
            nested.End();
            update.End();
        });

        var summary = recorder.ReadSummary()!;
        Assert.True(summary.CoverageComplete);
        Assert.Equal(320, summary.Records.Length);
        Assert.All(summary.Records.GroupBy(record => record.OperationId), operation =>
        {
            var cadence = Assert.Single(operation, record => record.Kind == FrameTimingRecordKind.Cadence);
            Assert.All(operation, record =>
            {
                Assert.Equal(cadence.Source, record.Source);
                Assert.Equal(cadence.ThreadId, record.ThreadId);
                Assert.InRange(record.StartTick, cadence.StartTick, record.EndTick);
            });
        });
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.KernelPresent);
        Assert.Equal(64, cadence.Entries.Length);
        Assert.Equal(63, cadence.Intervals.Length);
        Assert.All(cadence.Intervals, interval => Assert.True(interval.StartTick <= interval.EndTick));
    }

    [Fact]
    public void EvictionShortensTheRangeWithoutBridgingMissingHistory()
    {
        var recorder = new FrameTimingRecorder(capacity: 4);
        Assert.True(recorder.SetEnabled(true));
        for (var frame = 0; frame < 5; frame++)
        {
            CompleteUpdate(recorder);
            // Advance the clock so eviction and retained entries have distinct start ticks.
            var boundary = Stopwatch.GetTimestamp();
            while (Stopwatch.GetTimestamp() == boundary)
                Thread.SpinWait(1);
        }

        var summary = recorder.ReadSummary()!;
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.True(summary.CoverageComplete);
        Assert.Equal(6, summary.EvictedRecords);
        Assert.Equal(0, summary.DroppedRecords);
        Assert.True(summary.RetainedFromTick > summary.StartedTick);
        Assert.Equal(2, cadence.Entries.Length);
        var interval = Assert.Single(cadence.Intervals);
        Assert.Equal(cadence.Entries[0].StartTick, interval.StartTick);
        Assert.Equal(cadence.Entries[1].StartTick, interval.EndTick);
    }

    [Theory]
    [InlineData(false, 0L)]
    [InlineData(false, 95L)]
    [InlineData(true, 0L)]
    [InlineData(true, 95L)]
    public void EvictingLongSpanPreservesLaterStartsAndCadence(bool overwriteDuringRead, long closedTick)
    {
        var buffer = new FrameTimingBuffer(capacity: 4);
        var longSpan = new FrameTimingRecord(0, 10, 100, Environment.CurrentManagedThreadId,
            FrameTimingSource.KernelPresent, FrameTimingSpan.KernelPresent, FrameTimingRecordKind.Elapsed);
        foreach (var record in new[] { longSpan, longSpan with { StartTick = 20, EndTick = 105 }, Entry(0, 70), Entry(0, 90) })
        {
            buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, record with { OperationId = sequence });
        }

        if (!overwriteDuringRead)
            OverwriteOldest();

        var summary = buffer.Read(1, startedTick: 1, closedTick, overwriteDuringRead ? _ => OverwriteOldest() : null);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(11, summary.RetainedFromTick);
        Assert.Equal(overwriteDuringRead ? 0 : 1, summary.EvictedRecords);
        Assert.Equal(overwriteDuringRead ? 1 : 0, summary.UnreadRecords);
        Assert.Equal(overwriteDuringRead ? 3 : 4, summary.Records.Length);
        Assert.Equal(new long[] { 20, 70, 90 }, summary.Records.Take(3).Select(record => record.StartTick));
        Assert.Equal(105, summary.Records[0].EndTick);
        Assert.Equal(!overwriteDuringRead, summary.CoverageComplete);
        Assert.Equal(!overwriteDuringRead, cadence.CoverageComplete);
        Assert.Equal(new FrameTimingInterval(70, 90), Assert.Single(cadence.Intervals));

        void OverwriteOldest()
        {
            var tick = buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, Entry(sequence, tick) with { Kind = FrameTimingRecordKind.Point });
        }
    }

    [Fact]
    public void PublicationAtSampledCutoffBelongsToTheNextCadenceWindow()
    {
        var buffer = new FrameTimingBuffer(capacity: 3);
        buffer.BeginCadenceWrite();
        buffer.WriteCadence(Entry(1, 10));
        buffer.BeginCadenceWrite();
        buffer.WriteCadence(Entry(2, 20));

        var live = buffer.Read(1, startedTick: 1, closedTick: 0, afterCutoffSampled: cutoff =>
        {
            // Publish after the reader samples the sequence, using the reader's cutoff tick.
            buffer.BeginCadenceWrite();
            buffer.WriteCadence(Entry(3, cutoff));
        });

        while (Stopwatch.GetTimestamp() <= live.CutoffTick)
            Thread.SpinWait(1);

        var sameCutoff = buffer.Read(1, startedTick: 1, closedTick: live.CutoffTick);
        var later = buffer.Read(1, startedTick: 1, closedTick: live.CutoffTick + 1);
        foreach (var summary in new[] { live, sameCutoff })
        {
            var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
            Assert.Equal(live.CutoffTick, summary.CutoffTick);
            Assert.True(summary.CoverageComplete);
            Assert.True(cadence.CoverageComplete);
            Assert.Equal(new long[] { 10, 20 }, cadence.Entries.Select(record => record.StartTick));
            Assert.Equal(new FrameTimingInterval(10, 20), Assert.Single(cadence.Intervals));
        }

        Assert.Equal(2, live.Records.Length);
        Assert.Equal(live.CutoffTick, sameCutoff.Records[^1].StartTick);
        var laterCadence = later.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.True(laterCadence.CoverageComplete);
        Assert.Equal(live.CutoffTick, laterCadence.Entries[^1].StartTick);
        Assert.Equal(new FrameTimingInterval(20, live.CutoffTick), laterCadence.Intervals[^1]);
    }

    [Fact]
    public void RolloverDuringReadPreservesCadenceBeyondTheEvictedTick()
    {
        var buffer = new FrameTimingBuffer(capacity: 5);
        // Put timestamps out of sequence order to model a writer paused before sampling its tick.
        foreach (var tick in new long[] { 30, 20, 40, 50, 60 })
        {
            buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, Entry(sequence, tick));
        }

        var summary = buffer.Read(1, startedTick: 1, closedTick: 0, afterCutoffSampled: _ =>
        {
            var tick = buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, Entry(sequence, tick));
        });

        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(1, summary.UnreadRecords);
        Assert.Equal(0, summary.DroppedRecords);
        Assert.Equal(0, summary.EvictedRecords);
        Assert.False(summary.CoverageComplete);
        Assert.False(cadence.CoverageComplete);
        Assert.Equal<FrameTimingInterval>([new(40, 50), new(50, 60)], cadence.Intervals);
        Assert.Equal(31, summary.RetainedFromTick);
        Assert.Equal(new long[] { 40, 50, 60 }, cadence.Entries.Select(record => record.StartTick));
        Assert.Equal(new long[] { 40, 50, 60 }, summary.Records.Select(record => record.StartTick));
    }

    [Fact]
    public void UncommittedRolloverDuringReadKeepsCadenceUncertain()
    {
        var buffer = new FrameTimingBuffer(capacity: 3);
        foreach (var tick in new long[] { 20, 10, 30 })
        {
            buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, Entry(sequence, tick));
        }

        var summary = buffer.Read(1, startedTick: 1, closedTick: 0, afterCutoffSampled: _ =>
        {
            buffer.BeginWrite(out var sequence);
            // Mark the replacement in progress with its eviction tick still unpublished.
            SetFirstSlotStamp(buffer, -sequence);
        });

        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(1, summary.UnreadRecords);
        Assert.False(summary.CoverageComplete);
        Assert.False(cadence.CoverageComplete);
        Assert.Equal(new long[] { 10, 30 }, cadence.Entries.Select(record => record.StartTick));
        Assert.Empty(cadence.Intervals);
    }

    [Fact]
    public void DelayedWriterCannotOverwriteNewerDataOrCreateAFalseCadenceInterval()
    {
        var buffer = new FrameTimingBuffer(capacity: 2);
        var started = Stopwatch.GetTimestamp();
        var lost = buffer.BeginWrite(out var first);
        buffer.BeginWrite(out var second);
        buffer.Write(second, Entry(second, started));
        var observed = buffer.BeginWrite(out var third);
        buffer.Write(third, Entry(third, observed));

        var pending = buffer.Read(1, started, 0);
        Assert.False(pending.CoverageComplete);
        Assert.Empty(pending.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate).Intervals);

        buffer.Write(first, Entry(first, lost));
        var summary = buffer.Read(1, started, 0);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.False(cadence.CoverageComplete);
        Assert.Equal(1, summary.DroppedRecords);
        Assert.Equal(new[] { second, third }, cadence.Entries.Select(record => record.OperationId).Order());
        Assert.Empty(cadence.Intervals);
    }

    [Fact]
    public void LocalizedDropPreservesIntervalsOnBothSides()
    {
        var buffer = new FrameTimingBuffer(capacity: 4);
        buffer.BeginWrite(out var delayed);
        foreach (var tick in new long[] { 10, 20, 60, 70 })
        {
            buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, Entry(sequence, tick));
        }

        // Attempt the overtaken write with a tick between the two retained pairs.
        buffer.Write(delayed, Entry(delayed, 50));

        var summary = buffer.Read(1, startedTick: 1, closedTick: 0);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(1, summary.DroppedRecords);
        Assert.Equal(0, summary.EvictedRecords);
        Assert.Equal(0, summary.UnreadRecords);
        Assert.False(summary.CoverageComplete);
        Assert.False(cadence.CoverageComplete);
        Assert.Equal<FrameTimingInterval>([new(10, 20), new(60, 70)], cadence.Intervals);
    }

    [Fact]
    public void DroppedElapsedSpanDoesNotBreakCallbackCadence()
    {
        var buffer = WriteWithDelayedDrops(
            4,
            [Entry(0, 10), Entry(0, 20), Entry(0, 60), Entry(0, 70)],
            new FrameTimingRecord(0, 30, 50, 1, FrameTimingSource.KernelPresent,
                FrameTimingSpan.KernelPresent, FrameTimingRecordKind.Elapsed));

        var summary = buffer.Read(1, startedTick: 1, closedTick: 0);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(1, summary.DroppedRecords);
        Assert.Equal(0, summary.UnreadRecords);
        Assert.Equal(0, summary.EvictedRecords);
        Assert.False(summary.CoverageComplete);
        Assert.Equal<FrameTimingInterval>([new(10, 20), new(20, 60), new(60, 70)], cadence.Intervals);
        Assert.All(summary.Cadence, series => Assert.True(series.CoverageComplete));
    }

    [Theory]
    [InlineData(5L, true)]
    [InlineData(11L, false)]
    public void DroppedElapsedSpanCoverageUsesRetainedStart(long droppedStartTick, bool coverageComplete)
    {
        var buffer = WriteWithDelayedDrops(
            2,
            [Entry(0, 10), Entry(0, 20), Entry(0, 30)],
            new FrameTimingRecord(0, droppedStartTick, 100, 1, FrameTimingSource.KernelPresent,
                FrameTimingSpan.KernelPresent, FrameTimingRecordKind.Elapsed));

        var summary = buffer.Read(1, startedTick: 1, closedTick: 0);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(11, summary.RetainedFromTick);
        Assert.Equal(1, summary.DroppedRecords);
        Assert.Equal(0, summary.UnreadRecords);
        Assert.Equal(new long[] { 20, 30 }, summary.Records.Select(record => record.StartTick));
        Assert.Equal(coverageComplete, summary.CoverageComplete);
        Assert.True(cadence.CoverageComplete);
        Assert.Equal(new FrameTimingInterval(20, 30), Assert.Single(cadence.Intervals));
    }

    [Fact]
    public void DroppedCadenceOnlyBreaksIntervalsInItsOwnSource()
    {
        var kernelEntry = Entry(0, 10) with { Source = FrameTimingSource.KernelPresent, Span = FrameTimingSpan.KernelPresent };
        var buffer = WriteWithDelayedDrops(
            4,
            [Entry(0, 10), Entry(0, 60), kernelEntry, kernelEntry with { StartTick = 60, EndTick = 60 }],
            kernelEntry with { StartTick = 50, EndTick = 50 });

        var summary = buffer.Read(1, startedTick: 1, closedTick: 0);
        var framework = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        var kernel = summary.Cadence.Single(series => series.Source == FrameTimingSource.KernelPresent);
        Assert.Equal(1, summary.DroppedRecords);
        Assert.True(framework.CoverageComplete);
        Assert.Equal(new FrameTimingInterval(10, 60), Assert.Single(framework.Intervals));
        Assert.False(kernel.CoverageComplete);
        Assert.Empty(kernel.Intervals);
    }

    [Fact]
    public void DropsOutsideStoppedCadenceWindowDoNotAffectItsCoverage()
    {
        var buffer = WriteWithDelayedDrops(
            3,
            [Entry(0, 9), Entry(0, 10), Entry(0, 20), Entry(0, 30)],
            Entry(0, 5),
            Entry(0, 30),
            Entry(0, 40),
            new FrameTimingRecord(0, 25, 45, 1, FrameTimingSource.FrameworkUpdate,
                FrameTimingSpan.FrameworkUpdate, FrameTimingRecordKind.Elapsed));

        var summary = buffer.Read(1, startedTick: 1, closedTick: 30);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(4, summary.DroppedRecords);
        Assert.Equal(0, summary.UnreadRecords);
        Assert.Equal(10, summary.RetainedFromTick);
        Assert.False(summary.CoverageComplete);
        Assert.True(cadence.CoverageComplete);
        Assert.Equal(new long[] { 10, 20 }, cadence.Entries.Select(record => record.StartTick));
        Assert.Equal(new FrameTimingInterval(10, 20), Assert.Single(cadence.Intervals));

        var later = buffer.Read(1, startedTick: 1, closedTick: 31);
        var laterCadence = later.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.False(laterCadence.CoverageComplete);
        Assert.Equal(new long[] { 10, 20, 30 }, laterCadence.Entries.Select(record => record.StartTick));
        Assert.Equal(new FrameTimingInterval(10, 20), Assert.Single(laterCadence.Intervals));
    }

    [Fact]
    public void SeparateCadenceDropsPreserveIntervalsBetweenThem()
    {
        var buffer = WriteWithDelayedDrops(
            6,
            [Entry(0, 10), Entry(0, 20), Entry(0, 30), Entry(0, 40), Entry(0, 50), Entry(0, 60)],
            Entry(0, 55),
            Entry(0, 20));

        var summary = buffer.Read(1, startedTick: 1, closedTick: 0);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(2, summary.DroppedRecords);
        Assert.False(cadence.CoverageComplete);
        Assert.Equal<FrameTimingInterval>([new(30, 40), new(40, 50)], cadence.Intervals);
    }

    [Fact]
    public void CadenceDropHistoryOverflowUsesSourceBoundsAndResetsOnReuse()
    {
        var buffer = WriteWithDelayedDrops(
            3,
            [Entry(0, 10), Entry(0, 20), Entry(0, 30)],
            Entry(0, 40),
            Entry(0, 50),
            Entry(0, 60),
            Entry(0, 25));

        var summary = buffer.Read(1, startedTick: 1, closedTick: 31);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(4, summary.DroppedRecords);
        Assert.False(cadence.CoverageComplete);
        Assert.Equal(new FrameTimingInterval(10, 20), Assert.Single(cadence.Intervals));
        Assert.All(summary.Cadence.Where(series => series.Source != FrameTimingSource.FrameworkUpdate),
            series => Assert.True(series.CoverageComplete));

        var earlier = buffer.Read(1, startedTick: 1, closedTick: 25);
        Assert.All(earlier.Cadence, series => Assert.True(series.CoverageComplete));

        var successor = buffer.CreateSuccessor();
        var retained = buffer.Read(1, startedTick: 1, closedTick: 31);
        Assert.Equal<FrameTimingRecord>(summary.Records, retained.Records);
        Assert.Equal(summary.DroppedRecords, retained.DroppedRecords);
        var empty = successor.Read(2, startedTick: 1, closedTick: 30);
        Assert.Empty(empty.Records);
        Assert.Equal(0, empty.DroppedRecords);
        Assert.Equal(0, empty.UnreadRecords);

        successor.BeginWrite(out var first);
        var entry = Entry(first, 25);
        successor.Write(first, entry);
        var reused = successor.Read(2, startedTick: 1, closedTick: 30);
        Assert.Equal(entry, Assert.Single(reused.Records));
        Assert.Equal(0, reused.EvictedRecords);
        Assert.True(reused.CoverageComplete);
        Assert.All(reused.Cadence, series => Assert.True(series.CoverageComplete));
    }

    [Fact]
    public void DropInsideRetainedRingPreservesAdjacentIntervals()
    {
        var buffer = new FrameTimingBuffer(capacity: 6);
        buffer.BeginWrite(out var claimed);
        // Mark the slot occupied to model a writer paused after claiming it.
        SetFirstSlotStamp(buffer, -claimed);
        foreach (var tick in new long[] { 10, 20, 30, 70, 80, 60 })
        {
            buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, Entry(sequence, tick));
        }

        var pending = buffer.Read(1, startedTick: 1, closedTick: 0);
        Assert.Equal(1, pending.DroppedRecords);
        Assert.False(pending.CoverageComplete);
        Assert.Empty(pending.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate).Intervals);

        // Reset the stamp to complete the paused write through normal publication.
        SetFirstSlotStamp(buffer, 0);
        buffer.Write(claimed, Entry(claimed, 15));

        var summary = buffer.Read(1, startedTick: 1, closedTick: 0);
        var cadence = summary.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        Assert.Equal(1, summary.DroppedRecords);
        Assert.Equal(0, summary.EvictedRecords);
        Assert.Equal(0, summary.UnreadRecords);
        Assert.False(summary.CoverageComplete);
        Assert.False(cadence.CoverageComplete);
        Assert.Equal(new long[] { 10, 15, 20, 30, 70, 80 }, cadence.Entries.Select(record => record.StartTick));
        Assert.Equal<FrameTimingInterval>([new(10, 15), new(15, 20), new(20, 30), new(70, 80)], cadence.Intervals);
    }

    // Reserve the delayed writes first so the observed writes claim their slots before publication.
    private static FrameTimingBuffer WriteWithDelayedDrops(int capacity, FrameTimingRecord[] observed, params FrameTimingRecord[] drops)
    {
        var buffer = new FrameTimingBuffer(capacity);
        var delayed = new long[drops.Length];
        for (var i = 0; i < delayed.Length; i++)
            buffer.BeginWrite(out delayed[i]);

        foreach (var record in observed)
        {
            buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, record with { OperationId = sequence });
        }

        for (var i = 0; i < delayed.Length; i++)
            buffer.Write(delayed[i], drops[i] with { OperationId = delayed[i] });

        return buffer;
    }

    private static void SetFirstSlotStamp(FrameTimingBuffer buffer, long stamp)
    {
        var slots = (Array)typeof(FrameTimingBuffer).GetField("slots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(buffer)!;
        var slot = slots.GetValue(0)!;
        slot.GetType().GetField("Stamp")!.SetValue(slot, stamp);
        slots.SetValue(slot, 0);
    }

    private static FrameTimingProbe BeginUpdate(FrameTimingRecorder recorder, [UnscopedRef] out FrameTimingProbe.Admission root) =>
        recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out root);

    private static long CompleteUpdate(FrameTimingRecorder recorder) => BeginUpdate(recorder, out _).End();

    private static FrameTimingRecord Entry(long operationId, long tick) =>
        new(operationId, tick, tick, Environment.CurrentManagedThreadId, FrameTimingSource.FrameworkUpdate,
            FrameTimingSpan.FrameworkUpdate, FrameTimingRecordKind.Cadence);
}
