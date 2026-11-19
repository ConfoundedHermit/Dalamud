using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImPlot;
using Dalamud.Configuration.Internal;
using Dalamud.Game;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.Windows;
using Dalamud.Utility.Timing;

using Xunit;

namespace Dalamud.Test.Interface;

[Collection("Native framework render synchronization")]
public class FrameTimingMonitorWindowTests
{
    [Fact]
    public async Task NativePlotsDrawAndSelectTheKnownCadenceSpike()
    {
        await Task.Factory.StartNew(
            RunNativePlotTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Fact]
    public async Task SelectedPhaseHeadingKeepsItsBoundaryWhenThePlottedPhaseChanges()
    {
        await Task.Factory.StartNew(
            RunRetainedPhaseSelectionTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Fact]
    public async Task ClearingTheSelectionReleasesHitchDetailsAndKeepsRefreshing()
    {
        await Task.Factory.StartNew(
            RunClearSelectionTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Fact]
    public async Task NativeHitchTableClipsDenseDetailsAndKeepsTheFullScrollRange()
    {
        await Task.Factory.StartNew(
            RunDenseHitchTableTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Theory]
    [InlineData("Callback cadence")]
    [InlineData("Phase elapsed")]
    [InlineData("Main UI age at replay entry")]
    public async Task NativeMouseWheelOverPlotScrollsTheMonitor(string plotName)
    {
        await Task.Factory.StartNew(
            () => RunPlotWheelTest(plotName),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Theory]
    [InlineData(false, "Incomplete (4 unconfirmed intervals)")]
    [InlineData(true, "Incomplete")]
    public async Task NativeCadenceCoverageDescribesUnconfirmedIntervals(bool dropTrailingEntry, string expectedCoverage)
    {
        await Task.Factory.StartNew(
            () => RunCadenceCoverageTest(dropTrailingEntry, expectedCoverage),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryShowsPreparingUntilAReadConfirmsWhetherASessionExists(bool reopenStoppedSession)
    {
        await Task.Factory.StartNew(
            () => RunHistoryReadoutStatusTest(reopenStoppedSession),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Fact]
    public async Task ChangingTargetRateHidesStatisticsUntilTheMatchingReadoutArrives()
    {
        await Task.Factory.StartNew(
            RunChangedTargetRateTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Fact]
    public async Task StoppedHistoryShowsConfirmedCoverageAndCanIncludeTheUncertainBoundary()
    {
        await Task.Factory.StartNew(
            RunStoppedHistoryTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Fact]
    public async Task NativeLegendRescalesCadenceAndTogglesSelectionOfCoincidentPoints()
    {
        await Task.Factory.StartNew(
            RunCadenceLegendTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Fact]
    public async Task NativeCadenceLegendKeepsEveryToggleAccessibleAfterResize()
    {
        await Task.Factory.StartNew(
            RunNarrowCadenceLegendTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Theory]
    [InlineData("Callback cadence")]
    [InlineData("Phase elapsed")]
    [InlineData("Main UI age at replay entry")]
    public async Task NativePlotsSelectTheDisplayedPointWhenALiveRefreshCompletes(string plotName)
    {
        await Task.Factory.StartNew(
            () => RunLiveRefreshSelectionTest(plotName),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Fact]
    public async Task NativePlotsLimitDenseGeometryAndSelectTheOriginalSpike()
    {
        await Task.Factory.StartNew(
            RunDenseNativePlotTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Fact]
    public async Task SuppressingDrawDiscardsTheReadoutAndKeepsCollecting()
    {
        await Task.Factory.StartNew(
            RunDrawSuppressionTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Fact]
    public async Task FailedDrawDiscardsTheReadoutAndKeepsCollecting()
    {
        await Task.Factory.StartNew(
            RunDrawFailureTest,
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownDrainsAnAdmittedDrawAndRejectsAStaleDraw(bool failDraw)
    {
        await Task.Factory.StartNew(
            () => RunShutdownTest(failDraw),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private static void RunShutdownTest(bool failDraw)
    {
        Action? onReadSource = null;
        var sourceReads = 0;
        using var fixture = new NativeFixture(getOverlaySource: () =>
        {
            sourceReads++;
            onReadSource?.Invoke();
            return FrameTimingSource.DxgiPresent;
        });
        Assert.True(fixture.Recorder.SetEnabled(true));
        fixture.FrameUntil(() => fixture.Controller.Readout is not null);
        var readout = fixture.Controller.Readout;

        Task? shutdown = null;
        var failure = new InvalidOperationException("Injected failure during timing monitor shutdown.");
        onReadSource = () =>
        {
            // Start shutdown while the admitted draw is still in DrawControls.
            onReadSource = null;
            shutdown = Task.Factory.StartNew(
                fixture.Window.Shutdown,
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            Assert.True(SpinWait.SpinUntil(() => !fixture.Window.DrawConditions(), TimeSpan.FromSeconds(10)));
            Assert.False(shutdown.IsCompleted);
            Assert.Same(readout, fixture.Controller.Readout);

            if (failDraw)
                throw failure;
        };

        if (failDraw)
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => fixture.Frame()));
        else
            fixture.Frame();

        Assert.NotNull(shutdown);
        Assert.True(shutdown.Wait(TimeSpan.FromSeconds(10)));
        Assert.Null(fixture.Controller.Readout);

        // Calling Draw directly simulates a host that checked DrawConditions before shutdown.
        var readsAtShutdown = sourceReads;
        fixture.Frame();
        Assert.Equal(readsAtShutdown, sourceReads);
        Assert.Null(fixture.Controller.Readout);
    }

    private static void RunDrawFailureTest()
    {
        var failure = new InvalidOperationException("Injected timing monitor draw failure.");
        var failDraw = false;
        using var fixture = new NativeFixture(
            getOverlaySource: () => failDraw ? throw failure : FrameTimingSource.DxgiPresent);
        Assert.True(fixture.Recorder.SetEnabled(true));
        RecordUpdate();
        Thread.Sleep(40);
        RecordUpdate();
        fixture.FrameUntil(() => Cadence()?.EntryCount == 2);
        var detail = SelectPlotSample(fixture, Cadence()!.Samples, 0);

        failDraw = true;
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => fixture.Frame()));
        Assert.Null(fixture.Controller.Readout);
        Assert.Null(fixture.Controller.HitchDetail);
        Assert.True(fixture.Window.IsOpen);
        Assert.True(fixture.Controller.IsEnabled);

        failDraw = false;
        RecordUpdate();
        fixture.FrameUntil(() => Cadence()?.EntryCount == 3);
        Assert.Equal(detail.SessionId, fixture.Controller.Readout!.Summary.SessionId);
        Assert.Null(fixture.Controller.HitchDetail);

        FrameTimingCadenceView? Cadence() =>
            fixture.Controller.Readout?.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);

        void RecordUpdate()
        {
            var update = fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _);
            update.End();
        }
    }

    private static void RunDrawSuppressionTest()
    {
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        using var fixture = new NativeFixture();
        using var frameworkDestroy = new CancellationTokenSource();
        var framework = RuntimeHelpers.GetUninitializedObject(typeof(Framework));
        typeof(Framework).GetField("frameworkDestroy", privateInstance)!.SetValue(framework, frameworkDestroy);
        var manager = (InterfaceManager)RuntimeHelpers.GetUninitializedObject(typeof(InterfaceManager));
        typeof(InterfaceManager).GetField("framework", privateInstance)!.SetValue(manager, framework);
        var display = typeof(InterfaceManager).GetMethod("Display", privateInstance)!.CreateDelegate<Action>(manager);
        var dispatched = false;
        manager.Draw += () => dispatched = true;
        manager.DrawSuppressed += fixture.Window.StopReadout;
        manager.IsDispatchingEvents = true;

        Assert.True(fixture.Recorder.SetEnabled(true));
        RecordUpdate();
        Thread.Sleep(40);
        RecordUpdate();
        fixture.FrameUntil(() => Cadence()?.EntryCount == 2);
        var sessionId = fixture.Controller.Readout!.Summary.SessionId;
        SelectPlotSample(fixture, Cadence()!.Samples, 0);

        var readoutJob = typeof(FrameTimingMonitorController).GetField("readoutJob", privateInstance)!;
        var lifecycle = (Lock)typeof(FrameTimingRecorder).GetField("lifecycle", privateInstance)!.GetValue(fixture.Recorder)!;
        fixture.FrameUntil(() => readoutJob.GetValue(fixture.Controller) is null);
        Task pendingRefresh;
        // Block the refresh in the recorder until draw suppression invalidates it.
        using (lifecycle.EnterScope())
        {
            fixture.FrameUntil(() => readoutJob.GetValue(fixture.Controller) is Task);
            pendingRefresh = Assert.IsAssignableFrom<Task>(readoutJob.GetValue(fixture.Controller));
            Assert.False(pendingRefresh.IsCompleted);
            Assert.NotNull(fixture.Controller.Readout);
            Assert.NotNull(fixture.Controller.HitchDetail);

            manager.IsDispatchingEvents = false;
            display();
            Assert.False(dispatched);
            Assert.Null(fixture.Controller.Readout);
            Assert.Null(fixture.Controller.HitchDetail);
            Assert.True(fixture.Window.IsOpen);
            Assert.True(fixture.Controller.IsEnabled);
        }

        Assert.True(pendingRefresh.Wait(TimeSpan.FromSeconds(10)));
        RecordUpdate();
        manager.IsDispatchingEvents = true;
        fixture.Frame();
        Assert.Null(fixture.Controller.Readout);
        fixture.FrameUntil(() => Cadence()?.EntryCount == 3);
        Assert.Equal(sessionId, fixture.Controller.Readout!.Summary.SessionId);

        FrameTimingCadenceView? Cadence() =>
            fixture.Controller.Readout?.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);

        void RecordUpdate()
        {
            var update = fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _);
            update.End();
        }
    }

    private static void RunNativePlotTest()
    {
        using var fixture = new NativeFixture();
        fixture.Frame();
        Assert.False(fixture.Controller.IsEnabled);
        Assert.Null(fixture.Controller.Readout);
        Assert.True(ImGui.GetDrawData().TotalVtxCount > 0);

        fixture.Controller.SetEnabled(true);
        fixture.FrameUntil(() => fixture.Controller.IsEnabled && !fixture.Controller.IsChangingCollection);
        var firstUpdate = fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _);
        var nativeUpdate = firstUpdate.Begin(FrameTimingSpan.FrameworkNativeUpdate);
        Thread.Sleep(40);
        nativeUpdate.End();
        firstUpdate.End();
        var secondUpdate = fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _);
        secondUpdate.End();

        var kernel = fixture.Recorder.Begin(FrameTimingSource.KernelPresent, FrameTimingSpan.KernelPresent, out _);
        var step = kernel.Begin(FrameTimingSpan.Step);
        var snapshot = step.CreateSnapshot(Stopwatch.GetTimestamp());
        step.RecordPublication(snapshot);
        step.End();
        kernel.End();
        var overlay = fixture.Recorder.Begin(FrameTimingSource.DxgiPresent, FrameTimingSpan.OverlayCallback, out _);
        var replay = overlay.Begin(FrameTimingSpan.MainReplay);
        replay.End(FrameTimingOutcome.Completed, FrameTimingTarget.Display, snapshot: snapshot);
        overlay.End();

        fixture.Controller.SetEnabled(false);
        fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.Collecting: false });
        var range = fixture.Controller.Readout!;
        Assert.Equal(1, range.MainUiAge.Statistics.Count);
        Assert.Contains(range.Phases, phase => phase.Span == FrameTimingSpan.Step);
        var cadence = range.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate).Samples;
        var hitch = Assert.Single(cadence.Hitches);
        Assert.Equal(1, cadence.Statistics.OverBudget);

        var detail = SelectPlotSample(fixture, cadence, 0);
        Assert.Equal(hitch, detail.Hitch);
        var span = Assert.Single(detail.Records, record => record.Span == FrameTimingSpan.FrameworkNativeUpdate);
        Assert.Equal(FrameTimingOutcome.Completed, span.Outcome);
        Assert.Equal(Environment.CurrentManagedThreadId, span.ThreadId);
    }

    private static void RunRetainedPhaseSelectionTest()
    {
        const string fullPhase = "Full game update / Game update";
        const string nativePhase = "Original native update / Game update";
        using var fixture = new NativeFixture();
        Assert.True(fixture.Recorder.SetEnabled(true));
        Thread.Sleep(20);
        var update = fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _);
        Thread.Sleep(20);
        var native = update.Begin(FrameTimingSpan.FrameworkNativeUpdate);
        Thread.Sleep(20);
        native.End();
        update.End();
        Assert.True(fixture.Recorder.SetEnabled(false));
        fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.IsFinal: true });

        var phases = fixture.Controller.Readout!.Phases;
        var original = Assert.Single(phases, phase => phase.Span == FrameTimingSpan.FrameworkNativeUpdate).Samples;
        var full = Assert.Single(phases, phase => phase.Span == FrameTimingSpan.FrameworkUpdate).Samples;

        ChoosePhase(nativePhase);
        var selected = SelectPlotSample(fixture, original, 0, "Phase elapsed");
        Assert.Equal(original.Hitches[0], selected.Hitch);
        Assert.Contains(selected.Records, record => record.Span == FrameTimingSpan.FrameworkUpdate);
        Assert.Contains(selected.Records, record => record.Span == FrameTimingSpan.FrameworkNativeUpdate);

        ChoosePhase(fullPhase);
        Assert.Equal(selected.Hitch, fixture.Controller.HitchDetail?.Hitch);
        var text = ReadText();
        Assert.Contains($"{{ {fullPhase} }} Plotted phase", text);
        Assert.Contains($"Original native update {original.Milliseconds[0]:F3} ms · Game update", text);

        Assert.Equal(full.Hitches[0], SelectPlotSample(fixture, full, 0, "Phase elapsed").Hitch);
        Assert.Contains($"Full game update {full.Milliseconds[0]:F3} ms · Game update", ReadText());

        void ChoosePhase(string label)
        {
            fixture.Frame(() => ImGuiP.ActivateItem(ImGui.GetID("Plotted phase"u8)));
            fixture.Frame(() =>
            {
                var context = ImGui.GetCurrentContext();
                var popup = context.NavWindow;
                Assert.False(popup.IsNull);
                Assert.Equal(Assert.Single(context.OpenPopupStack).PopupId, popup.PopupId);
                ImGuiP.ActivateItem(ImGuiP.GetID(popup, label));
            });
            fixture.Frame();
        }

        string ReadText()
        {
            var log = string.Empty;
            fixture.Frame(
                () =>
                {
                    log = Encoding.UTF8.GetString(ImGui.GetCurrentContext().LogBuffer.Buf.AsSpan()[..^1]);
                    ImGui.LogFinish();
                },
                beforeDraw: () => ImGuiP.LogToBuffer(0));
            return log;
        }
    }

    private static void RunClearSelectionTest()
    {
        using var fixture = new NativeFixture();
        Assert.True(fixture.Recorder.SetEnabled(true));
        RecordUpdate();
        Thread.Sleep(40);
        RecordUpdate();
        fixture.FrameUntil(() => Cadence()?.EntryCount == 2);

        var detail = SelectPlotSample(fixture, Cadence()!.Samples, 0);
        var wideButton = fixture.GetVisibleControlRect("Clear selection");
        fixture.WindowWidth = 280;
        var narrowButton = fixture.GetVisibleControlRect("Clear selection");
        Assert.True(narrowButton.Min.X < wideButton.Min.X);
        fixture.Click((narrowButton.Min + narrowButton.Max) / 2);
        Assert.Null(fixture.Controller.HitchDetail);
        Assert.False(fixture.Controller.IsReadingHitch);
        Assert.True(fixture.Controller.IsEnabled);

        RecordUpdate();
        fixture.FrameUntil(() => Cadence()?.EntryCount == 3);
        Assert.Equal(detail.SessionId, fixture.Controller.Readout!.Summary.SessionId);
        Assert.Null(fixture.Controller.HitchDetail);

        FrameTimingCadenceView? Cadence() =>
            fixture.Controller.Readout?.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);

        void RecordUpdate()
        {
            var update = fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _);
            update.End();
        }
    }

    private static void RunPlotWheelTest(string plotName)
    {
        using var fixture = new NativeFixture { WindowHeight = 610 };
        Assert.True(fixture.Recorder.SetEnabled(true));
        fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _).End();
        Assert.True(fixture.Recorder.SetEnabled(false));
        fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.IsFinal: true });

