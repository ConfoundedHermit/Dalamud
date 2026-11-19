using System;
using System.Numerics;
using System.Threading;

using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImPlot;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility.Timing;

namespace Dalamud.Interface.Internal.Windows;

/// <summary>Displays callback cadence, inclusive elapsed spans, and main UI age for a live or stopped timing session.</summary>
internal sealed class FrameTimingMonitorWindow : Window
{
    private const ImGuiTableFlags TableFlags =
        ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;

    // With the axes locked, route mouse-wheel scrolling to the monitor window.
    private const ImPlotFlags PlotFlags = ImPlotFlags.NoMouseText | ImPlotFlags.NoMenus | ImPlotFlags.NoBoxSelect | ImPlotFlags.NoChild;
    private const float PlotBucketWidth = 8f;

    private readonly FrameTimingMonitorController controller;

    // Serializes draw-time access to the controller with close and shutdown cleanup.
    private readonly Lock readoutLock = new();
    private int cadenceBuckets;
    private int phaseBuckets;
    private int ageBuckets;
    private int historySeconds = 10;
    private int targetRate = 60;
    private bool includeUncertainStopBoundary;
    private (FrameTimingSource Source, FrameTimingSpan Span)? phaseSelection;
    private SelectedPoint? selection;
    private bool drewContents;
    private volatile bool shuttingDown;

    /// <summary>Initializes a new instance of the <see cref="FrameTimingMonitorWindow"/> class.</summary>
    /// <param name="controller">The background collector and readout controller.</param>
    internal FrameTimingMonitorWindow(FrameTimingMonitorController controller)
        : base("Frame Timing Monitor###DalamudFrameTimingMonitor")
    {
        this.controller = controller;
        this.Size = new Vector2(910, 745);
        this.SizeCondition = ImGuiCond.FirstUseEver;
        this.DisableFadeInFadeOut = true;
    }

    /// <inheritdoc/>
    public override bool DrawConditions() => !this.shuttingDown;

    /// <inheritdoc/>
    public override void PreDraw() => this.drewContents = false;

    /// <inheritdoc/>
    public override void PostDraw()
    {
        // Collapsed windows still run PostDraw, so stop requesting readouts when content is hidden.
        if (!this.drewContents || !this.IsOpen)
            this.StopReadout();
    }

    /// <inheritdoc/>
    public override void OnClose() => this.StopReadout();

    /// <inheritdoc/>
    public override void Draw()
    {
        // Shutdown can begin after the host checks DrawConditions.
        if (this.shuttingDown || !this.readoutLock.TryEnter())
            return;

        try
        {
            if (this.shuttingDown)
                return;

            this.DrawContents();
            this.drewContents = true;
        }
        catch
        {
            this.ClearReadout();
            throw;
        }
        finally
        {
            this.readoutLock.Exit();
        }
    }

    /// <summary>Clears the selection and stops requesting background readouts.</summary>
    internal void StopReadout()
    {
        if (this.shuttingDown)
            return;

        lock (this.readoutLock)
        {
            if (!this.shuttingDown)
                this.ClearReadout();
        }
    }

    /// <summary>Prevents new draws and clears the readout after any in-progress draw finishes.</summary>
    internal void Shutdown()
    {
        this.shuttingDown = true;
        lock (this.readoutLock)
            this.ClearReadout();
    }

    private void ClearReadout()
    {
        this.selection = null;
        this.controller.StopReadout();
    }

    private static string SourceLabel(FrameTimingSource source) => source switch
    {
        FrameTimingSource.FrameworkUpdate => "Game update",
        FrameTimingSource.KernelPresent => "Kernel Present",
        FrameTimingSource.DxgiPresent => "DXGI Present",
        FrameTimingSource.LegacyReShadePresent => "Legacy ReShade Present",
        FrameTimingSource.ReShadePresent => "ReShade addon Present",
        FrameTimingSource.ReShadeOverlay => "ReShadeOverlay event",
        _ => "Standalone",
    };

