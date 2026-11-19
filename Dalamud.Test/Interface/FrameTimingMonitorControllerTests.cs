using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Interface.Internal;
using Dalamud.Utility;
using Dalamud.Utility.Timing;

using Xunit;

namespace Dalamud.Test.Interface;

/// <summary>Exercises collection controls and asynchronous readout with a dedicated recorder.</summary>
public class FrameTimingMonitorControllerTests
{
    private static readonly FieldInfo HitchJob =
        typeof(FrameTimingMonitorController).GetField("hitchJob", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo ReadoutJob =
        typeof(FrameTimingMonitorController).GetField("readoutJob", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Fact]
    public async Task ClosingTheReadoutKeepsTheSessionAndTheStartupPreferenceIsIndependent()
    {
        var configuration = new DalamudConfiguration();
        var recorder = new FrameTimingRecorder(capacity: 32);
        var controller = new FrameTimingMonitorController(configuration, recorder, () => FrameTimingSource.ReShadeOverlay);
        try
        {
            controller.RequestReadout(5, 60);
            Assert.False(controller.IsEnabled);
            Assert.False(controller.EnableOnStartup);
            Assert.Equal(FrameTimingSource.ReShadeOverlay, controller.ActiveOverlaySource);

            controller.EnableOnStartup = true;
            Assert.True(configuration.EnableFrameTimingOnStartup);
            Assert.False(controller.IsEnabled);

            controller.SetEnabled(true);
            await UpdateUntilAsync(controller, () => !controller.IsChangingCollection);
            Assert.True(controller.IsEnabled);
            controller.EnableOnStartup = false;
            Assert.False(configuration.EnableFrameTimingOnStartup);
            Assert.True(controller.IsEnabled);

            RecordUpdate(recorder);
            await UpdateUntilAsync(controller, () => Entries(controller) == 1);
            var sessionId = controller.Readout!.Summary.SessionId;
            controller.StopReadout();
            Assert.Null(controller.Readout);
            Assert.True(controller.IsEnabled);

            RecordUpdate(recorder);
            await UpdateUntilAsync(controller, () => Entries(controller) == 2);
            Assert.Equal(sessionId, controller.Readout!.Summary.SessionId);

            controller.SetEnabled(false);
            await UpdateUntilAsync(controller, () => !controller.IsChangingCollection && controller.Readout?.Summary.IsFinal == true);
            Assert.False(controller.IsEnabled);
            Assert.False(controller.Readout!.Summary.Collecting);
            Assert.Equal(sessionId, controller.Readout.Summary.SessionId);
            Assert.Equal(2, Entries(controller));
        }
        finally
        {
            controller.StopReadout();
            recorder.Shutdown();
        }
    }

    [Fact]
    public async Task TurningCollectionOffClosesAdmissionWhileTheLifecycleLockIsHeld()
    {
        var recorder = new FrameTimingRecorder(capacity: 32);
        var controller = new FrameTimingMonitorController(new DalamudConfiguration(), recorder, () => FrameTimingSource.None);
        var timeout = TimeSpan.FromSeconds(10);
        var cancellationToken = TestContext.Current.CancellationToken;
        var admissionPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseEnable = new ManualResetEventSlim();
        // Keep the lifecycle lock held after admission opens to verify that stopping bypasses it.
        var enable = Task.Factory.StartNew(
            () => recorder.SetEnabled(true, afterEnableCommit: () =>
            {
                admissionPublished.SetResult();
                Assert.True(releaseEnable.Wait(timeout, cancellationToken), "Enable was not released.");
            }),
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        long beforeStop;
        long afterStop;
        try
        {
            await admissionPublished.Task.WaitAsync(timeout, cancellationToken);
            Assert.True(RecordUpdate(recorder) > 0);

            beforeStop = Stopwatch.GetTimestamp();
            controller.SetEnabled(false);
            afterStop = Stopwatch.GetTimestamp();

            Assert.False(controller.IsEnabled);
            Assert.False(controller.IsChangingCollection);
            Assert.Equal(0, RecordUpdate(recorder));
            Assert.False(enable.IsCompleted);
        }
        finally
        {
            releaseEnable.Set();
            Assert.True(await enable.WaitAsync(timeout, CancellationToken.None));
        }

        controller.RequestReadout(5, 60);
        await Assert.IsAssignableFrom<Task>(ReadoutJob.GetValue(controller)).WaitAsync(timeout, cancellationToken);
        controller.RequestReadout(5, 60);
        var stopped = Assert.IsType<FrameTimingSummary>(controller.Readout?.Summary);
        Assert.True(stopped.IsFinal);
        Assert.False(stopped.Collecting);
        Assert.InRange(stopped.CutoffTick, beforeStop, afterStop);
        Assert.Equal(1, Entries(controller));
    }

    [Fact]
    public async Task ChangedSettingsShareTheRefreshIntervalAndASupersededViewCanBeRequestedAgain()
    {
        var recorder = new FrameTimingRecorder(capacity: 32);
        var clock = new ManualClock();
        var controller = new FrameTimingMonitorController(new DalamudConfiguration(), recorder, () => FrameTimingSource.None, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            Assert.True(recorder.SetEnabled(true));
            RecordUpdate(recorder);
            Assert.True(recorder.SetEnabled(false));
            await UpdateUntilAsync(controller, () => controller.Readout?.Summary.IsFinal == true);
            var initialRange = controller.Readout!;

            controller.RequestReadout(7, 60);
            Assert.Null(controller.PlotReadout);
            controller.RequestReadout(5, 60, includeUncertainStopBoundary: true);
            Assert.Null(controller.PlotReadout);
            controller.RequestReadout(8, 180);
            Assert.Null(controller.PlotReadout);
            Assert.Same(initialRange.Summary, controller.LatestSummary);
            Assert.Null(ReadoutJob.GetValue(controller));

            clock.AdvanceRefreshInterval();
            controller.RequestReadout(8, 180);
            await Assert.IsAssignableFrom<Task>(ReadoutJob.GetValue(controller)).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            controller.RequestReadout(5, 60);
            Assert.Same(initialRange, controller.Readout);

            controller.RequestReadout(8, 180);
            Assert.Null(ReadoutJob.GetValue(controller));
            clock.AdvanceRefreshInterval();
            controller.RequestReadout(8, 180);
            await Assert.IsAssignableFrom<Task>(ReadoutJob.GetValue(controller)).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            controller.RequestReadout(8, 180);
            Assert.Equal(1000.0 / 180, controller.Readout!.BudgetMilliseconds);
            Assert.Same(initialRange.Summary, controller.Readout.Summary);
        }
        finally
        {
            controller.StopReadout();
            recorder.Shutdown();
        }
    }

    [Fact]
    public async Task FailedSettingsRefreshKeepsTheOldPlotHiddenAndDoesNotReportAnErrorForNewSettings()
    {
        var recorder = new FrameTimingRecorder(capacity: 32);
        var clock = new ManualClock();
        var controller = new FrameTimingMonitorController(new DalamudConfiguration(), recorder, () => FrameTimingSource.None, clock);
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            Assert.True(recorder.SetEnabled(true));
            RecordUpdate(recorder);
            Assert.True(recorder.SetEnabled(false));
            await UpdateUntilAsync(controller, () => controller.Readout?.Summary.IsFinal == true);
            var original = controller.PlotReadout!;

            // Use a zero target rate to force range construction to fail in the worker.
            clock.AdvanceRefreshInterval();
            controller.RequestReadout(5, 0);
            await Assert.IsAssignableFrom<Task>(ReadoutJob.GetValue(controller)).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            controller.RequestReadout(5, 0);
            Assert.NotNull(controller.ReadoutError);
            Assert.Null(controller.PlotReadout);
            Assert.False(controller.HasConfirmedNoSession);

            controller.RequestReadout(5, 120);
            Assert.Null(controller.ReadoutError);
            Assert.Null(controller.PlotReadout);

            controller.RequestReadout(5, 60);
            Assert.Same(original, controller.PlotReadout);
        }
        finally
        {
            controller.StopReadout();
            recorder.Shutdown();
        }
    }

    [Fact]
    public async Task ClosingTheReadoutDiscardsQueuedHitchAnalysisBeforeItStarts()
    {
        var recorder = new FrameTimingRecorder(capacity: 32);
        var scheduler = new ThreadBoundTaskScheduler();
        var controller = new FrameTimingMonitorController(
            new DalamudConfiguration(), recorder, () => FrameTimingSource.None, hitchScheduler: scheduler);
        try
        {
            Assert.True(recorder.SetEnabled(true));
            RecordUpdate(recorder);
            Assert.True(recorder.SetEnabled(false));
            await UpdateUntilAsync(controller, () => controller.Readout?.Summary.IsFinal == true);

            var range = controller.Readout!;
            var update = Assert.Single(range.Summary.Records, record => record.Kind == FrameTimingRecordKind.Elapsed);
            var hitch = new FrameTimingHitch(update.Source, update.StartTick, update.EndTick, update.Span, update.OperationId);
            controller.RequestHitch(range, hitch);
            var queuedJob = Assert.IsAssignableFrom<Task>(HitchJob.GetValue(controller));
            Assert.False(queuedJob.IsCompleted);

            controller.StopReadout();
            scheduler.Run();
            Assert.True(queuedJob.IsCompletedSuccessfully);
            Assert.Null(queuedJob.GetType().GetProperty(nameof(Task<int>.Result))!.GetValue(queuedJob));
            Assert.Null(controller.HitchDetail);

            controller.RequestReadout(5, 60);
            controller.RequestHitch(range, hitch);
            scheduler.Run();
            controller.RequestReadout(5, 60);

            var detail = Assert.IsType<FrameTimingHitchDetail>(controller.HitchDetail);
            Assert.Equal(hitch, detail.Hitch);
            Assert.Contains(update, detail.Records);
        }
        finally
        {
            controller.StopReadout();
            recorder.Shutdown();
        }
    }

    private static long RecordUpdate(FrameTimingRecorder recorder)
    {
        var update = recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _);
        return update.End();
    }

    private static int? Entries(FrameTimingMonitorController controller) =>
        controller.Readout?.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate).EntryCount;

    private static async Task UpdateUntilAsync(FrameTimingMonitorController controller, Func<bool> condition)
    {
        var started = Stopwatch.GetTimestamp();
        do
        {
            controller.RequestReadout(5, 60);
            if (condition())
                return;

            await Task.Delay(10);
        }
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));

        Assert.Fail(controller.ReadoutError ?? controller.CollectionError ?? "The requested frame timing readout did not arrive.");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => this.ticks;

        internal void AdvanceRefreshInterval() => this.ticks += TimeSpan.FromMilliseconds(500).Ticks;
    }
}
