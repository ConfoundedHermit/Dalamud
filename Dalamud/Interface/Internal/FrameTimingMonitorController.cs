using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Utility.Timing;

namespace Dalamud.Interface.Internal;

/// <summary>
/// Controls frame timing collection and publishes immutable readouts for the monitor.
/// </summary>
/// <remarks>
/// Collection startup, range analysis, and hitch analysis run in background jobs.
/// The window serializes calls, and the controller publishes completed results during drawing.
/// </remarks>
internal sealed class FrameTimingMonitorController
{
    private static readonly ModuleLog Log = ModuleLog.Create<FrameTimingMonitorController>();
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    private readonly DalamudConfiguration configuration;
    private readonly FrameTimingRecorder recorder;
    private readonly Func<FrameTimingSource> getOverlaySource;
    private readonly TimeProvider timeProvider;
    private readonly TaskScheduler hitchScheduler;

    private Task<string?>? enableJob;
    private Task? readoutJob;
    private ReadoutRequest? readoutRequest;
    private Task<HitchResult?>? hitchJob;
    private HitchRequest? requestedHitch;
    private string? hitchError;
    private int hitchGeneration;
    private long? lastReadoutTick;
    private ViewSettings? requestedSettings;
    private ViewSettings? publishedSettings;
    private FrameTimingPlotReadout? publishedReadout;

    /// <summary>Initializes a new instance of the <see cref="FrameTimingMonitorController"/> class.</summary>
    /// <param name="configuration">The configuration that stores the startup preference.</param>
    /// <param name="recorder">The timing recorder controlled by the window.</param>
    /// <param name="getOverlaySource">Reads the installed overlay callback source.</param>
    /// <param name="timeProvider">The clock used to limit the readout refresh rate.</param>
    /// <param name="hitchScheduler">The scheduler used for hitch analysis.</param>
    internal FrameTimingMonitorController(
        DalamudConfiguration configuration,
        FrameTimingRecorder recorder,
        Func<FrameTimingSource> getOverlaySource,
        TimeProvider? timeProvider = null,
        TaskScheduler? hitchScheduler = null)
    {
        this.configuration = configuration;
        this.recorder = recorder;
        this.getOverlaySource = getOverlaySource;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.hitchScheduler = hitchScheduler ?? TaskScheduler.Default;

        if (configuration.EnableFrameTimingOnStartup && !this.recorder.IsEnabled)
            this.CollectionError = "Frame timing collection could not start during launch.";
    }

    /// <summary>Gets a value indicating whether collection is active.</summary>
    internal bool IsEnabled => this.recorder.IsEnabled;

    /// <summary>Gets a value indicating whether the window has yet to process a collection start result.</summary>
    internal bool IsChangingCollection => this.enableJob is not null;

    /// <summary>Gets or sets a value indicating whether collection should begin at next launch.</summary>
    internal bool EnableOnStartup
    {
        get => this.configuration.EnableFrameTimingOnStartup;
        set
        {
            if (this.configuration.EnableFrameTimingOnStartup == value)
                return;

            this.configuration.EnableFrameTimingOnStartup = value;
            this.configuration.QueueSave();
        }
    }

    /// <summary>Gets the active overlay callback, if one has been installed.</summary>
    internal FrameTimingSource? ActiveOverlaySource
    {
        get
        {
            var source = this.getOverlaySource();
            return source == FrameTimingSource.None ? null : source;
        }
    }

    /// <summary>Gets the most recent collection control failure.</summary>
    internal string? CollectionError { get; private set; }

    /// <summary>Gets the read failure for the requested settings, if any.</summary>
    internal string? ReadoutError { get; private set; }

    /// <summary>Gets a value indicating whether a successful read found no timing session.</summary>
    internal bool HasConfirmedNoSession =>
        !this.IsChangingCollection && this.SettingsMatch && this.publishedReadout is null;

    /// <summary>Gets the error for the selected hitch, if reading it failed.</summary>
    internal string? HitchError => this.hitchError;

    /// <summary>Gets a value indicating whether a selected hitch is being read.</summary>
    internal bool IsReadingHitch => this.requestedHitch is not null || this.hitchJob is not null;

    /// <summary>Gets the immutable analysis for the requested settings.</summary>
    internal FrameTimingRange? Readout => this.PlotReadout?.Range;