    private static string SpanLabel(FrameTimingSpan span) => span switch
    {
        FrameTimingSpan.FrameworkUpdate => "Full game update",
        FrameTimingSpan.FrameworkManagedTick => "Managed framework tick",
        FrameTimingSpan.FrameworkGuard => "Initial framework guard",
        FrameTimingSpan.FrameworkNativeUpdate => "Original native update",
        FrameTimingSpan.KernelPresent => "Full kernel Present",
        FrameTimingSpan.KernelStepAdmission => "Step admission",
        FrameTimingSpan.KernelNativePresent => "Original kernel Present",
        FrameTimingSpan.FrameworkReacquisition => "Framework guard restoration",
        FrameTimingSpan.OverlayCallback => "Full overlay callback",
        FrameTimingSpan.MainAdmission => "Main presentation admission",
        FrameTimingSpan.MainFrameworkGuard => "Main framework guard",
        FrameTimingSpan.MainRenderGuard => "Main interface render guard",
        FrameTimingSpan.MainReadyAttempt => "Main ready attempt",
        FrameTimingSpan.MainPreQueue => "Pre-main queue drain",
        FrameTimingSpan.MainPostQueue => "Post-main queue drain",
        FrameTimingSpan.DxgiNativePresent => "Original DXGI Present",
        FrameTimingSpan.LegacyReShadeContinuation => "Legacy ReShade continuation",
        FrameTimingSpan.Step => "Backend Step",
        FrameTimingSpan.UiBuild => "UI construction",
        FrameTimingSpan.UiFinalize => "ImGui.Render finalization",
        FrameTimingSpan.StepDeviceGuard => "Step device guard",
        FrameTimingSpan.StepWriterGuard => "Step snapshot writer guard",
        FrameTimingSpan.Platform => "Platform window update",
        FrameTimingSpan.SnapshotCopy => "Main snapshot copy",
        FrameTimingSpan.SecondaryWork => "Secondary viewport work",
        FrameTimingSpan.SecondaryDraw => "Secondary viewport draw",
        FrameTimingSpan.SecondaryPresent => "Secondary viewport native Present",
        FrameTimingSpan.Retirement => "Resource retirement",
        FrameTimingSpan.MainRender => "Backend main render",
        FrameTimingSpan.ReplayDeviceGuard => "Replay device guard",
        FrameTimingSpan.ReplayRenderGuard => "Replay render guard",
        FrameTimingSpan.ReplayReaderGuard => "Replay snapshot reader guard",
        FrameTimingSpan.MainReplay => "Main snapshot replay",
        _ => "Unknown boundary",
    };

    private static string OutcomeLabel(FrameTimingOutcome outcome) => outcome switch
    {
        FrameTimingOutcome.Test => "Test",
        FrameTimingOutcome.OtherTarget => "Different target",
        FrameTimingOutcome.BackendUnavailable => "Backend unavailable",
        FrameTimingOutcome.MissingSnapshot => "Missing snapshot",
        _ => outcome.ToString(),
    };

    private static void DrawRangeStatus(FrameTimingRange range)
    {
        var summary = range.Summary;
        var frequency = summary.ClockFrequency;
        var retainedSeconds = (summary.CutoffTick - summary.RetainedFromTick) / (double)frequency;
        var elapsedSeconds = (summary.CutoffTick - summary.StartedTick) / (double)frequency;
        ImGui.TextUnformatted($"Session {summary.SessionId} · Began {elapsedSeconds:F2} s before readout cutoff");
        ImGui.TextUnformatted($"Selected {range.DurationSeconds:F2} s · Retained {retainedSeconds:F2} s · Capacity {summary.Capacity:N0} records");
        ImGui.TextUnformatted($"Dropped {summary.DroppedRecords:N0} · Unread {summary.UnreadRecords:N0} this read · Evicted {summary.EvictedRecords:N0}");
        if (!range.CoverageComplete)
            ImGui.TextWrapped("Record coverage is incomplete in the selected range."u8);
    }

    private static void SetupPlot(double duration, ReadOnlySpan<byte> yLabel, double maxMilliseconds)
    {
        ImPlot.SetupAxes("Seconds from range start"u8, yLabel, ImPlotAxisFlags.Lock, ImPlotAxisFlags.Lock);
        var limit = Math.Max(0.1, maxMilliseconds);
        ImPlot.SetupAxesLimits(0, duration, -limit * 0.04, limit * 1.08, ImPlotCond.Always);
    }

    // Use the item group's ID as the seed because ImPlot pushes it only after setup finishes.
    private static ImPlotItemPtr GetPlotItem(ref ImPlotItemGroup items, ImU8String label) =>
        ImPlot.GetItem(ref items, ImGuiP.GetIDWithSeed(label, items.ID));

    private static bool IsPlotItemVisible(ref ImPlotItemGroup items, ImU8String label)
    {
        var item = GetPlotItem(ref items, label);
        return item.IsNull || item.Show;
    }

