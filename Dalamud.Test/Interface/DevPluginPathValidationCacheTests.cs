using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Interface.Internal.Windows.Settings.Widgets;

using Xunit;

namespace Dalamud.Test.Interface;

public class DevPluginPathValidationCacheTests
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public Task PollingDoesNotWaitForBlockedProbesAndDoesNotRunThemOnTheCaller() => RunOnUiThread(() =>
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var callerThread = Environment.CurrentManagedThreadId;
        var workerThread = callerThread;
        var calls = 0;
        var cache = new DevPluginPathValidationCache(_ =>
        {
            Interlocked.Increment(ref calls);
            workerThread = Environment.CurrentManagedThreadId;
            entered.Set();
            return release.Wait(WorkerTimeout, TestContext.Current.CancellationToken) ? DevPluginPathStatus.Valid : DevPluginPathStatus.DllUnavailable;
        });
        cache.SetPaths(["plugin"]);
        try
        {
            cache.Refresh();
            Assert.True(entered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            for (var i = 0; i < 100; i++)
            {
                cache.SetPaths(["plugin", "plugin"]);
                cache.Refresh();
                cache.ConsumeCompleted();
                Assert.Equal(DevPluginPathStatus.Pending, cache.GetStatus("plugin"));
                Assert.Equal(DevPluginPathStatus.Pending, cache.GetFreshStatus("plugin"));
            }

            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.NotEqual(callerThread, workerThread);
        }
        finally
        {
            release.Set();
        }

        AwaitStatus(cache, "plugin", DevPluginPathStatus.Valid);
    });

    [Fact]
    public Task ResetRetainsPhysicalSlotsAndRejectsResultsForRecreatedEntries() => RunOnUiThread(() =>
    {
        using var bothEntered = new CountdownEvent(2);
        using var releaseOriginal = new ManualResetEventSlim();
        using var replacementEntered = new ManualResetEventSlim();
        using var releaseReplacement = new ManualResetEventSlim();
        var counts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var activeByPath = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var maximumActive = 0;
        var maximumActiveForPath = 0;
        var active = 0;
        var cache = new DevPluginPathValidationCache(path =>
        {
            var invocation = counts.AddOrUpdate(path, 1, (_, count) => count + 1);
            var activeForPath = activeByPath.AddOrUpdate(path, 1, (_, count) => count + 1);
            InterlockedMax(ref maximumActiveForPath, activeForPath);
            InterlockedMax(ref maximumActive, Interlocked.Increment(ref active));
            try
            {
                if (invocation == 1 && path is "a" or "b")
                {
                    bothEntered.Signal();
                    releaseOriginal.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
                    return DevPluginPathStatus.DllUnavailable;
                }

                replacementEntered.Set();
                releaseReplacement.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
                return DevPluginPathStatus.Valid;
            }
            finally
            {
                activeByPath.AddOrUpdate(path, 0, (_, count) => count - 1);
                Interlocked.Decrement(ref active);
            }
        });
        cache.SetPaths(["a", "b", "obsolete"]);
        try
        {
            cache.Refresh();
            Assert.True(bothEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            cache.Reset();
            cache.SetPaths(["a", "b", "replacement"]);
            for (var i = 0; i < 100; i++)
                cache.Refresh();

            Assert.Equal(2, counts.Values.Sum());
            releaseOriginal.Set();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                cache.Refresh();
                return replacementEntered.IsSet;
            }, WorkerTimeout));
            Assert.Equal(DevPluginPathStatus.Pending, cache.GetStatus("a"));
            Assert.Equal(DevPluginPathStatus.Pending, cache.GetStatus("b"));
            Assert.False(counts.ContainsKey("obsolete"));
        }
        finally
        {
            releaseOriginal.Set();
            releaseReplacement.Set();
        }

        AwaitStatusWithRefresh(cache, "a", DevPluginPathStatus.Valid);
        AwaitStatusWithRefresh(cache, "b", DevPluginPathStatus.Valid);
        AwaitStatusWithRefresh(cache, "replacement", DevPluginPathStatus.Valid);
        Assert.InRange(maximumActive, 1, 2);
        Assert.Equal(1, maximumActiveForPath);
        Assert.False(counts.ContainsKey("obsolete"));
    });

    [Fact]
    public Task RemovingAndReaddingAPathDoesNotPublishItsOldObservation() => RunOnUiThread(() =>
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var calls = 0;
        var cache = new DevPluginPathValidationCache(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
                return DevPluginPathStatus.ManifestUnavailable;
            }

            secondEntered.Set();
            releaseSecond.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
            return DevPluginPathStatus.Valid;
        });
        cache.SetPaths(["a"]);
        try
        {
            cache.Refresh();
            Assert.True(firstEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            cache.SetPaths([]);
            cache.SetPaths(["a"]);
            cache.Refresh();
            Assert.Equal(1, Volatile.Read(ref calls));
            releaseFirst.Set();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                cache.Refresh();
                return secondEntered.IsSet;
            }, WorkerTimeout));
            Assert.Equal(DevPluginPathStatus.Pending, cache.GetStatus("a"));
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
        }

        AwaitStatus(cache, "a", DevPluginPathStatus.Valid);
        Assert.Equal(2, calls);
    });

    [Fact]
    public Task InvalidationRequiresANewProbeAndDiscardsTheOldInFlightResult() => RunOnUiThread(() =>
    {
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var calls = 0;
        var cache = new DevPluginPathValidationCache(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstEntered.Set();
                releaseFirst.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
                return DevPluginPathStatus.Valid;
            }

            secondEntered.Set();
            releaseSecond.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
            return DevPluginPathStatus.ManifestUnavailable;
        });
        cache.SetPaths(["a"]);
        try
        {
            cache.Refresh();
            Assert.True(firstEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            cache.Invalidate("a");
            cache.Refresh();
            Assert.Equal(1, Volatile.Read(ref calls));
            releaseFirst.Set();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                cache.Refresh();
                return secondEntered.IsSet;
            }, WorkerTimeout));
            Assert.Equal(DevPluginPathStatus.Pending, cache.GetStatus("a"));
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
        }

        AwaitStatus(cache, "a", DevPluginPathStatus.ManifestUnavailable);
        cache.Invalidate("a");
        Assert.Equal(DevPluginPathStatus.Pending, cache.GetStatus("a"));
        Assert.Equal(2, calls);
    });

    [Fact]
    public Task QueuedPathsKeepTheirTurnAheadOfRefreshingAnEarlierPath() => RunOnUiThread(() =>
    {
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var thirdEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseOther = new ManualResetEventSlim();
        var clock = new ManualClock();
        var calls = new ConcurrentQueue<string>();
        var cache = new DevPluginPathValidationCache(path =>
        {
            calls.Enqueue(path);
            if (path == "a")
            {
                firstEntered.Set();
                releaseFirst.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
            }
            else
            {
                (path == "b" ? secondEntered : thirdEntered).Set();
                releaseOther.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
            }

            return DevPluginPathStatus.Valid;
        }, clock);
        cache.SetPaths(["a", "b", "c", "removed"]);
        try
        {
            cache.Refresh();
            Assert.True(firstEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            Assert.True(secondEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            cache.SetPaths(["a", "b", "c"]);
            releaseFirst.Set();
            AwaitStatus(cache, "a", DevPluginPathStatus.Valid);
            clock.Advance(TimeSpan.FromSeconds(1));
            cache.Refresh();
            Assert.True(thirdEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            Assert.Equal(new[] { "a", "b", "c" }, calls.ToArray().OrderBy(path => path));
        }
        finally
        {
            releaseFirst.Set();
            releaseOther.Set();
        }

        AwaitStatus(cache, "b", DevPluginPathStatus.Valid);
        AwaitStatus(cache, "c", DevPluginPathStatus.Valid);
    });

    [Fact]
    public Task NegativeResultsRefreshAfterOneSecondAndKeepTheirPreviousStatus() => RunOnUiThread(() =>
    {
        using var secondEntered = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var clock = new ManualClock();
        var calls = 0;
        var cache = new DevPluginPathValidationCache(_ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
                return DevPluginPathStatus.DllUnavailable;

            secondEntered.Set();
            releaseSecond.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
            return DevPluginPathStatus.Valid;
        }, clock);
        cache.SetPaths(["a"]);
        cache.Refresh();
        AwaitStatus(cache, "a", DevPluginPathStatus.DllUnavailable);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        for (var i = 0; i < 100; i++)
            cache.Refresh();

        Assert.Equal(1, calls);
        Assert.Equal(DevPluginPathStatus.DllUnavailable, cache.GetFreshStatus("a"));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(DevPluginPathStatus.Pending, cache.GetFreshStatus("a"));
        try
        {
            cache.Refresh();
            Assert.True(secondEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            Assert.Equal(DevPluginPathStatus.DllUnavailable, cache.GetStatus("a"));
        }
        finally
        {
            releaseSecond.Set();
        }

        AwaitStatus(cache, "a", DevPluginPathStatus.Valid);
    });

    [Fact]
    public Task RefreshAgeStartsAtWorkerCompletionInsteadOfResultConsumption() => RunOnUiThread(() =>
    {
        using var timestampRead = new ManualResetEventSlim();
        var clock = new ManualClock();
        var callerThread = Environment.CurrentManagedThreadId;
        // The worker signals after sampling its timestamp so the result can age before consumption.
        clock.TimestampRead = () =>
        {
            if (Environment.CurrentManagedThreadId != callerThread)
                timestampRead.Set();
        };
        var cache = new DevPluginPathValidationCache(_ => DevPluginPathStatus.Valid, clock);
        cache.SetPaths(["a"]);
        cache.Refresh();
        Assert.True(timestampRead.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
        clock.Advance(TimeSpan.FromSeconds(1));
        AwaitStatus(cache, "a", DevPluginPathStatus.Valid);
        Assert.Equal(DevPluginPathStatus.Pending, cache.GetFreshStatus("a"));
    });

    [Fact]
    public Task LoadingAndConsumingDoNotStartQueuedWork() => RunOnUiThread(() =>
    {
        var calls = 0;
        var cache = new DevPluginPathValidationCache(_ =>
        {
            Interlocked.Increment(ref calls);
            return DevPluginPathStatus.Valid;
        });
        cache.SetPaths(["a", "b", "c"]);
        cache.ConsumeCompleted();
        Assert.Equal(0, calls);
        cache.Refresh();
        AwaitStatus(cache, "a", DevPluginPathStatus.Valid);
        AwaitStatus(cache, "b", DevPluginPathStatus.Valid);
        for (var i = 0; i < 100; i++)
            cache.ConsumeCompleted();

        Assert.Equal(2, calls);
        Assert.Equal(DevPluginPathStatus.Pending, cache.GetStatus("c"));
        cache.Refresh();
        AwaitStatus(cache, "c", DevPluginPathStatus.Valid);
    });

    [Fact]
    public Task HiddenPendingPumpStartsOnlySubmittedCandidatesAndNeverRefreshesFinishedResults() => RunOnUiThread(() =>
    {
        using var warningsEntered = new CountdownEvent(2);
        using var releaseWarnings = new ManualResetEventSlim();
        using var candidateEntered = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();
        var calls = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var clock = new ManualClock();
        var cache = new DevPluginPathValidationCache(path =>
        {
            calls.AddOrUpdate(path, 1, (_, count) => count + 1);
            if (path is "warning-a" or "warning-b")
            {
                warningsEntered.Signal();
                releaseWarnings.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
            }
            else if (path == "candidate")
            {
                candidateEntered.Set();
                releaseCandidate.Wait(WorkerTimeout, TestContext.Current.CancellationToken);
            }

            return DevPluginPathStatus.Valid;
        }, clock);
        cache.SetPaths(["finished"]);
        cache.PumpPending(["finished"]);
        AwaitStatus(cache, "finished", DevPluginPathStatus.Valid);
        cache.SetPaths(["finished", "warning-a", "warning-b", "unrelated", "candidate"]);
        try
        {
            cache.Refresh();
            Assert.True(warningsEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            clock.Advance(TimeSpan.FromSeconds(2));
            releaseWarnings.Set();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                cache.PumpPending(["candidate", "finished"]);
                return candidateEntered.IsSet;
            }, WorkerTimeout));
            Assert.Equal(DevPluginPathStatus.Pending, cache.GetStatus("candidate"));
            Assert.False(calls.ContainsKey("unrelated"));
            Assert.Equal(1, calls["finished"]);
        }
        finally
        {
            releaseWarnings.Set();
            releaseCandidate.Set();
        }

        AwaitStatus(cache, "candidate", DevPluginPathStatus.Valid);
        clock.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 100; i++)
            cache.PumpPending(["candidate", "finished"]);

        Assert.Equal(1, calls["candidate"]);
        Assert.Equal(1, calls["finished"]);
        Assert.False(calls.ContainsKey("unrelated"));
        cache.Refresh();
        Assert.True(SpinWait.SpinUntil(() =>
        {
            cache.ConsumeCompleted();
            return cache.GetStatus("unrelated") == DevPluginPathStatus.Valid;
        }, WorkerTimeout));
    });

    [Fact]
    public Task ProbeFailuresBecomeUnavailableObservationsAndReleaseTheirSlots() => RunOnUiThread(() =>
    {
        var cache = new DevPluginPathValidationCache(path => path == "bad" ? throw new IOException("Unavailable") : DevPluginPathStatus.Valid);
        cache.SetPaths(["bad", "good", "next"]);
        cache.Refresh();
        AwaitStatus(cache, "bad", DevPluginPathStatus.DllUnavailable);
        AwaitStatus(cache, "good", DevPluginPathStatus.Valid);
        cache.Refresh();
        AwaitStatus(cache, "next", DevPluginPathStatus.Valid);
    });

    [Fact]
    public Task ExactPathsStayDistinctWithoutNormalization() => RunOnUiThread(() =>
    {
        var paths = new ConcurrentBag<string>();
        var cache = new DevPluginPathValidationCache(path =>
        {
            paths.Add(path);
            return DevPluginPathStatus.Valid;
        });
        cache.SetPaths(["a", "A", "a"]);
        cache.Refresh();
        AwaitStatus(cache, "a", DevPluginPathStatus.Valid);
        AwaitStatus(cache, "A", DevPluginPathStatus.Valid);
        Assert.Equal(new[] { "A", "a" }, paths.OrderBy(path => path, StringComparer.Ordinal));
    });

    [Theory]
    [InlineData("", false)]
    [InlineData("relative.dll", false)]
    [InlineData(@"C:\plugin.dll", true)]
    [InlineData(@"C:\plugin.DLL", false)]
    [InlineData(@"\\server\share\plugin.dll", true)]
    public void SyntaxChecksPreserveRootAndExtensionPolicy(string path, bool expected)
        => Assert.Equal(expected, DevPluginPathValidationCache.IsValidPath(path));

    [Fact]
    public Task DefaultProbeTracksDllAndManifestChangesAndPreservesErrorPrecedence() => RunOnUiThread(() =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dalamud-dev-path-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var dll = Path.Combine(directory, "plugin.dll");
            var manifest = Path.ChangeExtension(dll, ".json");
            var wrongExtension = Path.Combine(directory, "plugin.txt");
            var clock = new ManualClock();
            var cache = new DevPluginPathValidationCache(timeProvider: clock);
            cache.SetPaths([dll, wrongExtension]);
            cache.Refresh();
            AwaitStatus(cache, dll, DevPluginPathStatus.DllUnavailable);
            AwaitStatus(cache, wrongExtension, DevPluginPathStatus.DllUnavailable);
            File.WriteAllText(dll, string.Empty);
            File.WriteAllText(wrongExtension, string.Empty);
            clock.Advance(TimeSpan.FromSeconds(1));
            cache.Refresh();
            AwaitStatus(cache, dll, DevPluginPathStatus.ManifestUnavailable);
            AwaitStatus(cache, wrongExtension, DevPluginPathStatus.InvalidPath);
            File.WriteAllText(manifest, string.Empty);
            clock.Advance(TimeSpan.FromSeconds(1));
            cache.Refresh();
            AwaitStatus(cache, dll, DevPluginPathStatus.Valid);
            AwaitStatus(cache, wrongExtension, DevPluginPathStatus.InvalidPath);
            File.Delete(manifest);
            clock.Advance(TimeSpan.FromSeconds(1));
            cache.Refresh();
            AwaitStatus(cache, dll, DevPluginPathStatus.ManifestUnavailable);
            File.Delete(dll);
            clock.Advance(TimeSpan.FromSeconds(1));
            cache.Refresh();
            AwaitStatus(cache, dll, DevPluginPathStatus.DllUnavailable);
        }
        finally
        {
            File.Delete(Path.Combine(directory, "plugin.dll"));
            File.Delete(Path.Combine(directory, "plugin.json"));
            File.Delete(Path.Combine(directory, "plugin.txt"));
            Directory.Delete(directory);
        }
    });

    // Keep synchronous UI polling and worker gates off the shared pool that runs the probes.
    // One dedicated driver thread also preserves the cache's UI-thread contract and thread-ID checks.
    private static Task RunOnUiThread(Action action)
        => Task.Factory.StartNew(action, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void AwaitStatus(DevPluginPathValidationCache cache, string path, DevPluginPathStatus expected)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            cache.ConsumeCompleted();
            return cache.GetStatus(path) == expected;
        }, WorkerTimeout), $"{path} did not reach {expected}.");

    private static void AwaitStatusWithRefresh(DevPluginPathValidationCache cache, string path, DevPluginPathStatus expected)
        => Assert.True(SpinWait.SpinUntil(() =>
        {
            cache.Refresh();
            return cache.GetStatus(path) == expected;
        }, WorkerTimeout), $"{path} did not reach {expected}.");

    private static void InterlockedMax(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
                return;

            current = previous;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;

        public Action? TimestampRead { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            var value = Volatile.Read(ref this.timestamp);
            this.TimestampRead?.Invoke();
            return value;
        }

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref this.timestamp, elapsed.Ticks);
    }
}