    /// <summary>Gets the published range and geometry when they match the requested settings.</summary>
    internal FrameTimingPlotReadout? PlotReadout => this.SettingsMatch ? this.publishedReadout : null;

    /// <summary>Gets the latest published session summary, regardless of the requested range.</summary>
    internal FrameTimingSummary? LatestSummary => this.publishedReadout?.Range.Summary;

    /// <summary>Gets the span details published for the current selection.</summary>
    internal FrameTimingHitchDetail? HitchDetail { get; private set; }

    private bool SettingsMatch => this.publishedSettings is not null && this.publishedSettings == this.requestedSettings;

    /// <summary>Starts collection in the background or stops admitting new probes immediately.</summary>
    /// <param name="enabled">Whether to collect timing records.</param>
    internal void SetEnabled(bool enabled)
    {
        if (this.IsChangingCollection || this.IsEnabled == enabled)
            return;

        this.CollectionError = null;
        if (!enabled)
        {
            this.recorder.SetEnabled(false);
            this.InvalidateReadout();
            return;
        }

        this.enableJob = Task.Run(() =>
        {
            try
            {
                return this.recorder.SetEnabled(true) ? null : "Collection could not start. Timing storage may still be in use.";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not start frame timing collection");
                return "Could not start frame timing collection.";
            }
        });
    }

    /// <summary>Updates the monitor from completed jobs and schedules a readout refresh when needed.</summary>
    /// <param name="historySeconds">The trailing duration to display, in seconds.</param>
    /// <param name="targetHz">The rate used for the frame budget.</param>
    /// <param name="includeUncertainStopBoundary">Whether to extend stopped history to the sampled cutoff when the stop boundary is uncertain.</param>
    /// <param name="plotBuckets">The requested horizontal resolution of the charts.</param>
    /// <param name="deferPublication">Whether to defer completed plot work so hit testing uses the displayed geometry.</param>
    internal void RequestReadout(
        double historySeconds,
        double targetHz,
        bool includeUncertainStopBoundary = false,
        FrameTimingPlotBuckets plotBuckets = default,
        bool deferPublication = false)
    {
        var settings = new ViewSettings(historySeconds, targetHz, includeUncertainStopBoundary);
        if (this.requestedSettings != settings)
        {
            this.requestedSettings = settings;
            this.ReadoutError = null;
        }

        if (this.enableJob is { IsCompletedSuccessfully: true } job)
        {
            this.enableJob = null;
            this.CollectionError = job.Result;
            if (this.CollectionError is null)
            {
                this.InvalidateReadout();
                this.publishedReadout = null;
                this.ClearHitch();
            }
        }

        this.UpdateHitchDetail();
        if (!deferPublication && this.readoutJob is { IsCompleted: true })
        {
            this.readoutJob = null;
            var completedRequest = this.readoutRequest!;
            this.readoutRequest = null;
            if (completedRequest.Settings == settings && completedRequest.Result is { } result)
            {
                this.ReadoutError = result.Error;
                if (result.Error is null)
                {
                    this.publishedSettings = settings;
                    this.publishedReadout = result.Readout;
                }
            }
        }

        if (this.readoutJob is not null || this.IsChangingCollection)
            return;

        // A final summary needs no further reads. Rebuild the range or geometry only when the view changes.
        var settledRange = this.publishedReadout?.Range is { Summary.IsFinal: true } range ? range : null;
        if (this.SettingsMatch && !this.IsEnabled &&
            (this.publishedReadout is null || (settledRange is not null && this.publishedReadout.Buckets == plotBuckets)))
        {
            this.ReadoutError = null;
            return;
        }

        var now = this.timeProvider.GetTimestamp();
        if (this.lastReadoutTick is { } lastTick && this.timeProvider.GetElapsedTime(lastTick, now) < RefreshInterval)
        {
            return;
        }

        this.lastReadoutTick = now;
        var request = new ReadoutRequest(settings);
        var reusableRange = this.SettingsMatch ? settledRange : null;
        var previousSummary = this.LatestSummary;
        this.readoutRequest = request;
        this.readoutJob = Task.Run(() =>
        {
            try
            {
                if (request.IsDiscarded)
                    return;

                var currentSummary = settledRange?.Summary ?? this.recorder.ReadSummary(previousSummary: previousSummary);
                if (request.IsDiscarded)
                    return;

                var updatedRange = reusableRange ?? (currentSummary is null
                    ? null
                    : FrameTimingRange.CreateRecent(
                        currentSummary,
                        settings.Seconds,
                        settings.TargetHz,
                        settings.IncludeUncertainStopBoundary,
                        currentSummary.IsFinal ? null : (plotBuckets.Cadence, plotBuckets.Phase, plotBuckets.Age)));
                if (request.IsDiscarded)
                    return;

                var readout = updatedRange is null ? null : new FrameTimingPlotReadout(updatedRange, plotBuckets);
                request.Publish(new(readout, null));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not read frame timing history");
                request.Publish(new(null, "Could not read frame timing history."));
            }
        });
    }