    private static void DrawCadenceSeriesMenu(ReadOnlySpan<byte> title, ReadOnlySpan<byte> budgetLabel, FrameTimingRange range, float width)
    {
        var plot = ImPlot.GetPlot(title);
        var preview = "Series"u8;
        var menuWidth = ImGui.CalcTextSize(preview).X + (2 * ImGui.GetStyle().FramePadding.X) + ImGui.GetFrameHeight();
        ImGui.SetNextItemWidth(Math.Max(1, Math.Min(width, menuWidth)));
        using var combo = ImRaii.Combo("##Cadence series"u8, preview);
        if (!combo || plot.IsNull)
            return;

        ref var items = ref plot.Items;
        foreach (var cadence in range.Cadence)
        {
            if (!cadence.Samples.Hitches.IsEmpty)
                DrawToggle(ref items, SourceLabel(cadence.Source));
        }

        DrawToggle(ref items, budgetLabel);

        static void DrawToggle(ref ImPlotItemGroup items, ImU8String label)
        {
            var item = GetPlotItem(ref items, label);
            if (item.IsNull)
                return;

            using var color = ImRaii.PushColor(ImGuiCol.CheckMark, item.Color);
            ImGui.Checkbox(label, ref item.Show);
        }
    }

    private static int GetPlotBucketCount(float width) => Math.Max(1, (int)(width / PlotBucketWidth));

    private static unsafe void PlotPoints(string label, FrameTimingPlotDisplay display)
    {
        fixed (double* x = display.Seconds.Span)
        fixed (double* y = display.Milliseconds.Span)
        {
            ImPlot.SetNextMarkerStyle(ImPlotMarker.Circle, 2f, weight: 0f);
            ImPlot.PlotScatter(label, x, y, display.Seconds.Length);
        }
    }

    private static int FindPoint(FrameTimingPlotDisplay display, Vector2 mouse, ref float bestDistance)
    {
        var seconds = display.Seconds.Span;
        var milliseconds = display.Milliseconds.Span;
        // Find the candidate interval by sorted X coordinate, then compare distances in screen pixels.
        var radius = MathF.Sqrt(bestDistance);
        var firstSecond = ImPlot.PixelsToPlot(mouse.X - radius, mouse.Y).X;
        var lastSecond = ImPlot.PixelsToPlot(mouse.X + radius, mouse.Y).X;
        var low = 0;
        var high = seconds.Length;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (seconds[middle] < firstSecond)
                low = middle + 1;
            else
                high = middle;
        }

        var nearest = -1;
        for (var i = low; i < seconds.Length && seconds[i] <= lastSecond; i++)
        {
            var point = ImPlot.PlotToPixels(seconds[i], milliseconds[i]);
            var distance = Vector2.DistanceSquared(mouse, point);
            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            nearest = i;
        }

