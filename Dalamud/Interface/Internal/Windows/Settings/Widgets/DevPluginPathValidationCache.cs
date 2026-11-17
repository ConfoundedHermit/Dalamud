using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Dalamud.Interface.Internal.Windows.Settings.Widgets;

/// <summary>
/// The result of a background DLL and manifest check.
/// </summary>
[SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Internals")]
internal enum DevPluginPathStatus
{
    /// <summary>The path has no completed observation.</summary>
    Pending,

    /// <summary>The DLL is missing or unavailable.</summary>
    DllUnavailable,

    /// <summary>The existing file's path is not rooted or lacks the exact .dll extension.</summary>
    InvalidPath,

    /// <summary>The DLL's manifest is missing or unavailable.</summary>
    ManifestUnavailable,

    /// <summary>The DLL and matching .json manifest were both found.</summary>
    Valid,
}

/// <summary>
/// Caches background DLL and manifest checks without blocking the settings UI.
/// </summary>
/// <remarks>
/// Instance state belongs to the UI thread; workers return immutable observations.
/// Paths use ordinal comparison without normalization.
/// </remarks>
[SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Internals")]
internal sealed class DevPluginPathValidationCache
{
    private const int MaximumConcurrentProbes = 2;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);

    private readonly Func<string, DevPluginPathStatus> probe;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Queue<Entry> queued = new();
    private readonly List<RunningProbe> running = [];

    /// <summary>Initializes a new instance of the <see cref="DevPluginPathValidationCache"/> class.</summary>
    /// <param name="probe">The filesystem probe, or the standard DLL/manifest probe.</param>
    /// <param name="timeProvider">The monotonic clock used to age observations.</param>
    public DevPluginPathValidationCache(Func<string, DevPluginPathStatus>? probe = null, TimeProvider? timeProvider = null)
    {
        this.probe = probe ?? ProbeFiles;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Checks path syntax without accessing the filesystem.</summary>
    /// <param name="path">The candidate path.</param>
    /// <returns>Whether the path is rooted and has the exact .dll extension.</returns>
    public static bool IsValidPath(string path)
        => Path.IsPathRooted(path) && Path.GetExtension(path) == ".dll";

    /// <summary>
    /// Discards cached and queued checks. Running probes retain their concurrency slots, but their results are ignored.
    /// </summary>
    public void Reset()
    {
        this.entries.Clear();
        this.queued.Clear();
    }

    /// <summary>Invalidates a path without scheduling a check or cancelling its running probe.</summary>
    /// <param name="path">The exact path to check again.</param>
    public void Invalidate(string path)
    {
        this.entries[path] = new Entry(path);
        this.PruneQueue();
    }

    /// <summary>Retains only the current exact paths, without performing or scheduling I/O.</summary>
    /// <param name="paths">The paths currently displayed or submitted for validation.</param>
    public void SetPaths(IEnumerable<string> paths)
    {
        var currentPaths = new HashSet<string>(paths, StringComparer.Ordinal);
        foreach (var path in this.entries.Keys.Where(path => !currentPaths.Contains(path)).ToArray())
            this.entries.Remove(path);

        foreach (var path in currentPaths)
        {
            if (!this.entries.ContainsKey(path))
                this.entries.Add(path, new Entry(path));
        }

        this.PruneQueue();
    }

    /// <summary>Consumes finished work and starts queued checks for unchecked or stale paths.</summary>
    public void Refresh()
    {
        this.ConsumeCompleted();
        foreach (var entry in this.entries.Values)
        {
            if (this.IsStale(entry))
                this.Enqueue(entry);
        }

        this.StartQueued();
    }

    /// <summary>
    /// Starts checks only for the requested, unchecked paths, allowing submitted edits to finish while the widget is hidden.
    /// Completed observations are retained even when stale; unrelated queued checks remain paused.
    /// </summary>
    /// <param name="paths">The exact paths of pending submissions.</param>
    public void PumpPending(IEnumerable<string> paths)
    {
        this.ConsumeCompleted();
        var requested = new HashSet<string>(paths, StringComparer.Ordinal);
        foreach (var path in requested)
        {
            if (this.entries.TryGetValue(path, out var entry) && entry.Status == DevPluginPathStatus.Pending)
                this.Enqueue(entry);
        }

        this.StartQueued(requested);
    }

    /// <summary>Publishes finished observations without waiting or starting more work.</summary>
    public void ConsumeCompleted()
    {
        for (var i = this.running.Count - 1; i >= 0; i--)
        {
            var work = this.running[i];
            if (!work.Task.IsCompleted)
                continue;

            this.running.RemoveAt(i);
            Observation observation;
            try
            {
                // IsCompleted guarantees this reads an already finished task without waiting.
                observation = work.Task.GetAwaiter().GetResult();
            }
            catch
            {
                observation = new Observation(DevPluginPathStatus.DllUnavailable, this.timeProvider.GetTimestamp());
            }

            // Entry identity rejects late results after reset, removal, or resubmission of the same path.
            if (!this.IsCurrent(work.Entry))
                continue;

            work.Entry.Status = observation.Status;
            work.Entry.CompletedAt = observation.CompletedAt;
        }
    }

    /// <summary>Gets the previous observation during refresh, or Pending for an unchecked path.</summary>
    /// <param name="path">The exact path.</param>
    /// <returns>The cached status.</returns>
    public DevPluginPathStatus GetStatus(string path)
        => this.entries.TryGetValue(path, out var entry) ? entry.Status : DevPluginPathStatus.Pending;

    /// <summary>Gets an observation only while it is within the refresh interval.</summary>
    /// <param name="path">The exact path.</param>
    /// <returns>The fresh status, or Pending.</returns>
    public DevPluginPathStatus GetFreshStatus(string path)
        => this.entries.TryGetValue(path, out var entry) && !this.IsStale(entry) ? entry.Status : DevPluginPathStatus.Pending;

    private static DevPluginPathStatus ProbeFiles(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return DevPluginPathStatus.DllUnavailable;

        if (!IsValidPath(path))
            return DevPluginPathStatus.InvalidPath;

        return File.Exists(Path.ChangeExtension(path, ".json"))
                   ? DevPluginPathStatus.Valid
                   : DevPluginPathStatus.ManifestUnavailable;
    }

    private static Observation RunProbe(string path, Func<string, DevPluginPathStatus> probe, TimeProvider clock)
    {
        DevPluginPathStatus status;
        try
        {
            status = probe(path);
        }
        catch
        {
            status = DevPluginPathStatus.DllUnavailable;
        }

        // Age results from worker completion so delayed UI polling cannot extend their freshness.
        return new Observation(status, clock.GetTimestamp());
    }

    private bool IsCurrent(Entry entry)
        => this.entries.TryGetValue(entry.Path, out var current) && ReferenceEquals(entry, current);

    private void Enqueue(Entry entry)
    {
        if (entry.IsQueued || this.running.Any(work => work.Entry.Path == entry.Path))
            return;

        entry.IsQueued = true;
        this.queued.Enqueue(entry);
    }

    private void StartQueued(HashSet<string>? requested = null)
    {
        // Visit each queued entry once, retaining the order of all requests that remain queued.
        var queueLength = this.queued.Count;
        for (var i = 0; i < queueLength; i++)
        {
            var entry = this.queued.Dequeue();
            if (!this.IsCurrent(entry))
            {
                entry.IsQueued = false;
                continue;
            }

            if (this.running.Count >= MaximumConcurrentProbes ||
                (requested is not null && (!requested.Contains(entry.Path) || entry.Status != DevPluginPathStatus.Pending)))
            {
                this.queued.Enqueue(entry);
                continue;
            }

            entry.IsQueued = false;
            // Capture only probe inputs so a blocked worker does not retain the cache or settings.
            var path = entry.Path;
            var probe = this.probe;
            var clock = this.timeProvider;
            var task = Task.Run(() => RunProbe(path, probe, clock));
            this.running.Add(new RunningProbe(entry, task));
        }
    }

    private void PruneQueue()
    {
        // Removing obsolete entries must preserve the order of surviving requests.
        var queueLength = this.queued.Count;
        for (var i = 0; i < queueLength; i++)
        {
            var entry = this.queued.Dequeue();
            if (this.IsCurrent(entry))
                this.queued.Enqueue(entry);
        }
    }

    private bool IsStale(Entry entry)
        => entry.Status == DevPluginPathStatus.Pending ||
           this.timeProvider.GetElapsedTime(entry.CompletedAt, this.timeProvider.GetTimestamp()) >= RefreshInterval;

    private readonly record struct Observation(DevPluginPathStatus Status, long CompletedAt);

    [SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Internals")]
    private sealed class Entry(string path)
    {
        public string Path { get; } = path;

        public DevPluginPathStatus Status { get; set; } = DevPluginPathStatus.Pending;

        public long CompletedAt { get; set; }

        public bool IsQueued { get; set; }
    }

    private sealed record RunningProbe(Entry Entry, Task<Observation> Task);
}