    /// <summary>Requests span details for a selection in a particular immutable view.</summary>
    /// <param name="range">The view from which the selection was made.</param>
    /// <param name="hitch">The interval or span associated with the selected plot point.</param>
    internal void RequestHitch(FrameTimingRange range, FrameTimingHitch hitch)
    {
        this.ClearHitch();
        this.requestedHitch = new(range, hitch);
        this.UpdateHitchDetail();
    }

    /// <summary>Clears hitch details and invalidates pending results for the selection.</summary>
    internal void ClearHitch()
    {
        Interlocked.Increment(ref this.hitchGeneration);
        this.requestedHitch = null;
        this.HitchDetail = null;
        this.hitchError = null;
    }

    /// <summary>Releases published views and discards pending readout results while the monitor is hidden.</summary>
    internal void StopReadout()
    {
        if (this.requestedSettings is null && !this.IsReadingHitch)
            return;

        this.publishedReadout = null;
        this.requestedSettings = null;
        this.InvalidateReadout();
        this.ClearHitch();
    }

    private void InvalidateReadout()
    {
        this.readoutRequest?.Discard();
        this.lastReadoutTick = null;
        this.publishedSettings = null;
        this.ReadoutError = null;
    }

    private void UpdateHitchDetail()
    {
        // A selection change invalidates the result while the existing job retains its view until completion.
        if (this.hitchJob is { IsCompletedSuccessfully: true } job)
        {
            this.hitchJob = null;
            if (job.Result is { } result && result.Generation == this.hitchGeneration)
            {
                this.HitchDetail = result.Detail;
                this.hitchError = result.Error;
            }
        }

        if (this.hitchJob is not null || this.requestedHitch is not { } request)
            return;

        this.requestedHitch = null;
        var generation = this.hitchGeneration;
        this.hitchJob = Task.Factory.StartNew<HitchResult?>(() =>
        {
            if (generation != Volatile.Read(ref this.hitchGeneration))
                return null;

            try
            {
                var detail = new FrameTimingHitchDetail(request.Range.Summary.SessionId, request.Hitch, request.Range.GetHitch(request.Hitch));
                return new HitchResult(generation, detail, null);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not read frame timing hitch details");
                return new HitchResult(generation, null, "Could not read the selected hitch.");
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, this.hitchScheduler);
    }

    private readonly record struct ViewSettings(double Seconds, double TargetHz, bool IncludeUncertainStopBoundary);

    private sealed record ReadoutResult(FrameTimingPlotReadout? Readout, string? Error);

    private readonly record struct HitchRequest(FrameTimingRange Range, FrameTimingHitch Hitch);

    private readonly record struct HitchResult(int Generation, FrameTimingHitchDetail? Detail, string? Error);

    private sealed class ReadoutRequest(ViewSettings settings)
    {
        // Discard replaces any completed result and prevents a late worker from publishing another.
        private static readonly ReadoutResult Discarded = new(null, null);
        private ReadoutResult? result;

        internal ViewSettings Settings { get; } = settings;

        internal bool IsDiscarded => ReferenceEquals(Volatile.Read(ref this.result), Discarded);

        internal ReadoutResult? Result => this.IsDiscarded ? null : Volatile.Read(ref this.result);

        internal void Discard() => Interlocked.Exchange(ref this.result, Discarded);

        internal void Publish(ReadoutResult value) => Interlocked.CompareExchange(ref this.result, value, null);
    }
}

/// <summary>Immutable timing records associated with the selected plot point.</summary>
internal sealed record FrameTimingHitchDetail(
    long SessionId,
    FrameTimingHitch Hitch,
    ImmutableArray<FrameTimingRecord> Records);