        return nearest < 0 || display.SourceIndices.IsEmpty ? nearest : display.SourceIndices.Span[nearest];
    }

    private static void DrawCadenceStatistics(FrameTimingRange range, FrameTimingSource? activeOverlaySource)
    {
        if (!ImGui.CollapsingHeader("Cadence statistics"u8, ImGuiTreeNodeFlags.DefaultOpen))
            return;

        ImGui.TextWrapped("Overlay cadence measures raw callback or event entries, which can include test calls and other targets."u8);
        ImGui.TextUnformatted($"Frame budget {range.BudgetMilliseconds:F2} ms · Durations in ms");

        using (var table = ImRaii.Table("Cadence statistics table"u8, 10, TableFlags))
        {
            if (!table)
                return;

            ImGui.TableSetupColumn("Source"u8);
            ImGui.TableSetupColumn("Entries"u8);
            SetupDistributionColumns();
            ImGui.TableSetupColumn("> Budget"u8);
            ImGui.TableSetupColumn("Since last entry"u8);
            ImGui.TableSetupColumn("Coverage"u8);
            ImGui.TableHeadersRow();
            foreach (var cadence in range.Cadence)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(SourceLabel(cadence.Source));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{cadence.EntryCount:N0}");
                DrawDistributionColumns(cadence.Samples.Statistics);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{cadence.Samples.Statistics.OverBudget:N0}");
                ImGui.TableNextColumn();
                if (cadence.TrailingGapMilliseconds is { } gap)
                {
                    if (cadence.TrailingGapIsLowerBound)
                        ImGui.TextUnformatted($">= {gap:F2} ms");
                    else
                        ImGui.TextUnformatted($"{gap:F2} ms (incomplete)");
                }
                else
                {
                    ImGui.TextDisabled("—"u8);
                }

                ImGui.TableNextColumn();
                if (cadence.Source is not (FrameTimingSource.FrameworkUpdate or FrameTimingSource.KernelPresent) &&
                    cadence.EntryCount == 0 && activeOverlaySource is { } source && cadence.Source != source)
                {
                    ImGui.TextDisabled("Unavailable"u8);
                }
                else if (!cadence.CoverageComplete)
                {
                    var unconfirmed = cadence.UnconfirmedIntervalCount;
                    if (unconfirmed == 0)
                        ImGui.TextUnformatted("Incomplete"u8);
                    else
                        ImGui.TextWrapped($"Incomplete ({unconfirmed:N0} unconfirmed {(unconfirmed == 1 ? "interval" : "intervals")})");
                }
                else
                {
                    ImGui.TextUnformatted(cadence.EntryCount == 0 ? "No entries" : "Complete");
                }
            }
        }

        foreach (var cadence in range.Cadence)
        {
            if (cadence.UnfinishedEntry is { } entry)
            {
                ImGui.TextWrapped(
                    $"{SourceLabel(cadence.Source)} entry on managed thread {entry.ThreadId} had no recorded completion before the selected range ended.");
            }
        }
    }

    private static void DrawDistributionStatistics(FrameTimingRange range)
    {
        if (!ImGui.CollapsingHeader("Phase and age statistics"u8))
            return;

        ImGui.TextWrapped("Elapsed samples include nested work, waiting, and possible descheduling. Spans can overlap across threads."u8);
        ImGui.TextUnformatted($"Completed in-range spans · Durations in ms · {(range.CoverageComplete ? "Complete coverage" : "Incomplete coverage")}");
        using var table = ImRaii.Table("Phase and age statistics table"u8, 7, TableFlags);
        if (!table)
            return;

        ImGui.TableSetupColumn("Boundary"u8);
        ImGui.TableSetupColumn("Source"u8);
        SetupDistributionColumns();
        ImGui.TableHeadersRow();
        foreach (var phase in range.Phases)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(SpanLabel(phase.Span));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(SourceLabel(phase.Source));
            DrawDistributionColumns(phase.Samples.Statistics);
        }

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted("Main UI age at replay entry"u8);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted("All replay sources"u8);
        DrawDistributionColumns(range.MainUiAge.Statistics);
    }

    private static void SetupDistributionColumns()
    {
        ImGui.TableSetupColumn("Samples"u8);
        ImGui.TableSetupColumn("p50"u8);
        ImGui.TableSetupColumn("p95"u8);
        ImGui.TableSetupColumn("p99"u8);
        ImGui.TableSetupColumn("Max"u8);
    }

    private static void DrawDistributionColumns(FrameTimingStatistics statistics)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{statistics.Count:N0}");
        DrawStatistic(statistics.P50);
        DrawStatistic(statistics.P95);
        DrawStatistic(statistics.P99);
        DrawStatistic(statistics.Max);
    }

    private static void DrawStatistic(double? sample)
    {
        ImGui.TableNextColumn();
        if (sample is { } milliseconds)
            ImGui.TextUnformatted($"{milliseconds:F2}");
        else
            ImGui.TextDisabled("—"u8);
    }

    private static void DrawReplayStatistics(FrameTimingRange range)
    {
        if (!ImGui.CollapsingHeader("Main replay and overlay outcomes"u8))
            return;

        ImGui.TextUnformatted(
            $"Ready attempts {range.ReadyAttempts:N0} · Publications {range.Publications:N0} · Completed replays {range.CompletedReplays:N0}");
        ImGui.TextWrapped(
            $"Unknown replay ages {range.UnknownReplayAges:N0} · Completed replays with unknown snapshot IDs {range.UnknownReplaySnapshots:N0}");
        ImGui.TextUnformatted($"Observed snapshot reuse · {(range.CoverageComplete ? "Complete range coverage" : "Incomplete range coverage")}");
        ImGui.TextDisabled("Reuse counts apply to the selected range."u8);
        if (range.SnapshotReuse.IsEmpty)
        {
            ImGui.TextDisabled("No identified snapshots were published or replayed in this range."u8);
        }
        else
        {
            var tableHeight = 7 * ImGui.GetTextLineHeightWithSpacing();
            using var table = ImRaii.Table(
                "Observed snapshot reuse table"u8,
                2,
                TableFlags | ImGuiTableFlags.ScrollY,
                new Vector2(0, tableHeight));
            if (table)
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Snapshot ID"u8);
                ImGui.TableSetupColumn("Observed completed replays"u8);
                ImGui.TableHeadersRow();
                ImGuiClip.ClippedDraw(range.SnapshotReuse, static snapshot =>
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{snapshot.SnapshotId}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{snapshot.CompletedReplays:N0}");
                }, -1f);
            }
        }

        if (range.OverlayOutcomes.IsEmpty)
        {
            ImGui.TextDisabled("No completed overlay callbacks in this range."u8);
            return;
        }

        using var outcomes = ImRaii.Table("Overlay outcomes table"u8, 3, TableFlags);
        if (!outcomes)
            return;

        ImGui.TableSetupColumn("Overlay source"u8);
        ImGui.TableSetupColumn("Callback outcome"u8);
        ImGui.TableSetupColumn("Count"u8);
        ImGui.TableHeadersRow();
        foreach (var outcome in range.OverlayOutcomes)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(SourceLabel(outcome.Source));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(OutcomeLabel(outcome.Outcome));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{outcome.Count:N0}");
        }
    }

    private void DrawContents()
    {
        if (this.DrawControls())
            this.ClearSelection();

        // Estimate the initial bucket counts until ImPlot reports the actual chart widths.
        if (this.cadenceBuckets == 0)
            this.cadenceBuckets = this.phaseBuckets = this.ageBuckets = GetPlotBucketCount(ImGui.GetContentRegionAvail().X);

        // On mouse-down, hit-test the readout shown when the click began.
        var deferPublication = ImGui.IsMouseClicked(ImGuiMouseButton.Left) &&
                               ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        this.controller.RequestReadout(
            this.historySeconds,
            this.targetRate,
            this.includeUncertainStopBoundary,
            new(this.cadenceBuckets, this.phaseBuckets, this.ageBuckets),
            deferPublication);

        if (this.controller.CollectionError is { } collectionError)
            ImGui.TextWrapped(collectionError);
        var readoutError = this.controller.ReadoutError;
        if (readoutError is not null)
            ImGui.TextWrapped(readoutError);

        if (this.controller.PlotReadout is not { } readout)
        {
            if (this.controller.HasConfirmedNoSession)
                ImGui.TextDisabled("No timing session has been recorded."u8);
            else if (readoutError is null)
                ImGui.TextDisabled("Preparing the timing readout…"u8);

            return;
        }

        var range = readout.Range;
        if (this.selection is { } selected && selected.SessionId != range.Summary.SessionId)
            this.selection = null;

        DrawRangeStatus(range);
        var phase = this.DrawPhaseSelector(range);

        using (ImRaii.AlignedPlots("Frame timing charts"u8))
        {
            this.DrawCadencePlot(readout);
            this.DrawMetricPlot(
                "Phase elapsed"u8, "Elapsed (ms)"u8, readout, phase?.Samples, "Elapsed span", "No completed phase samples"u8,
                ref this.phaseBuckets, labelSelectionBySpan: true);
            this.DrawMetricPlot(
                "Main UI age at replay entry"u8, "Age (ms)"u8, readout, range.MainUiAge, "Main UI age", "No known replay ages"u8,
                ref this.ageBuckets);
        }

        this.DrawHitchDetails();
        DrawCadenceStatistics(range, this.controller.ActiveOverlaySource);
        DrawDistributionStatistics(range);
        DrawReplayStatistics(range);
    }

    private bool DrawControls()
    {
        var changing = this.controller.IsChangingCollection;
        var collecting = this.controller.IsEnabled;
        using (ImRaii.Disabled(changing))
        {
            if (ImGui.Checkbox("Enable monitoring"u8, ref collecting))
                this.controller.SetEnabled(collecting);
        }

        ImGui.SameLine();
        if (changing)
            ImGui.TextDisabled("Changing collection…"u8);
        else
            ImGui.TextDisabled(this.controller.IsEnabled ? "Collecting" : "Off");

        var startup = this.controller.EnableOnStartup;
        if (ImGui.Checkbox("Enable on startup"u8, ref startup))
            this.controller.EnableOnStartup = startup;
        ImGui.SameLine();
        ImGui.TextDisabled("Applies on next launch"u8);

        if (this.controller.ActiveOverlaySource is { } source)
            ImGui.TextUnformatted($"Active overlay callback: {SourceLabel(source)}");
        else
            ImGui.TextDisabled("Active overlay callback unavailable"u8);

        var historyLabel = "Visible history"u8;
        var rateLabel = "Target rate"u8;
        var style = ImGui.GetStyle();
        var sliderWidth = Math.Max(1, MathF.Floor(9 * ImGui.GetFontSize()));
        var historyLabelWidth = ImGui.CalcTextSize(historyLabel).X + style.ItemInnerSpacing.X;
        var rateLabelWidth = ImGui.CalcTextSize(rateLabel).X + style.ItemInnerSpacing.X;
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var rowWidth = (2 * sliderWidth) + historyLabelWidth + rateLabelWidth + style.ItemSpacing.X;

        ImGui.SetNextItemWidth(Math.Clamp(availableWidth - historyLabelWidth, 1, sliderWidth));
        var settingsChanged = ImGui.SliderInt(historyLabel, ref this.historySeconds, 1, 60, "%d s"u8, ImGuiSliderFlags.AlwaysClamp);
        if (rowWidth <= availableWidth)
            ImGui.SameLine();

        ImGui.SetNextItemWidth(Math.Clamp(ImGui.GetContentRegionAvail().X - rateLabelWidth, 1, sliderWidth));
        settingsChanged |= ImGui.SliderInt(rateLabel, ref this.targetRate, 10, 500, "%d fps"u8, ImGuiSliderFlags.AlwaysClamp);

        if (this.controller.IsEnabled)
        {
            settingsChanged |= this.includeUncertainStopBoundary;
            this.includeUncertainStopBoundary = false;
        }
        else if (this.controller.LatestSummary is { CanExcludeUncertainStopBoundary: true } summary)
        {
            var boundaryMilliseconds = (summary.CutoffTick - summary.AdmissionConfirmedThroughTick) * 1000.0 / summary.ClockFrequency;
            settingsChanged |= ImGui.Checkbox("Include uncertain stop boundary"u8, ref this.includeUncertainStopBoundary);
            ImGui.SameLine();
            ImGui.TextDisabled($"{boundaryMilliseconds:G3} ms before the stop cutoff");
        }

        return settingsChanged;
    }

    private FrameTimingPhaseView? DrawPhaseSelector(FrameTimingRange range)
    {
        var key = this.phaseSelection ?? (FrameTimingSource.KernelPresent, FrameTimingSpan.Step);
        FrameTimingPhaseView? selected = null;
        foreach (var phase in range.Phases)
        {
            if ((phase.Source, phase.Span) == key)
            {
                selected = phase;
                break;
            }
        }

        // Keep explicit phase selections across ranges, including ranges without that phase.
        if (selected is null && this.phaseSelection is null && !range.Phases.IsEmpty)
            selected = range.Phases[0];

        var preview = selected is { } available
            ? $"{SpanLabel(available.Span)} / {SourceLabel(available.Source)}"
            : $"{SpanLabel(key.Item2)} / {SourceLabel(key.Item1)} (unavailable)";
        ImGui.SetNextItemWidth(27 * ImGui.GetFontSize());
        if (!ImGui.BeginCombo("Plotted phase", preview))
            return selected;

        try
        {
            foreach (var phase in range.Phases)
            {
                if (ImGui.Selectable($"{SpanLabel(phase.Span)} / {SourceLabel(phase.Source)}", phase == selected))
                {
                    this.phaseSelection = (phase.Source, phase.Span);
                    selected = phase;
                }
            }
        }
        finally
        {
            ImGui.EndCombo();
        }

        return selected;
    }

    private unsafe void DrawCadencePlot(FrameTimingPlotReadout readout)
    {
        var title = "Callback cadence"u8;
        var range = readout.Range;
        var budget = range.BudgetMilliseconds;
        var budgetLabel = "Frame budget"u8;
        var style = ImPlot.GetStyle();
        var plotSize = new Vector2(-1, 15 * ImGui.GetFontSize());
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var plotWidth = Math.Min(availableWidth, ImGuiP.CalcItemSize(plotSize, 0, 0).X);
        var legendIconWidth = ImGui.GetTextLineHeight();
        // Allow one pixel for ImPlot to round the centered legend's position.
        var legendPadding = (2 * style.LegendInnerPadding.X) + 1;
        var widestEntry = legendIconWidth + ImGui.CalcTextSize(budgetLabel).X;
        var horizontalLegendWidth = legendPadding + widestEntry;
        foreach (var cadence in range.Cadence)
        {
            if (cadence.Samples.Hitches.IsEmpty)
                continue;

            var entryWidth = legendIconWidth + ImGui.CalcTextSize(SourceLabel(cadence.Source)).X;
            widestEntry = Math.Max(widestEntry, entryWidth);
            horizontalLegendWidth += style.LegendSpacing.X + entryWidth;
        }

        var useSeriesMenu = legendPadding + widestEntry > plotWidth;
        if (useSeriesMenu)
            DrawCadenceSeriesMenu(title, budgetLabel, range, availableWidth);

        using var plot = ImRaii.Plot(title, plotSize, PlotFlags | (useSeriesMenu ? ImPlotFlags.NoLegend : ImPlotFlags.None));
        if (!plot)
            return;

        ref var items = ref ImPlot.GetCurrentPlot().Items;
        var maxMilliseconds = IsPlotItemVisible(ref items, budgetLabel) ? budget : 0;
        foreach (var cadence in range.Cadence)
        {
            if (cadence.Samples.Statistics.Max is { } seriesMax && IsPlotItemVisible(ref items, SourceLabel(cadence.Source)))
                maxMilliseconds = Math.Max(maxMilliseconds, seriesMax);
        }

        SetupPlot(readout.DurationSeconds, "Interval (ms)"u8, maxMilliseconds);
        if (!useSeriesMenu)
        {
            var legendFlags = ImPlotLegendFlags.Outside;
            if (horizontalLegendWidth <= plotWidth)
                legendFlags |= ImPlotLegendFlags.Horizontal;
            ImPlot.SetupLegend(ImPlotLocation.North, legendFlags);
        }

        this.cadenceBuckets = GetPlotBucketCount(ImPlot.GetPlotSize().X);
        var clicked = ImPlot.IsPlotHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        var mouse = clicked ? ImGui.GetMousePos() : default;
        var distance = 81f;
        (FrameTimingPlotSeries Series, int Index)? found = null;
        foreach (var cadence in range.Cadence)
        {
            if (cadence.Samples.Hitches.IsEmpty)
                continue;

            var display = readout.GetDisplay(cadence.Samples);
            var label = SourceLabel(cadence.Source);
            PlotPoints(label, display);
            if (!clicked || !ImPlot.GetItem(label).Show)
                continue;

            var index = FindPoint(display, mouse, ref distance);
            if (index >= 0)
                found = (cadence.Samples, index);
        }

        ImPlot.SetNextLineStyle(new Vector4(0.94f, 0.75f, 0.37f, 1), 1.5f);
        ImPlot.PlotInfLines(budgetLabel, &budget, 1, ImPlotInfLinesFlags.Horizontal);
        this.DrawSelectedMarker(range);

        if (found is { } selected)
        {
            this.Select(
                range,
                selected.Series.Hitches[selected.Index],
                "Cadence interval",
                selected.Series.Milliseconds[selected.Index],
                selected.Series.Hitches[selected.Index].EndTick);
        }
    }

    private void DrawMetricPlot(
        ReadOnlySpan<byte> title,
        ReadOnlySpan<byte> axis,
        FrameTimingPlotReadout readout,
        FrameTimingPlotSeries? samples,
        string seriesLabel,
        ReadOnlySpan<byte> emptyMessage,
        ref int bucketCount,
        bool labelSelectionBySpan = false)
    {
        using var plot = ImRaii.Plot(title, new Vector2(-1, 8 * ImGui.GetFontSize()), PlotFlags | ImPlotFlags.NoLegend);
        if (!plot)
            return;

        var range = readout.Range;
        SetupPlot(readout.DurationSeconds, axis, samples?.Statistics.Max ?? 0);
        bucketCount = GetPlotBucketCount(ImPlot.GetPlotSize().X);
        if (samples is not { Hitches.IsEmpty: false } series)
        {
            ImPlot.PlotText(emptyMessage, range.DurationSeconds / 2, 0.05);
            this.DrawSelectedMarker(range);
            return;
        }

        var display = readout.GetDisplay(series);
        PlotPoints(seriesLabel, display);
        this.DrawSelectedMarker(range);

        if (ImPlot.IsPlotHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var distance = 81f;
            var index = FindPoint(display, ImGui.GetMousePos(), ref distance);
            if (index >= 0)
            {
                var hitch = series.Hitches[index];
                var selectionLabel = labelSelectionBySpan ? SpanLabel(hitch.Span) : seriesLabel;
                this.Select(range, hitch, selectionLabel, series.Milliseconds[index], hitch.StartTick);
            }
        }
    }

    private unsafe void DrawSelectedMarker(FrameTimingRange range)
    {
        if (this.selection is not { } selected || selected.PlotTick < range.StartTick || selected.PlotTick >= range.EndTick)
            return;

        var seconds = (selected.PlotTick - range.StartTick) / (double)range.Summary.ClockFrequency;
        ImPlot.SetNextLineStyle(new Vector4(0.95f, 0.95f, 0.95f, 0.8f));
        ImPlot.PlotInfLines("##Selected time"u8, &seconds, 1);
    }

    private void Select(FrameTimingRange range, FrameTimingHitch hitch, string metric, double milliseconds, long plotTick)
    {
        this.selection = new(range.Summary.SessionId, range.Summary.StartedTick, range.Summary.ClockFrequency, hitch, metric, milliseconds, plotTick);
        this.controller.RequestHitch(range, hitch);
    }

    private void ClearSelection()
    {
        this.selection = null;
        this.controller.ClearHitch();
    }

    private void DrawHitchDetails()
    {
        if (this.selection is not { } selected)
        {
            ImGui.TextDisabled("Click a chart point to inspect nearby measured spans."u8);
            return;
        }

        var sessionTime = (selected.PlotTick - selected.SessionStartTick) / (double)selected.ClockFrequency;
        ImU8String heading = $"{selected.Metric} {selected.Milliseconds:F3} ms · {SourceLabel(selected.Hitch.Source)} · {sessionTime:F3} s from session start";
        var clearLabel = "Clear selection"u8;
        var style = ImGui.GetStyle();
        var clearButtonWidth = ImGui.CalcTextSize(clearLabel).X + (2 * style.FramePadding.X);
        var rowWidth = ImGui.CalcTextSize(heading.Span).X + style.ItemSpacing.X + clearButtonWidth;
        if (rowWidth <= ImGui.GetContentRegionAvail().X)
        {
            ImGui.TextUnformatted(heading);
            ImGui.SameLine();
        }
        else
        {
            ImGui.TextWrapped(heading);
        }

        if (ImGui.SmallButton(clearLabel))
        {
            this.ClearSelection();
            return;
        }

        if (this.controller.HitchDetail is not { } detail ||
            detail.SessionId != selected.SessionId ||
            detail.Hitch != selected.Hitch)
        {
            if (this.controller.HitchError is { } error)
                ImGui.TextDisabled(error);
            else if (this.controller.IsReadingHitch)
                ImGui.TextDisabled("Reading nearby measured spans…"u8);
            else
                ImGui.TextDisabled("Hitch details unavailable."u8);
            return;
        }

        if (detail.Records.IsEmpty)
        {
            ImGui.TextDisabled("No completed spans observed near the selection."u8);
            return;
        }

        if (detail.Records.Length == FrameTimingRange.MaxHitchRecords)
            ImGui.TextDisabled($"Showing up to {FrameTimingRange.MaxHitchRecords} nearby spans.");

        ImGui.TextDisabled("Offsets are relative to the selected point. Elapsed times are inclusive."u8);
        using var table = ImRaii.Table(
            "Selected span details"u8,
            9,
            TableFlags | ImGuiTableFlags.ScrollY,
            new Vector2(0, Math.Min(9, detail.Records.Length + 1) * ImGui.GetTextLineHeightWithSpacing()));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Boundary"u8);
        ImGui.TableSetupColumn("Source"u8);
        ImGui.TableSetupColumn("Start offset (ms)"u8);
        ImGui.TableSetupColumn("Elapsed (ms)"u8);
        ImGui.TableSetupColumn("Managed thread"u8);
        ImGui.TableSetupColumn("Outcome"u8);
        ImGui.TableSetupColumn("Target"u8);
        ImGui.TableSetupColumn("Snapshot"u8);
        ImGui.TableSetupColumn("Native status"u8);
        ImGui.TableHeadersRow();
        var tickMilliseconds = 1000d / selected.ClockFrequency;
        ImGuiClip.ClippedDraw(detail.Records, record =>
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(record.Kind == FrameTimingRecordKind.Publication ? "Main snapshot publication" : SpanLabel(record.Span));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(SourceLabel(record.Source));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{(record.StartTick - selected.PlotTick) * tickMilliseconds:F3}");
            ImGui.TableNextColumn();
            if (record.Kind == FrameTimingRecordKind.Elapsed)
                ImGui.TextUnformatted($"{(record.EndTick - record.StartTick) * tickMilliseconds:F3}");
            else
                ImGui.TextDisabled("—"u8);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{record.ThreadId}");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(OutcomeLabel(record.Outcome));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(record.Target.ToString());
            ImGui.TableNextColumn();
            if (record.SnapshotId is { } snapshotId)
                ImGui.TextUnformatted($"{snapshotId}");
            else if (record.Span == FrameTimingSpan.MainReplay)
                ImGui.TextDisabled("Unknown"u8);
            else
                ImGui.TextDisabled("—"u8);
            ImGui.TableNextColumn();
            if (record.NativeStatus is { } status)
                ImGui.TextUnformatted($"0x{status:X8}");
            else
                ImGui.TextDisabled("—"u8);
        }, -1f);
    }

    private readonly record struct SelectedPoint(
        long SessionId,
        long SessionStartTick,
        long ClockFrequency,
        FrameTimingHitch Hitch,
        string Metric,
        double Milliseconds,
        long PlotTick);
}
