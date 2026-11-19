using System;
using System.Collections.Generic;

using Dalamud.Utility.Timing;

namespace Dalamud.Interface.Internal;

/// <summary>Horizontal bucket counts for the cadence, phase, and age charts.</summary>
internal readonly record struct FrameTimingPlotBuckets(int Cadence, int Phase, int Age);

/// <summary>Plot coordinates and, for reduced series, the corresponding indices in the source series.</summary>
/// <remarks>An empty index map means each point has the same index in its source series.</remarks>
internal readonly record struct FrameTimingPlotDisplay(
    ReadOnlyMemory<double> Seconds, ReadOnlyMemory<double> Milliseconds, ReadOnlyMemory<int> SourceIndices);

/// <summary>A timing range and its chart coordinates, prepared together on a background worker.</summary>
internal sealed class FrameTimingPlotReadout
{
    // Built once during construction and shared with the draw thread.
    private readonly Dictionary<FrameTimingPlotSeries, FrameTimingPlotDisplay> reduced = new(ReferenceEqualityComparer.Instance);

    /// <summary>Initializes a new instance of the <see cref="FrameTimingPlotReadout"/> class.</summary>
    /// <param name="range">The source range for statistics and selection.</param>
    /// <param name="buckets">The requested horizontal bucket counts.</param>
    internal FrameTimingPlotReadout(FrameTimingRange range, FrameTimingPlotBuckets buckets)
    {
        this.Range = range;
        this.Buckets = buckets;

        // Give even an empty range a positive axis length and bucket scale.
        this.DurationSeconds = Math.Max(0.001, range.DurationSeconds);

        foreach (var cadence in range.Cadence)
            this.Reduce(cadence.Samples, buckets.Cadence);

        // Prepare every phase so switching phases can reuse the current readout.
        foreach (var phase in range.Phases)
            this.Reduce(phase.Samples, buckets.Phase);

        this.Reduce(range.MainUiAge, buckets.Age);
    }

    /// <summary>Gets the full timing range underlying the plotted points.</summary>
    internal FrameTimingRange Range { get; }

    /// <summary>Gets the bucket counts used to prepare the chart coordinates.</summary>
    internal FrameTimingPlotBuckets Buckets { get; }

    /// <summary>Gets the duration used for horizontal plot layout and bucketing.</summary>
    internal double DurationSeconds { get; }

    /// <summary>Gets prepared coordinates for a series in this readout.</summary>
    /// <param name="series">The source series.</param>
    /// <returns>A view of the source coordinates or a reduced set when the series exceeds the point budget.</returns>
    internal FrameTimingPlotDisplay GetDisplay(FrameTimingPlotSeries series) =>
        this.reduced.TryGetValue(series, out var display)
            ? display
            : new(series.Seconds.AsMemory(), series.Milliseconds.AsMemory(), default);

    private void Reduce(FrameTimingPlotSeries series, int bucketCount)
    {
        bucketCount = Math.Max(1, bucketCount);
        var limit = 2 * bucketCount;
        var sourceSeconds = series.Seconds;
        if (sourceSeconds.Length <= limit)
            return;

        var sourceMilliseconds = series.Milliseconds;
        var seconds = new double[limit];
        var milliseconds = new double[limit];
        var sourceIndices = new int[limit];
        var count = 0;
        var bucketsPerSecond = bucketCount / this.DurationSeconds;

        // Keep each bucket's original extrema in time order so hit testing resolves to recorded samples.
        for (var first = 0; first < sourceSeconds.Length;)
        {
            var bucket = GetBucket(sourceSeconds[first]);
            var min = first;
            var max = first;
            var next = first + 1;
            for (; next < sourceSeconds.Length && GetBucket(sourceSeconds[next]) == bucket; next++)
            {
                if (sourceMilliseconds[next] < sourceMilliseconds[min])
                    min = next;
                if (sourceMilliseconds[next] > sourceMilliseconds[max])
                    max = next;
            }

            AddPoint(Math.Min(min, max));
            if (min != max)
                AddPoint(Math.Max(min, max));
            first = next;
        }

        this.reduced.Add(series, new(seconds.AsMemory(0, count), milliseconds.AsMemory(0, count), sourceIndices.AsMemory(0, count)));

        int GetBucket(double second) => Math.Min(bucketCount - 1, (int)(second * bucketsPerSecond));

        void AddPoint(int index)
        {
            seconds[count] = sourceSeconds[index];
            milliseconds[count] = sourceMilliseconds[index];
            sourceIndices[count] = index;
            count++;
        }
    }
}
