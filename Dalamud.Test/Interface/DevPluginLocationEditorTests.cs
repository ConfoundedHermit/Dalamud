using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration;
using Dalamud.Interface.Internal.Windows.Settings.Widgets;

using Xunit;

namespace Dalamud.Test.Interface;

public class DevPluginLocationEditorTests
{
    private const string OriginalPath = @"C:\plugins\original.dll";
    private const string CandidatePath = @"C:\plugins\candidate.dll";
    private const string OtherPath = @"C:\plugins\other.dll";
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task PendingEditAndPollingReturnWhileTheProbeIsBlocked()
    {
        using var probeEntered = new ManualResetEventSlim();
        using var releaseProbe = new ManualResetEventSlim();
        var workerThreads = new ConcurrentBag<int>();
        var uiThread = 0;
        var editor = CreateEditor(path =>
        {
            workerThreads.Add(Environment.CurrentManagedThreadId);
            if (path == CandidatePath)
            {
                probeEntered.Set();
                Assert.True(releaseProbe.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            }

            return DevPluginPathStatus.Valid;
        });

        try
        {
            var uiWork = Task.Factory.StartNew(() =>
            {
                uiThread = Environment.CurrentManagedThreadId;
                var row = editor.Locations[0];
                editor.SetInputPath(row, CandidatePath);
                editor.SubmitEdit(row);
                for (var i = 0; i < 100; i++)
                {
                    editor.Refresh();
                    Assert.True(editor.HasPendingEdits);
                    Assert.Equal(OriginalPath, row.Path);
                }
            }, TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            await uiWork.WaitAsync(WorkerTimeout, TestContext.Current.CancellationToken);
            Assert.True(probeEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            Assert.DoesNotContain(uiThread, workerThreads);
            Assert.False(editor.Changed);
            Assert.Equal(CandidatePath, editor.GetPendingPath(editor.Locations[0]));
        }
        finally
        {
            releaseProbe.Set();
        }

        PollUntil(editor, () => !editor.HasPendingEdits, refresh: false);
        Assert.Equal(CandidatePath, editor.Locations[0].Path);
    }

    [Fact]
    public void ValidityPollingCommitsSuccessfulEditsWithoutSchedulingMoreWork()
    {
        using var candidateEntered = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();
        var calls = 0;
        var editor = CreateEditor(path =>
        {
            Interlocked.Increment(ref calls);
            if (path == CandidatePath)
            {
                candidateEntered.Set();
                Assert.True(releaseCandidate.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            }

            return DevPluginPathStatus.Valid;
        });
        var row = editor.Locations[0];

        try
        {
            editor.SetInputPath(row, CandidatePath);
            editor.SubmitEdit(row);
            Assert.True(candidateEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            Assert.True(editor.HasPendingEdits);
        }
        finally
        {
            releaseCandidate.Set();
        }

        PollUntil(editor, () => !editor.HasPendingEdits, refresh: false);

        Assert.Equal(CandidatePath, row.Path);
        Assert.Equal(CandidatePath, editor.GetInputPath(row));
        Assert.Null(editor.GetPendingPath(row));
        Assert.Null(editor.GetRejectedEdit(row));
        Assert.True(editor.Changed);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2, WorkerTimeout));
        var callsAfterCommit = Volatile.Read(ref calls);
        for (var i = 0; i < 100; i++)
            Assert.False(editor.HasPendingEdits);
        Assert.Equal(callsAfterCommit, Volatile.Read(ref calls));

        editor.MarkSaved();
        Assert.False(editor.Changed);
    }

    [Fact]
    public void QueuedEditResolvesWhileHiddenWithoutStartingUnrelatedWarnings()
    {
        const string unrelatedPath = @"C:\plugins\unrelated.dll";
        using var acceptedEntered = new CountdownEvent(2);
        using var releaseAccepted = new ManualResetEventSlim();
        using var candidateEntered = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();
        using var unrelatedEntered = new ManualResetEventSlim();
        var clock = new ManualClock();
        var calls = 0;
        var cache = new DevPluginPathValidationCache(path =>
        {
            Interlocked.Increment(ref calls);
            if (path == CandidatePath)
            {
                candidateEntered.Set();
                Assert.True(releaseCandidate.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            }
            else if (path == unrelatedPath)
            {
                unrelatedEntered.Set();
            }
            else
            {
                acceptedEntered.Signal();
                Assert.True(releaseAccepted.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            }

            return DevPluginPathStatus.Valid;
        }, clock);
        var editor = new DevPluginLocationEditor(cache);
        editor.Reset([Location(OriginalPath), Location(OtherPath), Location(unrelatedPath)]);
        var row = editor.Locations[0];

        try
        {
            editor.Refresh();
            Assert.True(acceptedEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            editor.SetInputPath(row, CandidatePath);
            editor.SubmitEdit(row);
            Assert.False(candidateEntered.IsSet);
            Assert.True(editor.HasPendingEdits);

            releaseAccepted.Set();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                cache.ConsumeCompleted();
                return cache.GetStatus(OriginalPath) == DevPluginPathStatus.Valid &&
                       cache.GetStatus(OtherPath) == DevPluginPathStatus.Valid;
            }, WorkerTimeout));

            for (var i = 0; i < 100; i++)
            {
                editor.ConsumeCompleted();
                Assert.True(editor.HasPendingEdits);
            }

            Assert.False(candidateEntered.IsSet);
            Assert.False(unrelatedEntered.IsSet);
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Equal(OriginalPath, row.Path);

            // The Settings window advances already submitted edits even when this widget is hidden.
            editor.AdvancePendingEdits();
            Assert.True(candidateEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            Assert.True(editor.HasPendingEdits);
            Assert.False(unrelatedEntered.IsSet);
        }
        finally
        {
            releaseAccepted.Set();
            releaseCandidate.Set();
        }

        PollUntil(editor, () => !editor.HasPendingEdits, refresh: false);
        Assert.Equal(CandidatePath, row.Path);
        clock.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 100; i++)
        {
            editor.AdvancePendingEdits();
            Assert.False(editor.HasPendingEdits);
        }

        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.False(unrelatedEntered.IsSet);
        Assert.Equal(DevPluginLocationError.Pending, editor.GetWarning(editor.Locations[2]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedEditRestoresTheAcceptedPathAndShowsItsRejection(bool manifestUnavailable)
    {
        var status = manifestUnavailable ? DevPluginPathStatus.ManifestUnavailable : DevPluginPathStatus.DllUnavailable;
        var expectedError = manifestUnavailable ? DevPluginLocationError.ManifestUnavailable : DevPluginLocationError.DllUnavailable;
        var editor = CreateEditor(path => path == CandidatePath ? status : DevPluginPathStatus.Valid);
        var row = editor.Locations[0];

        editor.SetInputPath(row, CandidatePath);
        editor.SubmitEdit(row);
        PollUntil(editor, () => !editor.HasPendingEdits);

        Assert.Equal(OriginalPath, row.Path);
        Assert.Equal(OriginalPath, editor.GetInputPath(row));
        Assert.Equal(CandidatePath, editor.GetRejectedEdit(row)?.Path);
        Assert.Equal(expectedError, editor.GetRejectedEdit(row)?.Error);
        Assert.False(editor.Changed);

        editor.SetInputPath(row, OtherPath);
        Assert.Null(editor.GetRejectedEdit(row));
    }

    [Fact]
    public void TypingANewerDraftRevokesTheSubmittedCandidatesAuthority()
    {
        using var candidateEntered = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();
        var editor = CreateEditor(path =>
        {
            if (path == CandidatePath)
            {
                candidateEntered.Set();
                Assert.True(releaseCandidate.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            }

            return DevPluginPathStatus.Valid;
        });
        var row = editor.Locations[0];

        try
        {
            editor.SetInputPath(row, CandidatePath);
            editor.SubmitEdit(row);
            Assert.True(candidateEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            editor.SetInputPath(row, OtherPath);
            Assert.False(editor.HasPendingEdits);
            Assert.Null(editor.GetPendingPath(row));
        }
        finally
        {
            releaseCandidate.Set();
        }

        editor.ConsumeCompleted();
        Assert.Equal(OriginalPath, row.Path);
        Assert.Equal(OtherPath, editor.GetInputPath(row));
        Assert.False(editor.Changed);

        editor.SubmitEdit(row);
        PollUntil(editor, () => !editor.HasPendingEdits);
        Assert.Equal(OtherPath, row.Path);
    }

    [Fact]
    public void ResumingInputCancelsAPendingEditBeforeTheNewBufferIsSubmitted()
    {
        using var candidateEntered = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();
        var editor = CreateEditor(path =>
        {
            if (path == CandidatePath)
            {
                candidateEntered.Set();
                Assert.True(releaseCandidate.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            }

            return DevPluginPathStatus.Valid;
        });
        var row = editor.Locations[0];

        try
        {
            editor.SetInputPath(row, CandidatePath);
            editor.SubmitEdit(row);
            Assert.True(candidateEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));

            editor.CancelPendingEdit(row);
            Assert.False(editor.HasPendingEdits);
            Assert.Equal(CandidatePath, editor.GetInputPath(row));
            Assert.Equal(OriginalPath, row.Path);
        }
        finally
        {
            releaseCandidate.Set();
        }

        editor.ConsumeCompleted();
        Assert.False(editor.HasPendingEdits);
        Assert.Equal(OriginalPath, row.Path);
        Assert.False(editor.Changed);
    }

    [Fact]
    public void DiscardingAnUnsubmittedDraftRestoresTheAcceptedInputWithoutProbing()
    {
        var calls = 0;
        var editor = CreateEditor(_ =>
        {
            Interlocked.Increment(ref calls);
            return DevPluginPathStatus.Valid;
        });
        var row = editor.Locations[0];

        editor.SetInputPath(row, CandidatePath);
        Assert.Equal(CandidatePath, editor.GetInputPath(row));
        editor.DiscardEdit(row);

        Assert.Equal(OriginalPath, editor.GetInputPath(row));
        Assert.Equal(OriginalPath, row.Path);
        Assert.False(editor.HasPendingEdits);
        Assert.Null(editor.GetPendingPath(row));
        Assert.Null(editor.GetRejectedEdit(row));
        Assert.False(editor.Changed);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public void DiscardingAPendingEditRestoresTheAcceptedInputAndPreventsALateCommit()
    {
        using var candidateEntered = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();
        using var candidateReturned = new ManualResetEventSlim();
        var editor = CreateEditor(path =>
        {
            if (path == CandidatePath)
            {
                candidateEntered.Set();
                Assert.True(releaseCandidate.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
                candidateReturned.Set();
            }

            return DevPluginPathStatus.Valid;
        });
        var row = editor.Locations[0];

        try
        {
            editor.SetInputPath(row, CandidatePath);
            editor.SubmitEdit(row);
            Assert.True(candidateEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            Assert.True(editor.HasPendingEdits);

            editor.DiscardEdit(row);
            Assert.Equal(OriginalPath, editor.GetInputPath(row));
            Assert.Equal(OriginalPath, row.Path);
            Assert.False(editor.HasPendingEdits);
            Assert.Null(editor.GetPendingPath(row));
        }
        finally
        {
            releaseCandidate.Set();
        }

        Assert.True(candidateReturned.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
        for (var i = 0; i < 100; i++)
        {
            editor.AdvancePendingEdits();
            Assert.False(editor.HasPendingEdits);
        }

        Assert.Equal(OriginalPath, editor.GetInputPath(row));
        Assert.Equal(OriginalPath, row.Path);
        Assert.Null(editor.GetRejectedEdit(row));
        Assert.False(editor.Changed);
    }

    [Fact]
    public void CompletedEditsStillResolveWhenValidityIsPolledAfterTheObservationBecomesStale()
    {
        var clock = new ManualClock();
        var calls = 0;
        var cache = new DevPluginPathValidationCache(_ =>
        {
            Interlocked.Increment(ref calls);
            return DevPluginPathStatus.Valid;
        }, clock);
        var editor = new DevPluginLocationEditor(cache);
        editor.Reset([Location(OriginalPath)]);
        var row = editor.Locations[0];
        editor.SetInputPath(row, CandidatePath);
        editor.SubmitEdit(row);

        // Delay the editor's commit until the cached observation has expired.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            cache.ConsumeCompleted();
            return cache.GetStatus(CandidatePath) == DevPluginPathStatus.Valid &&
                   cache.GetStatus(OriginalPath) == DevPluginPathStatus.Valid;
        }, WorkerTimeout));
        Assert.Equal(OriginalPath, row.Path);
        clock.Advance(TimeSpan.FromSeconds(2));
        var callsBeforeValidity = Volatile.Read(ref calls);

        Assert.False(editor.HasPendingEdits);
        Assert.Equal(CandidatePath, row.Path);
        Assert.Equal(callsBeforeValidity, Volatile.Read(ref calls));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingOrResettingARowDiscardsItsPendingEdit(bool reset)
    {
        using var candidateEntered = new ManualResetEventSlim();
        using var releaseCandidate = new ManualResetEventSlim();
        var editor = CreateEditor(path =>
        {
            if (path == CandidatePath)
            {
                candidateEntered.Set();
                Assert.True(releaseCandidate.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            }

            return DevPluginPathStatus.Valid;
        });
        var oldRow = editor.Locations[0];

        try
        {
            editor.SetInputPath(oldRow, CandidatePath);
            editor.SubmitEdit(oldRow);
            Assert.True(candidateEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            if (reset)
                editor.Reset([Location(OriginalPath)]);
            else
                editor.Remove(oldRow);
            Assert.False(editor.HasPendingEdits);
        }
        finally
        {
            releaseCandidate.Set();
        }

        editor.ConsumeCompleted();
        Assert.Equal(OriginalPath, oldRow.Path);
        if (reset)
        {
            Assert.NotSame(oldRow, editor.Locations[0]);
            Assert.Equal(OriginalPath, editor.Locations[0].Path);
            Assert.False(editor.Changed);
        }
        else
        {
            Assert.Empty(editor.Locations);
            Assert.True(editor.Changed);
        }
    }

    [Fact]
    public void ResetAndResubmitOfTheSamePathRequiresANewObservation()
    {
        using var firstEntered = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var candidateCalls = 0;
        var editor = CreateEditor(path =>
        {
            if (path != CandidatePath)
                return DevPluginPathStatus.Valid;

            if (Interlocked.Increment(ref candidateCalls) == 1)
            {
                firstEntered.Set();
                Assert.True(releaseFirst.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
                return DevPluginPathStatus.Valid;
            }

            secondEntered.Set();
            Assert.True(releaseSecond.Wait(WorkerTimeout, TestContext.Current.CancellationToken));
            return DevPluginPathStatus.ManifestUnavailable;
        });

        try
        {
            var oldRow = editor.Locations[0];
            editor.SetInputPath(oldRow, CandidatePath);
            editor.SubmitEdit(oldRow);
            Assert.True(firstEntered.Wait(WorkerTimeout, TestContext.Current.CancellationToken));

            editor.Reset([Location(OriginalPath)]);
            var newRow = editor.Locations[0];
            editor.SetInputPath(newRow, CandidatePath);
            editor.SubmitEdit(newRow);
            releaseFirst.Set();
            Assert.True(SpinWait.SpinUntil(() =>
            {
                editor.Refresh();
                return secondEntered.IsSet;
            }, WorkerTimeout));
            Assert.True(editor.HasPendingEdits);
            Assert.Equal(OriginalPath, newRow.Path);
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
        }

        PollUntil(editor, () => !editor.HasPendingEdits);
        Assert.Equal(OriginalPath, editor.Locations[0].Path);
        Assert.Equal(DevPluginLocationError.ManifestUnavailable, editor.GetRejectedEdit(editor.Locations[0])?.Error);
        Assert.Equal(2, Volatile.Read(ref candidateCalls));
    }

    [Fact]
    public void EditCannotCommitAPathAlreadyUsedByAnotherRow()
    {
        var editor = CreateEditor(_ => DevPluginPathStatus.Valid, Location(OriginalPath), Location(CandidatePath));
        var row = editor.Locations[0];

        editor.SetInputPath(row, CandidatePath);
        editor.SubmitEdit(row);
        PollUntil(editor, () => !editor.HasPendingEdits);

        Assert.Equal(OriginalPath, row.Path);
        Assert.Equal(DevPluginLocationError.Duplicate, editor.GetRejectedEdit(row)?.Error);
        Assert.False(editor.Changed);
    }

    [Fact]
    public void DuplicateWarningsUseTheLiveListInsteadOfCachedErrors()
    {
        var calls = 0;
        var editor = CreateEditor(_ =>
        {
            Interlocked.Increment(ref calls);
            return DevPluginPathStatus.Valid;
        }, Location(OriginalPath), Location(OriginalPath));
        var row = editor.Locations[0];
        editor.Refresh();
        PollUntil(editor, () => editor.GetWarning(row) != DevPluginLocationError.Pending);
        Assert.Equal(DevPluginLocationError.Duplicate, editor.GetWarning(row));

        editor.Remove(editor.Locations[1]);
        Assert.Equal(DevPluginLocationError.None, editor.GetWarning(row));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void EditingPreservesOrdinalDuplicateComparison()
    {
        var editor = CreateEditor(_ => DevPluginPathStatus.Valid, Location(OriginalPath), Location(CandidatePath));
        var row = editor.Locations[0];
        const string differentlyCasedPath = @"C:\plugins\CANDIDATE.dll";

        editor.SetInputPath(row, differentlyCasedPath);
        editor.SubmitEdit(row);
        PollUntil(editor, () => !editor.HasPendingEdits);

        Assert.Equal(differentlyCasedPath, row.Path);
        Assert.Null(editor.GetRejectedEdit(row));
    }

    [Fact]
    public void AddingOnlyChecksSyntaxAndPreservesComparisonAndExtensionRules()
    {
        var calls = 0;
        var editor = CreateEditor(_ =>
        {
            Interlocked.Increment(ref calls);
            return DevPluginPathStatus.DllUnavailable;
        });

        Assert.Equal(DevPluginLocationError.None, editor.Add('"' + CandidatePath + '"'));
        Assert.Equal(DevPluginLocationError.Duplicate, editor.Add(@"c:\PLUGINS\CANDIDATE.dll"));
        Assert.Equal(DevPluginLocationError.InvalidPath, editor.Add(@"C:\plugins\uppercase.DLL"));
        Assert.Equal(DevPluginLocationError.InvalidPath, editor.Add("relative.dll"));
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal(2, editor.Locations.Count);
        Assert.Equal(CandidatePath, editor.Locations[1].Path);
        Assert.True(editor.Locations[1].IsEnabled);
        Assert.True(editor.Changed);
    }

    [Fact]
    public void ResetClonesConfigurationAndDoesNotProbeUntilRefreshEvenForDisabledLocations()
    {
        var calls = 0;
        var source = Location(OriginalPath);
        source.IsEnabled = false;
        var editor = CreateEditor(_ =>
        {
            Interlocked.Increment(ref calls);
            return DevPluginPathStatus.DllUnavailable;
        }, source);

        Assert.NotSame(source, editor.Locations[0]);
        Assert.False(editor.HasPendingEdits);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Equal(DevPluginLocationError.Pending, editor.GetWarning(editor.Locations[0]));

        editor.Refresh();
        PollUntil(editor, () => editor.GetWarning(editor.Locations[0]) != DevPluginLocationError.Pending);
        Assert.Equal(DevPluginLocationError.DllUnavailable, editor.GetWarning(editor.Locations[0]));
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    private static DevPluginLocationEditor CreateEditor(
        Func<string, DevPluginPathStatus> probe,
        params DevPluginLocationSettings[] locations)
    {
        var editor = new DevPluginLocationEditor(new DevPluginPathValidationCache(probe));
        editor.Reset(locations.Length == 0 ? [Location(OriginalPath)] : locations);
        return editor;
    }

    private static DevPluginLocationSettings Location(string path) => new() { Path = path, IsEnabled = true };

    private static void PollUntil(DevPluginLocationEditor editor, Func<bool> condition, bool refresh = true)
    {
        Assert.True(SpinWait.SpinUntil(() =>
        {
            if (refresh)
                editor.Refresh();
            else
                editor.ConsumeCompleted();
            return condition();
        }, WorkerTimeout), "The submitted validation did not finish.");
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref this.ticks);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref this.ticks, elapsed.Ticks);
    }
}