        var mouse = Vector2.Zero;
        fixture.Frame(() =>
        {
            Assert.True(ImGui.GetScrollMaxY() > 0);
            var plot = ImPlot.GetPlot(plotName);
            Assert.False(plot.IsNull);
            mouse = (plot.PlotRect.Min + plot.PlotRect.Max) / 2;
            var clip = ImGuiP.GetCurrentWindowRead().InnerClipRect;
            Assert.InRange(mouse.X, clip.Min.X, clip.Max.X);
            Assert.InRange(mouse.Y, clip.Min.Y, clip.Max.Y);
        });

        var io = ImGui.GetIO();
        io.AddMousePosEvent(mouse.X, mouse.Y);
        var limits = default((double XMin, double XMax, double YMin, double YMax));
        fixture.Frame(() =>
        {
            Assert.True(ImPlot.GetPlot(plotName).Hovered);
            Assert.Equal(0, ImGui.GetScrollY());
            limits = GetLimits();
        });

        io.AddMouseWheelEvent(0, -1);
        fixture.Frame(() =>
        {
            Assert.True(ImGui.GetScrollY() > 0);
            Assert.Equal(limits, GetLimits());
        });

        (double XMin, double XMax, double YMin, double YMax) GetLimits()
        {
            var plot = ImPlot.GetPlot(plotName);
            var x = plot.Axes[(int)ImAxis.X1].Range;
            var y = plot.Axes[(int)ImAxis.Y1].Range;
            return (x.Min, x.Max, y.Min, y.Max);
        }
    }

    private static void RunDenseHitchTableTest()
    {
        using var fixture = new NativeFixture(capacity: 256);
        Assert.True(fixture.Recorder.SetEnabled(true));
        var update = fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _);
        for (var i = 0; i < FrameTimingRange.MaxHitchRecords; i++)
            update.Begin(FrameTimingSpan.FrameworkNativeUpdate).End(nativeStatus: i);
        update.End();
        Thread.Sleep(20);
        fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _).End();
        Thread.Sleep(20);
        Assert.True(fixture.Recorder.SetEnabled(false));
        fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.IsFinal: true });

        var withoutDetails = MeasureDrawAllocations();
        var cadence = fixture.Controller.Readout!.Cadence.Single(series => series.Source == FrameTimingSource.FrameworkUpdate);
        var detail = SelectPlotSample(fixture, cadence.Samples, 0);
        Assert.Equal(FrameTimingRange.MaxHitchRecords, detail.Records.Length);

        var withDetails = MeasureDrawAllocations();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Average managed bytes per draw were {withoutDetails:N0} before selection and {withDetails:N0} with {detail.Records.Length} hitch records.");
        Assert.True(withDetails <= withoutDetails + 4096,
            $"Hitch details added {withDetails - withoutDetails:N0} managed bytes per draw.");

        fixture.Frame(() =>
        {
            var table = GetHitchTable();
            Assert.InRange(table.CurrentRow, 1, 16);
            var child = table.InnerWindow;
            Assert.True(child.ScrollMax.Y >= (detail.Records.Length - 16) * ImGui.GetTextLineHeight());
            ImGuiP.SetScrollY(child, child.ScrollMax.Y);
        });
        fixture.Frame(() =>
        {
            var table = GetHitchTable();
            Assert.InRange(table.CurrentRow, 1, 16);
            Assert.Equal(table.InnerWindow.ScrollMax.Y, table.InnerWindow.Scroll.Y);
        });

        static ImGuiTablePtr GetHitchTable()
        {
            var table = ImGuiP.TableFindByID(ImGui.GetID("Selected span details"u8));
            Assert.False(table.IsNull);
            return table;
        }

        long MeasureDrawAllocations()
        {
            const int frames = 8;
            long started = 0;
            long bytes = 0;
            for (var i = 0; i < frames; i++)
                fixture.Frame();
            for (var i = 0; i < frames; i++)
            {
                fixture.Frame(
                    () => bytes += GC.GetAllocatedBytesForCurrentThread() - started,
                    () => started = GC.GetAllocatedBytesForCurrentThread());
            }

            return bytes / frames;
        }
    }

    private static void RunHistoryReadoutStatusTest(bool reopenStoppedSession)
    {
        const string preparing = "Preparing the timing readout…";
        const string noSession = "No timing session has been recorded.";
        using var fixture = new NativeFixture();

        long? stoppedSession = null;
        if (reopenStoppedSession)
        {
            Assert.True(fixture.Recorder.SetEnabled(true));
            var update = fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _);
            update.End();
            Assert.True(fixture.Recorder.SetEnabled(false));
            fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.IsFinal: true });
            stoppedSession = fixture.Controller.Readout!.Summary.SessionId;

            fixture.Window.IsOpen = false;
            fixture.Window.OnClose();
            Assert.Null(fixture.Controller.Readout);
            fixture.Window.IsOpen = true;
        }

        var text = string.Empty;
        fixture.Frame(ReadLoggedText, BeginLog);
        Assert.Contains(preparing, text);
        Assert.DoesNotContain(noSession, text);

        if (stoppedSession is { } sessionId)
        {
            fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.IsFinal: true });
            var reopened = fixture.Controller.Readout!;
            Assert.Equal(sessionId, reopened.Summary.SessionId);
            Assert.Equal(1, reopened.Cadence.Single(static series => series.Source == FrameTimingSource.FrameworkUpdate).EntryCount);
        }
        else
        {
            fixture.FrameUntil(() => text.Contains(noSession, StringComparison.Ordinal), ReadLoggedText, BeginLog);
            Assert.DoesNotContain(preparing, text);
        }

        static void BeginLog() => ImGuiP.LogToBuffer(0);

        void ReadLoggedText()
        {
            text = Encoding.UTF8.GetString(ImGui.GetCurrentContext().LogBuffer.Buf.AsSpan()[..^1]);
            ImGui.LogFinish();
        }
    }

    private static void RunChangedTargetRateTest()
    {
        const string preparing = "Preparing the timing readout…";
        var clock = new ManualClock();
        using var fixture = new NativeFixture(timeProvider: clock);
        Assert.True(fixture.Recorder.SetEnabled(true));
        var pin = fixture.Recorder.Begin(FrameTimingSpan.FrameworkUpdate, out var admission);
        var session = admission.Session;
        var endTick = session.StartedTick + (Stopwatch.Frequency / 100);
        WriteCadence(session.StartedTick);
        WriteCadence(endTick);
        Assert.True(SpinWait.SpinUntil(() => Stopwatch.GetTimestamp() > endTick, TimeSpan.FromSeconds(1)));
        pin.End();
        Assert.True(fixture.Recorder.SetEnabled(false));

        fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.IsFinal: true });
        var initial = fixture.Controller.Readout!;
        Assert.Contains($"Frame budget {1000.0 / 60:F2} ms", LoggedFrame());
        Assert.Equal(0, OverBudget(initial));

        // Enter an exact rate through the slider's numeric input mode.
        fixture.Frame(beforeDraw: () => ImGui.GetCurrentContext().NavActivateInputId = ImGui.GetID("Target rate"u8));
        var io = ImGui.GetIO();
        io.AddInputCharacters("120"u8);
        var pending = LoggedFrame();
        Assert.Contains(preparing, pending);
        Assert.DoesNotContain("Frame budget", pending);
        Assert.DoesNotContain("Game update", pending);

        io.AddKeyEvent(ImGuiKey.Enter, true);
        fixture.Frame();
        io.AddKeyEvent(ImGuiKey.Enter, false);
        Assert.Contains("120 fps", LoggedFrame());

        clock.AdvanceRefreshInterval();
        fixture.FrameUntil(() => fixture.Controller.Readout is { } range && !ReferenceEquals(initial, range));
        var updated = LoggedFrame();
        Assert.DoesNotContain(preparing, updated);
        Assert.Contains($"Frame budget {1000.0 / 120:F2} ms", updated);
        Assert.Equal(1, OverBudget(fixture.Controller.Readout!));

        var wideHistory = fixture.GetVisibleControlRect("Visible history", isSlider: true);
        var wideRate = fixture.GetVisibleControlRect("Target rate", isSlider: true);
        Assert.Equal(wideHistory.Min.Y, wideRate.Min.Y);

        fixture.WindowWidth = 280;
        var narrowHistory = fixture.GetVisibleControlRect("Visible history", isSlider: true);
        var narrowRate = fixture.GetVisibleControlRect("Target rate", isSlider: true);
        Assert.Equal(narrowHistory.Min.X, narrowRate.Min.X);
        Assert.True(narrowRate.Min.Y > narrowHistory.Min.Y);
        fixture.Click(new(narrowRate.Max.X - 2, (narrowRate.Min.Y + narrowRate.Max.Y) / 2));
        clock.AdvanceRefreshInterval();
        fixture.FrameUntil(() => fixture.Controller.Readout is { BudgetMilliseconds: <= 2.5 });

        static int OverBudget(FrameTimingRange range) =>
            range.Cadence.Single(static series => series.Source == FrameTimingSource.FrameworkUpdate).Samples.Statistics.OverBudget;

        void WriteCadence(long tick)
        {
            var entry = new FrameTimingRecord(session.NextOperationId(), tick, tick, Environment.CurrentManagedThreadId,
                FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, FrameTimingRecordKind.Cadence);
            session.Buffer.BeginWrite(out var sequence);
            session.Buffer.Write(sequence, entry);
        }

        string LoggedFrame()
        {
            var text = string.Empty;
            fixture.Frame(
                () =>
                {
                    text = Encoding.UTF8.GetString(ImGui.GetCurrentContext().LogBuffer.Buf.AsSpan()[..^1]);
                    ImGui.LogFinish();
                },
                beforeDraw: () => ImGuiP.LogToBuffer(0));
            return text;
        }
    }

    private static void RunStoppedHistoryTest()
    {
        using var fixture = new NativeFixture(beforeStopCommit: recorder =>
        {
            if (!recorder.IsEnabled)
                return;

            // Complete a callback between the last confirmed admission tick and the stop cutoff.
            var hookTick = Stopwatch.GetTimestamp();
            SpinWait.SpinUntil(() => Stopwatch.GetTimestamp() > hookTick);
            var present = recorder.Begin(FrameTimingSource.KernelPresent, FrameTimingSpan.KernelPresent, out _);
            var presentEnd = present.End();
            SpinWait.SpinUntil(() => Stopwatch.GetTimestamp() > presentEnd);
        });
        Assert.True(fixture.Recorder.SetEnabled(true));
        var update = fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _);
        Assert.True(fixture.Recorder.SetEnabled(false));
        update.End();

        fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.IsFinal: true });
        var confirmed = fixture.Controller.Readout!;
        Assert.Equal(confirmed.Summary.AdmissionConfirmedThroughTick, confirmed.EndTick);
        Assert.True(confirmed.EndTick < confirmed.Summary.CutoffTick);
        Assert.True(confirmed.CoverageComplete);
        var cadence = UpdateCadence(confirmed);
        Assert.True(cadence.CoverageComplete);
        Assert.True(cadence.TrailingGapIsLowerBound);
        Assert.NotNull(cadence.UnfinishedEntry);
        Assert.DoesNotContain(confirmed.Phases, static phase => phase.Span == FrameTimingSpan.KernelPresent);

        fixture.Frame(
            () =>
            {
                var log = Encoding.UTF8.GetString(ImGui.GetCurrentContext().LogBuffer.Buf.AsSpan()[..^1]);
                ImGui.LogFinish();
                Assert.Contains("ms before the stop cutoff", log);
                Assert.Contains("before the selected range ended.", log);
                ImGuiP.ActivateItem(ImGui.GetID("Include uncertain stop boundary"u8));
            },
            beforeDraw: () => ImGuiP.LogToBuffer(0));
        fixture.FrameUntil(() => fixture.Controller.Readout?.EndTick == confirmed.Summary.CutoffTick);
        var full = fixture.Controller.Readout!;
        Assert.False(full.CoverageComplete);
        Assert.False(UpdateCadence(full).TrailingGapIsLowerBound);
        Assert.Contains(full.Phases, static phase => phase.Span == FrameTimingSpan.KernelPresent);

        static FrameTimingCadenceView UpdateCadence(FrameTimingRange range) =>
            range.Cadence.Single(static series => series.Source == FrameTimingSource.FrameworkUpdate);
    }

    private static void RunCadenceCoverageTest(bool dropTrailingEntry, string expectedCoverage)
    {
        const int entryCount = 5;
        using var fixture = new NativeFixture(capacity: entryCount);
        Assert.True(fixture.Recorder.SetEnabled(true));
        var pin = fixture.Recorder.Begin(FrameTimingSpan.FrameworkUpdate, out var admission);
        var session = admission.Session;
        var buffer = session.Buffer;

        // Reserve the trailing entry's sequence early so newer writes take its slot.
        long delayed = 0;
        if (dropTrailingEntry)
            buffer.BeginWrite(out delayed);

        var lastEntryTick = session.StartedTick;
        for (var i = 0; i < entryCount; i++)
        {
            lastEntryTick = buffer.BeginWrite(out var sequence);
            buffer.Write(sequence, Entry(lastEntryTick));
        }

        if (dropTrailingEntry)
        {
            SpinWait.SpinUntil(() => Stopwatch.GetTimestamp() > lastEntryTick);
            buffer.Write(delayed, Entry(Stopwatch.GetTimestamp()));
        }
        else
        {
            buffer.BeginCadenceWrite();
        }

        try
        {
            fixture.FrameUntil(() => fixture.Controller.Readout is not null);
            Assert.Equal(dropTrailingEntry ? 1 : 0, fixture.Controller.Readout!.Summary.DroppedRecords);
            fixture.Frame(
                () =>
                {
                    var text = Encoding.UTF8.GetString(ImGui.GetCurrentContext().LogBuffer.Buf.AsSpan()[..^1]);
                    ImGui.LogFinish();
                    var row = Assert.Single(text.Split('\n'),
                        line => line.TrimStart().StartsWith("| Game update |", StringComparison.Ordinal));
                    var columns = row.Trim().Trim('|').Split('|', StringSplitOptions.TrimEntries);
                    Assert.EndsWith(" ms (incomplete)", columns[^2]);
                    Assert.Equal(expectedCoverage, columns[^1]);
                },
                beforeDraw: () => ImGuiP.LogToBuffer(0));
        }
        finally
        {
            if (!dropTrailingEntry)
                buffer.CancelCadenceWrite();

            pin.End();
        }

        FrameTimingRecord Entry(long tick) => new(
            session.NextOperationId(), tick, tick, Environment.CurrentManagedThreadId,
            FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, FrameTimingRecordKind.Cadence);
    }

    private static void RunCadenceLegendTest()
    {
        using var fixture = new NativeFixture();
        Assert.True(fixture.Recorder.SetEnabled(true));
        var pin = fixture.Recorder.Begin(FrameTimingSpan.FrameworkUpdate, out var admission);
        var session = admission.Session;
        var startTick = session.StartedTick;
        // Keep DXGI below the frame budget so hiding the budget line lowers the axis maximum.
        var endTick = startTick + (Stopwatch.Frequency / 125);
        var outlierTick = endTick + Stopwatch.Frequency;
        WriteCadence(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, startTick);
        WriteCadence(FrameTimingSource.DxgiPresent, FrameTimingSpan.OverlayCallback, startTick);
        WriteCadence(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, endTick);
        WriteCadence(FrameTimingSource.DxgiPresent, FrameTimingSpan.OverlayCallback, endTick);
        WriteCadence(FrameTimingSource.DxgiPresent, FrameTimingSpan.OverlayCallback, endTick + (Stopwatch.Frequency / 250));
        WriteCadence(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, outlierTick);

        // Keep the outlier inside the stopped readout and away from the right edge of the plot.
        var cutoff = outlierTick + (Stopwatch.Frequency / 200);
        while (Stopwatch.GetTimestamp() < cutoff)
            Thread.Sleep(1);
        pin.End();

        fixture.Controller.SetEnabled(false);
        fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.IsFinal: true });
        var range = fixture.Controller.Readout!;
        var gameUpdate = range.Cadence.Single(static series => series.Source == FrameTimingSource.FrameworkUpdate).Samples;
        var dxgiPresent = range.Cadence.Single(static series => series.Source == FrameTimingSource.DxgiPresent).Samples;
        Assert.Equal(2, gameUpdate.Hitches.Length);
        Assert.Equal(2, dxgiPresent.Hitches.Length);
        var outlier = gameUpdate.Milliseconds[1];
        Assert.Equal(1000, outlier);
        var visibleMax = dxgiPresent.Milliseconds.Max();
        Assert.True(range.BudgetMilliseconds > visibleMax);
        Assert.Equal(gameUpdate.Seconds[0], dxgiPresent.Seconds[0]);
        Assert.Equal(gameUpdate.Milliseconds[0], dxgiPresent.Milliseconds[0]);

        Assert.Equal(gameUpdate.Hitches[0], SelectPlotSample(fixture, gameUpdate, 0).Hitch);

        var firstLegendIcon = Vector2.Zero;
        var budgetLegendPosition = Vector2.Zero;
        var budgetLegendIndex = 0;
        var initialMax = 0d;
        fixture.Frame(() =>
        {
            var plot = ImPlot.GetPlot("Callback cadence"u8);
            Assert.False(plot.IsNull);
            initialMax = plot.Axes[(int)ImAxis.Y1].Range.Max;
            Assert.True(initialMax >= outlier);
            Assert.Equal("Game update", ImPlot.GetLegendLabelS(ref plot.Items, 0));
            var padding = ImPlot.GetStyle().LegendInnerPadding;
            var halfIcon = ImGui.GetTextLineHeight() / 2;
            firstLegendIcon = plot.Items.Legend.Rect.Min + padding + new Vector2(halfIcon);
            budgetLegendIndex = ImPlot.GetLegendCount(ref plot.Items) - 1;
            var budgetLabel = ImPlot.GetLegendLabelS(ref plot.Items, budgetLegendIndex);
            Assert.Equal("Frame budget", budgetLabel);
            budgetLegendPosition = new Vector2(
                plot.Items.Legend.Rect.Max.X - padding.X - (ImGui.CalcTextSize(budgetLabel).X / 2),
                firstLegendIcon.Y);
        });

        fixture.Click(firstLegendIcon);
        var budgetMax = 0d;
        fixture.Frame(() =>
        {
            var plot = ImPlot.GetPlot("Callback cadence"u8);
            ref var y = ref plot.Axes[(int)ImAxis.Y1];
            budgetMax = y.Range.Max;
            Assert.InRange(range.BudgetMilliseconds, y.Range.Min, budgetMax);
            Assert.True(budgetMax < outlier);
        });

        fixture.Click(budgetLegendPosition);
        fixture.Frame(() =>
        {
            var plot = ImPlot.GetPlot("Callback cadence"u8);
            Assert.False(ImPlot.GetLegendItem(ref plot.Items, budgetLegendIndex).Show);
            ref var y = ref plot.Axes[(int)ImAxis.Y1];
            Assert.InRange(visibleMax, y.Range.Min, y.Range.Max);
            Assert.True(y.Range.Max < range.BudgetMilliseconds, $"The hidden budget still sets the axis maximum to {y.Range.Max:F2} ms.");
            var separation = Math.Abs(ToPixels(y, dxgiPresent.Milliseconds[0]) - ToPixels(y, dxgiPresent.Milliseconds[1]));
            Assert.True(separation >= ImGui.GetTextLineHeight(), $"DXGI samples are only {separation:F2} px apart.");
        });
        Assert.Equal(dxgiPresent.Hitches[0], SelectPlotSample(fixture, dxgiPresent, 0).Hitch);

        fixture.Click(budgetLegendPosition);
        fixture.Frame(() => Assert.Equal(budgetMax, ImPlot.GetPlot("Callback cadence"u8).Axes[(int)ImAxis.Y1].Range.Max));

        fixture.Click(firstLegendIcon);
        fixture.Frame(() => Assert.Equal(initialMax, ImPlot.GetPlot("Callback cadence"u8).Axes[(int)ImAxis.Y1].Range.Max));
        Assert.Equal(gameUpdate.Hitches[0], SelectPlotSample(fixture, gameUpdate, 0).Hitch);

        void WriteCadence(FrameTimingSource source, FrameTimingSpan span, long tick)
        {
            var entry = new FrameTimingRecord(session.NextOperationId(), tick, tick, Environment.CurrentManagedThreadId,
                source, span, FrameTimingRecordKind.Cadence);
            session.Buffer.BeginWrite(out var sequence);
            session.Buffer.Write(sequence, entry);
        }
    }

    private static void RunNarrowCadenceLegendTest()
    {
        using var fixture = new NativeFixture(getOverlaySource: () => FrameTimingSource.ReShadePresent);
        Assert.True(fixture.Recorder.SetEnabled(true));
        RecordCallbacks();
        Thread.Sleep(10);
        RecordCallbacks();
        Assert.True(fixture.Recorder.SetEnabled(false));
        fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.IsFinal: true });
        fixture.Frame(() =>
        {
            var plot = ImPlot.GetPlot("Callback cadence"u8);
            Assert.True(plot.Items.Legend.Flags.HasFlag(ImPlotLegendFlags.Horizontal));
        });

        string[] labels = ["Game update", "Kernel Present", "ReShade addon Present", "Frame budget"];
        var toggles = new Vector2[labels.Length];
        fixture.WindowWidth = 280;
        fixture.Frame(() =>
        {
            var plot = ImPlot.GetPlot("Callback cadence"u8);
            var legend = plot.Items.Legend;
            Assert.InRange(legend.Rect.Min.X, plot.FrameRect.Min.X, plot.FrameRect.Max.X);
            Assert.InRange(legend.Rect.Max.X, plot.FrameRect.Min.X, plot.FrameRect.Max.X);
            Assert.False(legend.Flags.HasFlag(ImPlotLegendFlags.Horizontal));
            Assert.Equal(labels.Length, ImPlot.GetLegendCount(ref plot.Items));

            var style = ImPlot.GetStyle();
            var lineHeight = ImGui.GetTextLineHeight();
            var firstIcon = legend.Rect.Min + style.LegendInnerPadding + new Vector2(lineHeight / 2);
            for (var i = 0; i < toggles.Length; i++)
            {
                Assert.Equal(labels[i], ImPlot.GetLegendLabelS(ref plot.Items, i));
                toggles[i] = firstIcon + new Vector2(0, i * (lineHeight + style.LegendSpacing.Y));
            }
        });

        for (var i = 0; i < toggles.Length; i++)
        {
            fixture.Click(toggles[i]);
            fixture.Frame(() =>
            {
                var plot = ImPlot.GetPlot("Callback cadence"u8);
                Assert.False(ImPlot.GetLegendItem(ref plot.Items, i).Show);
            });
        }

        fixture.WindowWidth = 1000;
        fixture.Frame(() =>
        {
            var plot = ImPlot.GetPlot("Callback cadence"u8);
            Assert.True(plot.Items.Legend.Flags.HasFlag(ImPlotLegendFlags.Horizontal));
            for (var i = 0; i < toggles.Length; i++)
                Assert.False(ImPlot.GetLegendItem(ref plot.Items, i).Show);
        });

        ImGui.GetIO().FontGlobalScale = 3f;
        fixture.WindowWidth = 280;
        fixture.Frame(() =>
        {
            var plot = ImPlot.GetPlot("Callback cadence"u8);
            Assert.True(plot.Flags.HasFlag(ImPlotFlags.NoLegend));
        });

        var menu = fixture.GetVisibleControlRect("##Cadence series");
        fixture.Click((menu.Min + menu.Max) / 2);
        for (var i = 0; i < labels.Length; i++)
        {
            var toggle = fixture.GetVisibleControlRect(labels[i], inPopup: true);
            fixture.Click((toggle.Min + toggle.Max) / 2);
            fixture.Frame(() =>
            {
                var plot = ImPlot.GetPlot("Callback cadence"u8);
                Assert.True(ImPlot.GetLegendItem(ref plot.Items, i).Show);
            });
        }

        fixture.Click(new Vector2(900, 20));
        ImGui.GetIO().FontGlobalScale = 1f;
        fixture.WindowWidth = 1000;
        fixture.Frame(() =>
        {
            var plot = ImPlot.GetPlot("Callback cadence"u8);
            Assert.False(plot.Flags.HasFlag(ImPlotFlags.NoLegend));
            Assert.True(plot.Items.Legend.Flags.HasFlag(ImPlotLegendFlags.Horizontal));
            for (var i = 0; i < labels.Length; i++)
                Assert.True(ImPlot.GetLegendItem(ref plot.Items, i).Show);
        });

        void RecordCallbacks()
        {
            fixture.Recorder.Begin(FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate, out _).End();
            fixture.Recorder.Begin(FrameTimingSource.KernelPresent, FrameTimingSpan.KernelPresent, out _).End();
            fixture.Recorder.Begin(FrameTimingSource.ReShadePresent, FrameTimingSpan.OverlayCallback, out _).End();
        }
    }

    private static void RunLiveRefreshSelectionTest(string plotName)
    {
        var clock = new ManualClock();
        using var fixture = new NativeFixture(timeProvider: clock);
        Assert.True(fixture.Recorder.SetEnabled(true));
        var pin = fixture.Recorder.Begin(FrameTimingSpan.FrameworkUpdate, out var admission);
        var session = admission.Session;
        var tenMilliseconds = Stopwatch.Frequency / 100;
        var thread = Environment.CurrentManagedThreadId;
        WriteCadence(2);
        WriteCadence(3);
        WriteMetrics(2, 1);
        WaitPast(6);

        fixture.FrameUntil(() => fixture.Controller.Readout is not null);
        var initial = fixture.Controller.Readout!;
        var initialSeries = GetSeries(initial);
        var expectedHitch = Assert.Single(initialSeries.Hitches);
        fixture.Frame();
        var mouse = Vector2.Zero;
        fixture.Frame(() => mouse = GetPointPosition(initialSeries));
        var io = ImGui.GetIO();
        io.AddMousePosEvent(mouse.X, mouse.Y);

        // Add an outlier so the refreshed chart moves the original point outside its hit radius.
        WriteCadence(12);
        WriteMetrics(12, 9);
        WaitPast(22);
        pin.End();
        clock.AdvanceRefreshInterval();
        fixture.Frame(() => Assert.Equal(mouse, GetPointPosition(initialSeries)));

        // Complete the refresh between frames so the click targets the chart still on screen.
        var readoutJob = typeof(FrameTimingMonitorController).GetField("readoutJob", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var refresh = Assert.IsAssignableFrom<Task>(readoutJob.GetValue(fixture.Controller));
        Assert.True(refresh.Wait(TimeSpan.FromSeconds(10)));
        Assert.Same(initial, fixture.Controller.Readout);

        io.AddMouseButtonEvent(0, true);
        fixture.Frame();
        io.AddMouseButtonEvent(0, false);
        fixture.FrameUntil(() => fixture.Controller.Readout is { } updated &&
                                 !ReferenceEquals(initial, updated) && !fixture.Controller.IsReadingHitch);

        var refreshedSeries = GetSeries(fixture.Controller.Readout!);
        Assert.Equal(2, refreshedSeries.Hitches.Length);
        Assert.Equal(expectedHitch, refreshedSeries.Hitches[0]);
        fixture.Frame(() => Assert.True(Math.Abs(mouse.Y - GetPointPosition(refreshedSeries).Y) > 9));
        var detail = Assert.IsType<FrameTimingHitchDetail>(fixture.Controller.HitchDetail);
        Assert.Equal(expectedHitch, detail.Hitch);

        FrameTimingPlotSeries GetSeries(FrameTimingRange range) => plotName switch
        {
            "Callback cadence" => range.Cadence.Single(static series => series.Source == FrameTimingSource.FrameworkUpdate).Samples,
            "Phase elapsed" => range.Phases.Single(static series => series.Span == FrameTimingSpan.Step).Samples,
            "Main UI age at replay entry" => range.MainUiAge,
            _ => throw new ArgumentOutOfRangeException(nameof(plotName)),
        };

        Vector2 GetPointPosition(FrameTimingPlotSeries series)
        {
            var plot = ImPlot.GetPlot(plotName);
            Assert.False(plot.IsNull);
            ref var x = ref plot.Axes[(int)ImAxis.X1];
            ref var y = ref plot.Axes[(int)ImAxis.Y1];
            var position = new Vector2(ToPixels(x, series.Seconds[0]), ToPixels(y, series.Milliseconds[0]));
            Assert.InRange(position.X, plot.PlotRect.Min.X, plot.PlotRect.Max.X);
            Assert.InRange(position.Y, plot.PlotRect.Min.Y, plot.PlotRect.Max.Y);
            return position;
        }

        long Tick(int offset) => session.StartedTick + (offset * tenMilliseconds);

        void WaitPast(int offset) =>
            Assert.True(SpinWait.SpinUntil(() => Stopwatch.GetTimestamp() > Tick(offset), TimeSpan.FromSeconds(10)));

        void WriteCadence(int offset)
        {
            var tick = Tick(offset);
            Write(new(session.NextOperationId(), tick, tick, thread, FrameTimingSource.FrameworkUpdate, FrameTimingSpan.FrameworkUpdate,
                FrameTimingRecordKind.Cadence));
        }

        void WriteMetrics(int offset, int duration)
        {
            var tick = Tick(offset);
            Write(new(session.NextOperationId(), tick, Tick(offset + duration), thread, FrameTimingSource.KernelPresent,
                FrameTimingSpan.Step, FrameTimingRecordKind.Elapsed, FrameTimingOutcome.Completed));
            Write(new(session.NextOperationId(), tick, tick + 1, thread, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay,
                FrameTimingRecordKind.Elapsed, FrameTimingOutcome.Completed, SnapshotId: 1, SnapshotAgeTicks: duration * tenMilliseconds));
        }

        void Write(FrameTimingRecord record)
        {
            session.Buffer.BeginWrite(out var sequence);
            session.Buffer.Write(sequence, record);
        }
    }

    private static void RunDenseNativePlotTest()
    {
        const int sampleCount = 1000;
        const int spikeIndex = sampleCount / 2;
        using var fixture = new NativeFixture(capacity: 8192);
        fixture.Controller.SetEnabled(true);
        fixture.FrameUntil(() => fixture.Controller.IsEnabled && !fixture.Controller.IsChangingCollection);
        var spikeSpan = RecordDenseHistory(fixture.Recorder, sampleCount, spikeIndex);
        fixture.Controller.SetEnabled(false);
        fixture.FrameUntil(() => fixture.Controller.Readout is { Summary.IsFinal: true });
        var range = fixture.Controller.Readout!;
        Assert.Equal(3, range.Cadence.Count(static series => series.Samples.Statistics.Count == sampleCount));
        var cadence = range.Cadence.Single(static series => series.Source == FrameTimingSource.FrameworkUpdate).Samples;
        Assert.Equal(sampleCount, cadence.Statistics.Count);
        Assert.Equal(1, cadence.Statistics.OverBudget);
        Assert.Equal(cadence.Milliseconds[spikeIndex], cadence.Statistics.Max);
        Assert.Equal(spikeSpan.StartTick, cadence.Hitches[spikeIndex].StartTick);
        Assert.Equal(sampleCount, range.MainUiAge.Statistics.Count);
        var phase = Assert.Single(range.Phases, static phase => phase.Span == FrameTimingSpan.Step);
        Assert.Equal(sampleCount, phase.Samples.Statistics.Count);

        fixture.Frame();
        var geometry = GetGeometry();
        fixture.Frame();
        Assert.Same(range, fixture.Controller.Readout);
        Assert.Equal(geometry, GetGeometry());
        // Allow room for the UI and up to two markers per bucket. Drawing all samples exceeds these limits.
        Assert.InRange(geometry.Vertices, 1, 25_000);
        Assert.InRange(geometry.Indices, 1, 60_000);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Dense monitor geometry: {geometry.Vertices:N0} vertices and {geometry.Indices:N0} indices.");

        var wideReadout = fixture.Controller.PlotReadout!;
        fixture.WindowWidth = 600;
        // Require a bucket reduction larger than the change from plot margins settling.
        const int minimumBucketDecrease = 25;
        fixture.FrameUntil(() => fixture.Controller.PlotReadout is { } readout &&
                                 readout.Buckets.Cadence <= wideReadout.Buckets.Cadence - minimumBucketDecrease &&
                                 readout.Buckets.Phase <= wideReadout.Buckets.Phase - minimumBucketDecrease &&
                                 readout.Buckets.Age <= wideReadout.Buckets.Age - minimumBucketDecrease);

        var narrowReadout = fixture.Controller.PlotReadout!;
        Assert.Same(range, narrowReadout.Range);
        Assert.InRange(narrowReadout.GetDisplay(cadence).Seconds.Length, 1, 2 * narrowReadout.Buckets.Cadence);
        Assert.InRange(narrowReadout.GetDisplay(phase.Samples).Seconds.Length, 1, 2 * narrowReadout.Buckets.Phase);
        Assert.InRange(narrowReadout.GetDisplay(range.MainUiAge).Seconds.Length, 1, 2 * narrowReadout.Buckets.Age);

        var detail = SelectPlotSample(fixture, cadence, spikeIndex);
        Assert.Equal(cadence.Hitches[spikeIndex], detail.Hitch);
        Assert.Contains(spikeSpan, detail.Records);
    }

    private static FrameTimingRecord RecordDenseHistory(FrameTimingRecorder recorder, int sampleCount, int spikeIndex)
    {
        var pin = recorder.Begin(FrameTimingSpan.FrameworkUpdate, out var admission);
        var session = admission.Session;
        var spacing = Stopwatch.Frequency / 100_000;
        var spikeDuration = Stopwatch.Frequency / 40;
        var thread = Environment.CurrentManagedThreadId;
        FrameTimingRecord spikeSpan = default;
        long lastUpdateTick = 0;
        for (var i = 0; i <= sampleCount; i++)
        {
            var tick = session.StartedTick + ((i + (i / 2)) * spacing);
            lastUpdateTick = i <= spikeIndex ? tick : tick + spikeDuration;
            var update = session.NextOperationId();
            var kernel = session.NextOperationId();
            var overlay = session.NextOperationId();
            Write(new(update, lastUpdateTick, lastUpdateTick, thread, FrameTimingSource.FrameworkUpdate,
                FrameTimingSpan.FrameworkUpdate, FrameTimingRecordKind.Cadence));
            Write(new(kernel, tick, tick, thread, FrameTimingSource.KernelPresent,
                FrameTimingSpan.KernelPresent, FrameTimingRecordKind.Cadence));
            Write(new(overlay, tick, tick, thread, FrameTimingSource.DxgiPresent,
                FrameTimingSpan.OverlayCallback, FrameTimingRecordKind.Cadence));

            if (i == spikeIndex)
            {
                spikeSpan = new(update, lastUpdateTick, lastUpdateTick + spikeDuration, thread, FrameTimingSource.FrameworkUpdate,
                    FrameTimingSpan.FrameworkNativeUpdate, FrameTimingRecordKind.Elapsed, FrameTimingOutcome.Completed);
                Write(spikeSpan);
            }

            if (i == sampleCount)
                break;

            var measurement = (1 + (i % 2)) * spacing;
            Write(new(kernel, tick, tick + measurement, thread, FrameTimingSource.KernelPresent,
                FrameTimingSpan.Step, FrameTimingRecordKind.Elapsed, FrameTimingOutcome.Completed));
            Write(new(overlay, tick, tick + spacing, thread, FrameTimingSource.DxgiPresent, FrameTimingSpan.MainReplay,
                FrameTimingRecordKind.Elapsed, FrameTimingOutcome.Completed, SnapshotId: 1, SnapshotAgeTicks: measurement));
        }

        // Wait past the last synthetic entry so the stopped readout includes it.
        while (Stopwatch.GetTimestamp() <= lastUpdateTick)
            Thread.Sleep(1);
        pin.End();
        return spikeSpan;

        void Write(FrameTimingRecord record)
        {
            session.Buffer.BeginWrite(out var sequence);
            session.Buffer.Write(sequence, record);
        }
    }

    private static (int Vertices, int Indices) GetGeometry()
    {
        var data = ImGui.GetDrawData();
        return (data.TotalVtxCount, data.TotalIdxCount);
    }

    private static FrameTimingHitchDetail SelectPlotSample(
        NativeFixture fixture, FrameTimingPlotSeries series, int index, string plotName = "Callback cadence")
    {
        var previous = fixture.Controller.HitchDetail;
        var mouse = Vector2.Zero;
        // Let the scrollbar settle before sampling plot coordinates.
        fixture.Frame();
        fixture.Frame(() =>
        {
            var plot = ImPlot.GetPlot(plotName);
            Assert.False(plot.IsNull);
            Assert.False(ImPlot.GetPlot("Phase elapsed"u8).IsNull);
            Assert.False(ImPlot.GetPlot("Main UI age at replay entry"u8).IsNull);
            ref var x = ref plot.Axes[(int)ImAxis.X1];
            ref var y = ref plot.Axes[(int)ImAxis.Y1];
            mouse = new Vector2(ToPixels(x, series.Seconds[index]), ToPixels(y, series.Milliseconds[index]));
            Assert.InRange(mouse.X, plot.PlotRect.Min.X, plot.PlotRect.Max.X);
            Assert.InRange(mouse.Y, plot.PlotRect.Min.Y, plot.PlotRect.Max.Y);
        });

        fixture.Click(mouse);
        fixture.FrameUntil(() => fixture.Controller.HitchDetail is { } detail && !ReferenceEquals(detail, previous));

        return fixture.Controller.HitchDetail!;
    }

    private static float ToPixels(ImPlotAxis axis, double value) =>
        (float)(axis.PixelMin + ((value - axis.ScaleMin) * axis.ScaleToPixel));

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => this.ticks;

        internal void AdvanceRefreshInterval() => this.ticks += TimeSpan.FromMilliseconds(500).Ticks;
    }

    private sealed class NativeFixture : IDisposable
    {
        private readonly ImGuiContextPtr previousGui;
        private readonly ImPlotContextPtr previousPlot;
        private readonly ImGuiContextPtr gui;
        private readonly ImPlotContextPtr plot;
        private readonly FrameTimingMonitorWindow window;

        public NativeFixture(
            int capacity = 64,
            Func<FrameTimingSource>? getOverlaySource = null,
            Action<FrameTimingRecorder>? beforeStopCommit = null,
            TimeProvider? timeProvider = null)
        {
            this.previousGui = ImGui.GetCurrentContext();
            this.previousPlot = ImPlot.GetCurrentContext();
            this.gui = ImGui.CreateContext();
            ImGui.SetCurrentContext(this.gui);
            ImPlot.SetImGuiContext(this.gui);
            this.plot = ImPlot.CreateContext();
            ImPlot.SetCurrentContext(this.plot);

            var io = ImGui.GetIO();
            Marshal.WriteIntPtr(Unsafe.As<ImGuiIOPtr, nint>(ref io), (int)Marshal.OffsetOf<ImGuiIO>("IniFilename"), nint.Zero);
            io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
            io.DisplaySize = new Vector2(1050, 850);
            io.DeltaTime = 1f / 60;
            io.Fonts.AddFontDefault();
            io.Fonts.Build();

            this.Recorder = new FrameTimingRecorder(capacity, beforeStopCommit);
            this.Controller = new FrameTimingMonitorController(
                new DalamudConfiguration(), this.Recorder, getOverlaySource ?? (() => FrameTimingSource.DxgiPresent), timeProvider);
            this.window = new FrameTimingMonitorWindow(this.Controller) { IsOpen = true };
        }

        public FrameTimingRecorder Recorder { get; }

        public FrameTimingMonitorController Controller { get; }

        public FrameTimingMonitorWindow Window => this.window;

        public float WindowWidth { get; set; } = 1000;

        public float WindowHeight { get; set; } = 790;

        public void Click(Vector2 mouse)
        {
            var io = ImGui.GetIO();
            io.AddMousePosEvent(mouse.X, mouse.Y);
            this.Frame();
            io.AddMouseButtonEvent(0, true);
            this.Frame();
            io.AddMouseButtonEvent(0, false);
            this.Frame();
        }

        public ImRect GetVisibleControlRect(string label, bool isSlider = false, bool inPopup = false)
        {
            var bounds = default(ImRect);
            var window = default(ImGuiWindowPtr);
            this.Frame(
                () =>
                {
                    var context = ImGui.GetCurrentContext();
                    Assert.True(context.NavIdIsAlive, $"{label} was not drawn.");
                    bounds = window.NavRectRel[(int)context.NavLayer];
                    bounds.Min += window.DC.CursorStartPos;
                    bounds.Max += window.DC.CursorStartPos;
                    var labelWidth = isSlider ? ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X : 0;
                    var clip = window.InnerClipRect;
                    Assert.True(bounds.Min.X >= clip.Min.X && bounds.Max.X + labelWidth <= clip.Max.X,
                                $"{label} spans x={bounds.Min.X}–{bounds.Max.X + labelWidth}, outside x={clip.Min.X}–{clip.Max.X}.");
                    Assert.InRange(bounds.Min.Y, clip.Min.Y, clip.Max.Y);
                    Assert.InRange(bounds.Max.Y, clip.Min.Y, clip.Max.Y);
                },
                beforeDraw: () =>
                {
                    // ImGui records a focused item's bounds before clipping it.
                    var context = ImGui.GetCurrentContext();
                    window = inPopup ? context.NavWindow : ImGuiP.GetCurrentWindowRead();
                    if (inPopup)
                    {
                        Assert.False(window.IsNull);
                        Assert.Equal(Assert.Single(context.OpenPopupStack).PopupId, window.PopupId);
                    }

                    ImGuiP.SetFocusID(ImGuiP.GetID(window, label), window);
                    context.NavIdIsAlive = false;
                });
            return bounds;
        }

        public void Frame(Action? inspect = null, Action? beforeDraw = null)
        {
            ImGui.NewFrame();
            ImGui.SetNextWindowPos(Vector2.Zero);
            ImGui.SetNextWindowSize(new Vector2(this.WindowWidth, this.WindowHeight));
            this.window.PreDraw();
            var open = ImGui.Begin(this.window.WindowName, ImGuiWindowFlags.NoSavedSettings);
            try
            {
                Assert.True(open);
                beforeDraw?.Invoke();
                this.window.Draw();
                inspect?.Invoke();
            }
            finally
            {
                ImGui.End();
                this.window.PostDraw();
                ImGui.Render();
            }
        }

        public void FrameUntil(Func<bool> condition, Action? inspect = null, Action? beforeDraw = null)
        {
            var start = Stopwatch.GetTimestamp();
            do
            {
                this.Frame(inspect, beforeDraw);
                if (condition())
                    return;

                Thread.Sleep(10);
            }
            while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(10));

            Assert.Fail(this.Controller.ReadoutError ?? this.Controller.CollectionError ?? "The timing monitor did not publish the requested view.");
        }

        public void Dispose()
        {
            this.window.StopReadout();
            this.Recorder.Shutdown();
            ImPlot.DestroyContext(this.plot);
            ImPlot.SetCurrentContext(this.previousPlot);
            ImPlot.SetImGuiContext(this.previousGui);
            ImGui.DestroyContext(this.gui);
            ImGui.SetCurrentContext(this.previousGui);
        }
    }
}
