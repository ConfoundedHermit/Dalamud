using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Hooking.WndProcHook;
using Dalamud.Interface.ImGuiBackend;
using Dalamud.Interface.ImGuiBackend.Delegates;
using Dalamud.Interface.ImGuiBackend.InputHandler;
using Dalamud.Interface.ImGuiBackend.Renderers;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.ReShadeHandling;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Utility.Timing;

using TerraFX.Interop.DirectX;
using TerraFX.Interop.Windows;

using Xunit;

namespace Dalamud.Test.Interface;

[Collection("Native framework render synchronization")]
public class InterfaceShutdownTests
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("DxgiSwapChainPresentDetour", "dxgiSwapChainPresentHook", "dxgiSwapChainPresentFallback")]
    [InlineData("ReShadeDxgiSwapChainOnPresentDetour", "reShadeDxgiSwapChainPresentHook", "reShadeDxgiSwapChainPresentFallback")]
    [InlineData("AsHookDxgiSwapChainResizeBuffersDetour", "dxgiSwapChainResizeBuffersHook", "dxgiSwapChainResizeBuffersFallback")]
    [InlineData("AsReShadeAddonDxgiSwapChainResizeBuffersDetour", "dxgiSwapChainResizeBuffersHook", "dxgiSwapChainResizeBuffersFallback")]
    [InlineData("KernelDeviceSwapChainPresentDetour", "kernelDeviceSwapchainPresentHook", "kernelDeviceSwapchainPresentFallback")]
    [InlineData("SetCursorDetour", "setCursorHook", "setCursorFallback")]
    public void ShutdownDisablesHooksWithoutBlockingAndDrainsOriginalContinuation(
        string detourName,
        string hookField,
        string fallbackField)
    {
        using var fixture = new ShutdownFixture();
        using var originalEntered = new ManualResetEventSlim();
        using var releaseOriginal = new ManualResetEventSlim();
        var fallbackCalls = 0;
        var originalCalls = 0;
        var hook = fixture.InstallHook(hookField, fallbackField, () =>
        {
            Interlocked.Increment(ref originalCalls);
            originalEntered.Set();
            Assert.True(releaseOriginal.Wait(WorkerTimeout), "The original continuation was not released.");
        }, () => Interlocked.Increment(ref fallbackCalls));
        var invoke = fixture.CreateDetourInvoker(detourName);
        Exception? workerFailure = null;
        Exception? dispatcherFailure = null;
        Task? cleanup = null;
        var dispatcherStarted = false;
        using var dispatcherReturned = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try
            {
                invoke(false);
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
        }) { IsBackground = true };
        var dispatcher = new Thread(() =>
        {
            try
            {
                cleanup = fixture.ClearHooks();
            }
            catch (Exception exception)
            {
                dispatcherFailure = exception;
            }
            finally
            {
                dispatcherReturned.Set();
            }
        }) { IsBackground = true };

        worker.Start();
        try
        {
            Assert.True(originalEntered.Wait(WorkerTimeout), "The detour did not reach its original continuation.");
            dispatcher.Start();
            dispatcherStarted = true;
            Assert.True(dispatcherReturned.Wait(WorkerTimeout), "Hook detachment blocked the framework dispatcher.");
            Assert.Null(dispatcherFailure);
            Assert.NotNull(cleanup);
            Assert.True(hook.DisableCalls > 0);
            Assert.False(cleanup.Wait(TimeSpan.FromMilliseconds(100)), "Cleanup completed before the original continuation returned.");
            Assert.False(hook.IsDisposed);
            Assert.Same(hook, fixture.GetField(hookField));
        }
        finally
        {
            releaseOriginal.Set();
            Assert.True(worker.Join(WorkerTimeout), "The callback worker did not finish.");
            if (dispatcherStarted)
                Assert.True(dispatcher.Join(WorkerTimeout), "The cleanup dispatcher did not finish.");
        }

        Assert.Null(workerFailure);
        Assert.True(cleanup!.Wait(WorkerTimeout), "Cleanup did not finish after the callback returned.");
        Assert.True(hook.IsDisposed);
        Assert.Null(fixture.GetField(hookField));
        Assert.Equal(1, originalCalls);
        Assert.Equal(0, fallbackCalls);

        // A caller holding a stale detour must use the native fallback after hooks are released.
        invoke(true);
        Assert.Equal(1, originalCalls);
        Assert.Equal(1, fallbackCalls);
        Assert.Same(cleanup, fixture.ClearHooks());
    }

    [Theory]
    [InlineData("DxgiSwapChainPresentDetour", "dxgiSwapChainPresentHook", "dxgiSwapChainPresentFallback")]
    [InlineData("ReShadeDxgiSwapChainOnPresentDetour", "reShadeDxgiSwapChainPresentHook", "reShadeDxgiSwapChainPresentFallback")]
    [InlineData("KernelDeviceSwapChainPresentDetour", "kernelDeviceSwapchainPresentHook", "kernelDeviceSwapchainPresentFallback")]
    public void OriginalFailureReleasesCallbackSoCleanupCanFinish(
        string detourName,
        string hookField,
        string fallbackField)
    {
        using var fixture = new ShutdownFixture();
        var failure = new ContinuationException();
        var hook = fixture.InstallHook(hookField, fallbackField, () => throw failure, () => { });
        var invoke = fixture.CreateDetourInvoker(detourName);

        Assert.Same(failure, Assert.Throws<ContinuationException>(() => invoke(false)));
        Assert.True(fixture.ClearHooks().Wait(WorkerTimeout), "An exceptional callback kept cleanup blocked.");
        Assert.True(hook.IsDisposed);
    }

    [Theory]
    [InlineData(DXGI.DXGI_PRESENT_TEST, false, 7)]
    [InlineData(0u, false, DXGI.DXGI_ERROR_INVALID_CALL)]
    [InlineData(0u, true, 7)]
    public void DxgiPresentRecordsAdmissionAndPreservesOriginalResult(uint flags, bool contended, int originalStatus)
    {
        using var presentation = new PresentationTestFixture();
        using var fixture = new ShutdownFixture();
        var recorder = FrameTimingRecorder.Instance;
        FrameTimingSummary? duringOriginal = null;
        var originalCalls = 0;
        fixture.InstallHook(
            "dxgiSwapChainPresentHook",
            "dxgiSwapChainPresentFallback",
            () =>
            {
                originalCalls++;
                duringOriginal = recorder.ReadSummary();
            },
            () => throw new InvalidOperationException("Unexpected DXGI Present fallback."),
            originalStatus);
        var present = fixture.BindDetour<Func<nint, uint, uint, int>>("DxgiSwapChainPresentDetour");
        var (expectedOutcome, expectedTarget, descriptorCalls) = (contended, flags) switch
        {
            (true, _) => (FrameTimingOutcome.Contended, FrameTimingTarget.Unknown, 0),
            (false, DXGI.DXGI_PRESENT_TEST) => (FrameTimingOutcome.Test, FrameTimingTarget.Unknown, 0),
            _ => (FrameTimingOutcome.OtherTarget, FrameTimingTarget.Other, 2),
        };

        Assert.True(recorder.SetEnabled(true));
        try
        {
            int InvokePresent() => present(presentation.ForeignSwapChain, 0, flags);
            var status = contended ? PresentWithFrameworkContention(InvokePresent) : InvokePresent();

            Assert.Equal(originalStatus, status);
            Assert.Equal(1, originalCalls);
            Assert.Equal(descriptorCalls, presentation.DescriptorCalls);
            Assert.NotNull(duringOriginal);
            var entry = Assert.Single(duringOriginal.Records, record => record.Kind == FrameTimingRecordKind.Cadence);
            Assert.Equal(FrameTimingSource.DxgiPresent, entry.Source);
            Assert.DoesNotContain(duringOriginal.Records, record => record.Kind == FrameTimingRecordKind.Elapsed &&
                record.Span is FrameTimingSpan.OverlayCallback or FrameTimingSpan.DxgiNativePresent);

            var summary = recorder.ReadSummary()!;
            var callback = AssertOverlayTiming(summary, FrameTimingSource.DxgiPresent, expectedOutcome, expectedTarget);
            var native = Assert.Single(summary.Records, record => record.Span == FrameTimingSpan.DxgiNativePresent);
            Assert.Equal(entry.OperationId, callback.OperationId);
            Assert.Equal(entry.OperationId, native.OperationId);
            Assert.Equal(FrameTimingSource.DxgiPresent, native.Source);
            Assert.Equal(FrameTimingRecordKind.Elapsed, native.Kind);
            Assert.Equal(expectedTarget, native.Target);
            Assert.Equal(status, native.NativeStatus);
            Assert.Equal(status >= 0 ? FrameTimingOutcome.Completed : FrameTimingOutcome.Failed, native.Outcome);
            Assert.True(callback.StartTick <= native.StartTick && native.EndTick <= callback.EndTick);
        }
        finally
        {
            recorder.SetEnabled(false);
        }
    }

    [Fact]
    public void ReShadeCallbacksRecordDistinctCadenceSources()
    {
        using var presentation = new PresentationTestFixture();
        using var fixture = new ShutdownFixture();
        var originalCalls = 0;
        fixture.InstallHook(
            "reShadeDxgiSwapChainPresentHook",
            "reShadeDxgiSwapChainPresentFallback",
            () => originalCalls++,
            () => throw new InvalidOperationException("Unexpected legacy ReShade Present fallback."));
        var legacyPresent = fixture.BindDetour<Action<nint, uint, nint>>("ReShadeDxgiSwapChainOnPresentDetour");
        var recorder = FrameTimingRecorder.Instance;
        Assert.True(recorder.SetEnabled(true));
        try
        {
            legacyPresent(presentation.ForeignSwapChain, 0, 0);
            fixture.InvokeReShadeAddonPresentation(presentation.ForeignSwapChain);

            var summary = recorder.ReadSummary()!;
            var legacy = AssertOverlayTiming(summary, FrameTimingSource.LegacyReShadePresent, FrameTimingOutcome.OtherTarget, FrameTimingTarget.Other);
            var present = AssertOverlayTiming(summary, FrameTimingSource.ReShadePresent, FrameTimingOutcome.OtherTarget, FrameTimingTarget.Other);
            var overlay = AssertOverlayTiming(summary, FrameTimingSource.ReShadeOverlay, FrameTimingOutcome.OtherTarget, FrameTimingTarget.Other);
            Assert.NotEqual(legacy.OperationId, present.OperationId);
            Assert.NotEqual(legacy.OperationId, overlay.OperationId);
            Assert.NotEqual(present.OperationId, overlay.OperationId);

            var continuation = Assert.Single(summary.Records, record => record.Span == FrameTimingSpan.LegacyReShadeContinuation);
            Assert.Equal(legacy.OperationId, continuation.OperationId);
            Assert.Equal(FrameTimingSource.LegacyReShadePresent, continuation.Source);
            Assert.Equal(FrameTimingOutcome.Completed, continuation.Outcome);
            Assert.Equal(1, originalCalls);
            Assert.All(summary.Records, record => Assert.Null(record.NativeStatus));
        }
        finally
        {
            recorder.SetEnabled(false);
        }
    }

    [Fact]
    public void KernelPresentKeepsCadenceAndCallsOriginalOnceAfterStepFails()
    {
        using var fixture = new ShutdownFixture();
        fixture.PrepareStepBackend();
        using var originalEntered = new ManualResetEventSlim();
        using var releaseOriginal = new ManualResetEventSlim();
        var originalCalls = 0;
        var originalEntryTick = 0L;
        fixture.InstallHook("kernelDeviceSwapchainPresentHook", "kernelDeviceSwapchainPresentFallback", () =>
        {
            originalCalls++;
            originalEntryTick = Stopwatch.GetTimestamp();
            originalEntered.Set();
            Assert.True(releaseOriginal.Wait(WorkerTimeout));
        }, () => throw new InvalidOperationException("Unexpected kernel Present fallback."));
        var invoke = fixture.CreateDetourInvoker("KernelDeviceSwapChainPresentDetour");
        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                // Invoke Step off the main thread to trigger its affinity check before graphics access.
                invoke(false);
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
        }) { IsBackground = true };

        var recorder = FrameTimingRecorder.Instance;
        Assert.True(recorder.SetEnabled(true));
        try
        {
            var releaseTick = 0L;
            worker.Start();
            try
            {
                Assert.True(originalEntered.Wait(WorkerTimeout));
                var stalled = recorder.ReadSummary()!;
                Assert.Single(stalled.Records, record => record.Source == FrameTimingSource.KernelPresent && record.Kind == FrameTimingRecordKind.Cadence);
                var step = Assert.Single(stalled.Records, record => record.Span == FrameTimingSpan.Step);
                Assert.Equal(FrameTimingOutcome.Failed, step.Outcome);
                Assert.DoesNotContain(stalled.Records, record => record.Span == FrameTimingSpan.KernelNativePresent);
            }
            finally
            {
                releaseTick = Stopwatch.GetTimestamp();
                releaseOriginal.Set();
                Assert.True(worker.Join(WorkerTimeout));
            }

            Assert.Null(workerFailure);
            Assert.Equal(1, originalCalls);
            var completed = recorder.ReadSummary()!;
            var native = Assert.Single(completed.Records, record => record.Span == FrameTimingSpan.KernelNativePresent);
            var kernel = Assert.Single(completed.Records, record => record.Span == FrameTimingSpan.KernelPresent && record.Kind == FrameTimingRecordKind.Elapsed);
            Assert.Equal(FrameTimingOutcome.Completed, native.Outcome);
            Assert.Equal(FrameTimingOutcome.Failed, kernel.Outcome);
            Assert.Equal(kernel.OperationId, native.OperationId);
            Assert.True(kernel.StartTick <= native.StartTick && native.StartTick <= originalEntryTick);
            Assert.True(releaseTick <= native.EndTick && native.EndTick <= kernel.EndTick);
        }
        finally
        {
            recorder.SetEnabled(false);
            Assert.True(fixture.ClearHooks().Wait(WorkerTimeout));
        }
    }

    [Fact]
    public void KernelPresentReportsSuspendedStepAndCallsOriginalOnceDuringResize()
    {
        using var fixture = new ShutdownFixture();
        using var drawDataLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        var backend = fixture.PrepareStepBackend(drawDataLock);
        using var resize = backend.BeginResize();
        var originalCalls = 0;
        fixture.InstallHook(
            "kernelDeviceSwapchainPresentHook",
            "kernelDeviceSwapchainPresentFallback",
            () => originalCalls++,
            () => throw new InvalidOperationException("Unexpected kernel Present fallback."));
        var invoke = fixture.CreateDetourInvoker("KernelDeviceSwapChainPresentDetour");
        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                ThreadSafety.MarkMainThread();
                invoke(false);
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
        }) { IsBackground = true };

        var recorder = FrameTimingRecorder.Instance;
        Assert.True(recorder.SetEnabled(true));
        try
        {
            worker.Start();
            Assert.True(worker.Join(WorkerTimeout));
            Assert.Null(workerFailure);
            Assert.Equal(1, originalCalls);

            var records = recorder.ReadSummary()!.Records;
            var step = Assert.Single(records, record => record.Span == FrameTimingSpan.Step && record.Kind == FrameTimingRecordKind.Elapsed);
            var kernel = Assert.Single(records, record => record.Span == FrameTimingSpan.KernelPresent && record.Kind == FrameTimingRecordKind.Elapsed);
            var native = Assert.Single(records, record => record.Span == FrameTimingSpan.KernelNativePresent);
            Assert.Equal(FrameTimingOutcome.Suspended, step.Outcome);
            Assert.Equal(FrameTimingOutcome.Suspended, kernel.Outcome);
            Assert.Equal(FrameTimingOutcome.Completed, native.Outcome);
            Assert.Equal(kernel.OperationId, step.OperationId);
            Assert.Equal(kernel.OperationId, native.OperationId);
            Assert.DoesNotContain(records, record => record.Kind == FrameTimingRecordKind.Publication);
        }
        finally
        {
            recorder.SetEnabled(false);
            Assert.True(fixture.ClearHooks().Wait(WorkerTimeout));
        }
    }

    [Fact]
    public void ClearHooksBeforeHookInstallationIsSafeAndIdempotent()
    {
        using var fixture = new ShutdownFixture();
        var cleanup = fixture.ClearHooks();
        Assert.True(cleanup.Wait(WorkerTimeout));
        Assert.Same(cleanup, fixture.ClearHooks());
    }

    [Fact]
    public void StoppedEventCallbacksSkipNativeAndBackendState()
    {
        using var fixture = new ShutdownFixture();
        fixture.InvokeStoppedEventCallbacks();
        Assert.True(fixture.ClearHooks().Wait(WorkerTimeout));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ServiceUnloadYieldsFrameworkUntilOriginalContinuationAndPreservesDisposalOrder(
        bool startOnFrameworkThread, bool recordingEnabled)
    {
        using var fixture = new ShutdownFixture();
        var recorder = FrameTimingRecorder.Instance;
        var framework = (Framework)fixture.GetField("framework")!;
        using var dispatcher = new FrameworkDispatcher(framework);
        // Let the test coordinator yield its pool thread while queued unload work makes progress.
        var originalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hooksDetached = new ManualResetEventSlim();
        var frameworkWorkRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOriginal = new ManualResetEventSlim();
        var unloadReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var resourcesDisposed = new ManualResetEventSlim();
        using var followingServiceDisposed = new ManualResetEventSlim();
        var windowCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseWindowCallback = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();
        var originalCalls = 0;
        var hook = fixture.InstallHook("dxgiSwapChainPresentHook", "dxgiSwapChainPresentFallback", () =>
        {
            Interlocked.Increment(ref originalCalls);
            originalEntered.TrySetResult();
            Assert.True(hooksDetached.Wait(WorkerTimeout), "Service unload did not detach interception.");

            // Native Present may need the framework/window thread before it can return.
            var frameworkWork = framework.RunOnFrameworkThread(() =>
            {
                frameworkWorkRan.TrySetResult();
            });
            Assert.True(frameworkWork.Wait(WorkerTimeout), "The drain prevented native-dependent framework work.");
            Assert.True(releaseOriginal.Wait(WorkerTimeout), "The original continuation was not released.");
        }, () => throw new InvalidOperationException("Unexpected native fallback before drain."));
        var invoke = fixture.CreateDetourInvoker("DxgiSwapChainPresentDetour");
        fixture.PrepareServiceDisposal(() =>
        {
            Assert.True(framework.IsInFrameworkUpdateThread, "The backend was released away from the window owner thread.");
            Assert.True(hook.IsDisposed);
            order.Enqueue("resources");
            resourcesDisposed.Set();
        });
        Assert.True(recorder.SetEnabled(recordingEnabled));
        var worker = Task.Factory.StartNew(() => invoke(false), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? unload = null;
        Task? windowCallback = null;

        try
        {
            await originalEntered.Task.WaitAsync(WorkerTimeout);
            var admitted = recorder.ReadSummary();
            Assert.Equal(recordingEnabled, admitted?.Collecting ?? false);

            Task BeginUnload()
            {
                // Close admission at unload entry with a reversible disable for the shared recorder.
                recorder.SetEnabled(false);
                // Exercise service ordering with the real interface disposer and isolated neighboring services.
                var result = ServiceManager.UnloadServices(framework,
                [
                    () =>
                    {
                        Assert.True(framework.IsInFrameworkUpdateThread);
                        Assert.Equal(0, hook.DisableCalls);
                        order.Enqueue("preceding service");
                        return Task.CompletedTask;
                    },
                    () =>
                    {
                        Assert.True(framework.IsInFrameworkUpdateThread);
                        var cleanup = fixture.DisposeServiceAsync();
                        order.Enqueue("detach");
                        hooksDetached.Set();
                        return cleanup;
                    },
                    () =>
                    {
                        Assert.True(framework.IsInFrameworkUpdateThread);
                        Assert.True(resourcesDisposed.IsSet);
                        order.Enqueue("following service");
                        followingServiceDisposed.Set();
                        return Task.CompletedTask;
                    },
                ]);
                unloadReturned.TrySetResult();
                return result;
            }

            unload = startOnFrameworkThread ? framework.RunOnFrameworkThread(BeginUnload) : Task.Run(BeginUnload);
            await unloadReturned.Task.WaitAsync(WorkerTimeout);
            await frameworkWorkRan.Task.WaitAsync(WorkerTimeout);
            Assert.False(worker.IsCompleted);
            Assert.False(unload.IsCompleted);
            var hookCleanup = Assert.IsAssignableFrom<Task>(fixture.GetField("hookCleanupTask"));
            Assert.False(hookCleanup.IsCompleted);
            Assert.False(hook.IsDisposed);
            Assert.False(resourcesDisposed.IsSet);
            Assert.False(followingServiceDisposed.IsSet);

            var draining = recorder.ReadSummary(admitted?.SessionId);
            Assert.False(draining?.Collecting ?? false);
            if (recordingEnabled)
            {
                Assert.NotNull(draining);
                Assert.Single(draining.Records, record => record.Source == FrameTimingSource.DxgiPresent &&
                    record.Kind == FrameTimingRecordKind.Cadence);
                Assert.DoesNotContain(draining.Records, record => record.Kind == FrameTimingRecordKind.Elapsed &&
                    record.Span is FrameTimingSpan.OverlayCallback or FrameTimingSpan.DxgiNativePresent);
            }

            // Secondary WndProc callbacks are outside the hook drain; owner-thread dispatch must serialize teardown.
            windowCallback = framework.RunOnFrameworkThread(() =>
            {
                windowCallbackEntered.TrySetResult();
                Assert.True(releaseWindowCallback.Wait(WorkerTimeout), "The owner-thread operation was not released.");
                Assert.False(resourcesDisposed.IsSet);
            });
            await windowCallbackEntered.Task.WaitAsync(WorkerTimeout);
            var releaseTick = Stopwatch.GetTimestamp();
            releaseOriginal.Set();
            await worker.WaitAsync(WorkerTimeout);
            await hookCleanup.WaitAsync(WorkerTimeout);
            Assert.False(resourcesDisposed.IsSet);
            Assert.False(unload.IsCompleted);
            releaseWindowCallback.Set();
            await windowCallback.WaitAsync(WorkerTimeout);
            await unload.WaitAsync(WorkerTimeout);
            Assert.True(hook.IsDisposed);
            Assert.True(resourcesDisposed.IsSet);
            Assert.True(followingServiceDisposed.IsSet);
            Assert.Equal(1, originalCalls);
            Assert.Equal(new[] { "preceding service", "detach", "resources", "following service" }, order.ToArray());

            var completed = recorder.ReadSummary(admitted?.SessionId);
            if (recordingEnabled)
            {
                Assert.NotNull(completed);
                var callback = AssertOverlayTiming(completed, FrameTimingSource.DxgiPresent, FrameTimingOutcome.Test, FrameTimingTarget.Unknown);
                var native = Assert.Single(completed.Records, record => record.Span == FrameTimingSpan.DxgiNativePresent);
                Assert.Equal(callback.OperationId, native.OperationId);
                Assert.Equal(FrameTimingOutcome.Completed, native.Outcome);
                Assert.Equal(7, native.NativeStatus);
                Assert.True(callback.StartTick <= native.StartTick && releaseTick <= native.EndTick && native.EndTick <= callback.EndTick);
            }
            else
            {
                Assert.Equal(admitted?.Records.Length, completed?.Records.Length);
            }
        }
        finally
        {
            // Release blocked work before fixture disposal, including when an assertion fails.
            hooksDetached.Set();
            releaseOriginal.Set();
            releaseWindowCallback.Set();
            try
            {
                await Task.WhenAll(worker, unload ?? Task.CompletedTask, windowCallback ?? Task.CompletedTask).WaitAsync(WorkerTimeout);
            }
            finally
            {
                recorder.SetEnabled(false);
                // Unload can resume this continuation on the dispatcher; return to the pool before joining it.
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InterfaceDisposalWithoutActiveCallbacksPreservesOwnerThread(bool startOnFrameworkThread, bool unloading)
    {
        using var fixture = new ShutdownFixture();
        var framework = (Framework)fixture.GetField("framework")!;
        using var dispatcher = new FrameworkDispatcher(framework, unloading);
        var disposeCalls = 0;
        fixture.PrepareServiceDisposal(() =>
        {
            Assert.True(framework.IsInFrameworkUpdateThread);
            Interlocked.Increment(ref disposeCalls);
        });

        try
        {
            var disposal = startOnFrameworkThread
                ? framework.RunOnFrameworkThread(fixture.DisposeServiceAsync)
                : Task.Run(fixture.DisposeServiceAsync);
            await disposal.WaitAsync(WorkerTimeout);
            Assert.Equal(1, disposeCalls);
            Assert.Null(fixture.GetField("backend"));
        }
        finally
        {
            await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
    }

    [Fact]
    public async Task InterfaceDisposalAfterFrameworkDestructionDoesNotNeedAnotherTick()
    {
        using var fixture = new ShutdownFixture();
        var framework = (Framework)fixture.GetField("framework")!;
        using var dispatcher = new FrameworkDispatcher(framework, unloading: true, destroyed: true);
        var disposeCalls = 0;
        fixture.PrepareServiceDisposal(() =>
        {
            Assert.False(framework.IsInFrameworkUpdateThread);
            Interlocked.Increment(ref disposeCalls);
        });

        await fixture.DisposeServiceAsync().WaitAsync(WorkerTimeout);
        Assert.Equal(1, disposeCalls);
        Assert.Null(fixture.GetField("backend"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ServiceUnloadWithoutLiveFrameworkStillAwaitsEachService(bool unloadingFramework)
    {
        using var fixture = new ShutdownFixture();
        var framework = (Framework)fixture.GetField("framework")!;
        using var dispatcher = new FrameworkDispatcher(framework, unloadingFramework);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = false;
        var followingStarted = false;
        var callerThread = Environment.CurrentManagedThreadId;
        var unload = ServiceManager.UnloadServices(unloadingFramework ? framework : null,
        [
            async () =>
            {
                Assert.Equal(callerThread, Environment.CurrentManagedThreadId);
                firstStarted = true;
                await release.Task.ConfigureAwait(false);
            },
            () =>
            {
                Assert.False(framework.IsInFrameworkUpdateThread);
                followingStarted = true;
                return Task.CompletedTask;
            },
        ]);

        Assert.True(firstStarted);
        Assert.False(followingStarted);
        Assert.False(unload.IsCompleted);
        release.SetResult();
        await unload.WaitAsync(WorkerTimeout);
        Assert.True(followingStarted);
    }

    private static int PresentWithFrameworkContention(Func<int> present)
    {
        var status = 0;
        ExceptionDispatchInfo? workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                status = present();
            }
            catch (Exception exception)
            {
                workerFailure = ExceptionDispatchInfo.Capture(exception);
            }
        }) { IsBackground = true };

        bool returnedWhileFrameworkHeld;
        using (ThreadSafety.NativeFrameworkRenderSyncRoot.EnterScope())
        {
            worker.Start();
            returnedWhileFrameworkHeld = worker.Join(WorkerTimeout);
        }

        if (!returnedWhileFrameworkHeld)
            Assert.True(worker.Join(WorkerTimeout), "DXGI Present did not finish after framework release.");

        Assert.True(returnedWhileFrameworkHeld, "DXGI Present waited on framework admission.");
        workerFailure?.Throw();
        return status;
    }

    private static FrameTimingRecord AssertOverlayTiming(
        FrameTimingSummary summary, FrameTimingSource source, FrameTimingOutcome outcome, FrameTimingTarget target)
    {
        var entry = Assert.Single(summary.Records, record => record.Source == source && record.Kind == FrameTimingRecordKind.Cadence);
        Assert.Equal(FrameTimingSpan.OverlayCallback, entry.Span);
        var callback = Assert.Single(summary.Records, record => record.Source == source && record.Kind == FrameTimingRecordKind.Elapsed &&
            record.Span == FrameTimingSpan.OverlayCallback);
        Assert.Equal(entry.OperationId, callback.OperationId);
        Assert.Equal(entry.StartTick, callback.StartTick);
        Assert.Equal(outcome, callback.Outcome);
        Assert.Equal(target, callback.Target);
        Assert.Null(callback.NativeStatus);

        var admission = Assert.Single(summary.Records, record => record.OperationId == entry.OperationId && record.Span == FrameTimingSpan.MainAdmission);
        Assert.Equal(FrameTimingRecordKind.Elapsed, admission.Kind);
        Assert.Equal(source, admission.Source);
        Assert.Equal(outcome, admission.Outcome);
        Assert.Equal(target, admission.Target);
        return callback;
    }

    private delegate void ReShadeObjectCallback(ref ReShadeAddonInterface.ApiObject value);

    private delegate void ReShadePresentCallback(
        ref ReShadeAddonInterface.ApiObject runtime,
        ref ReShadeAddonInterface.ApiObject swapChain,
        ReadOnlySpan<RECT> sourceRect,
        ReadOnlySpan<RECT> destRect,
        ReadOnlySpan<RECT> dirtyRects);

    internal interface IFakeHook : IDalamudHook
    {
        int DisableCalls { get; }
    }

    private sealed class FakeHook<T>(T original) : Hook<T>((nint)1), IFakeHook
        where T : Delegate
    {
        private int disableCalls;

        public int DisableCalls => Volatile.Read(ref this.disableCalls);

        public override bool IsEnabled => !this.IsDisposed && this.DisableCalls == 0;

        public override string BackendName => "Shutdown test";

        public override T Original
        {
            get
            {
                this.CheckDisposed();
                return original;
            }
        }

        public override void Disable() => Interlocked.Increment(ref this.disableCalls);

        public override void Enable() => throw new NotSupportedException();
    }

    internal sealed class ShutdownFixture : IDisposable
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Lazy<nint[]> AssertionLibraries = new(() => LoadAssertionLibraries());

        private readonly InterfaceManager manager;
        private readonly Func<Task> clearHooks;
        private readonly nint swapChain;
        private readonly nint swapChainVTable;
        private readonly GetDescCallback getDesc = (_, _) => unchecked((int)0x80004005);

        public ShutdownFixture()
        {
            this.manager = Uninitialized<InterfaceManager>();
            foreach (var field in typeof(InterfaceManager).GetFields(PrivateInstance))
            {
                if (field.FieldType == typeof(Lock))
                    field.SetValue(this.manager, new Lock());
                else if (field.FieldType == typeof(object))
                    field.SetValue(this.manager, new object());
            }

            this.SetField("callbackLifetime", new InterfaceCallbackLifetime());
            this.SetField("framework", Uninitialized<Framework>());
            this.SetField("wndProcHookManager", Uninitialized<WndProcHookManager>());
            var resizeStates = typeof(InterfaceManager).GetField("reShadeResizes", PrivateInstance)!;
            resizeStates.SetValue(this.manager, Activator.CreateInstance(resizeStates.FieldType));
            this.clearHooks = typeof(InterfaceManager).GetMethod("ClearHooks", PrivateInstance)!
                .CreateDelegate<Func<Task>>(this.manager);

            // A failing GetDesc rejects the resize target before any game-service lookup.
            this.swapChainVTable = Marshal.AllocHGlobal(18 * nint.Size);
            this.swapChain = Marshal.AllocHGlobal(nint.Size);
            Marshal.WriteIntPtr(this.swapChain, this.swapChainVTable);
            Marshal.WriteIntPtr(this.swapChainVTable, 12 * nint.Size, Marshal.GetFunctionPointerForDelegate(this.getDesc));
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDescCallback(nint swapChain, nint desc);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate nint GetNativeCallback(nint apiObject);

        public InterfaceManager Manager => this.manager;

        public Task ClearHooks() => this.clearHooks();

        public Task DisposeServiceAsync() => ((IInternalAsyncDisposableService)this.manager).DisposeServiceAsync();

        public Dx11Win32Backend PrepareStepBackend(ReaderWriterLockSlim? drawDataLock = null)
        {
            this.SetField("dalamudAtlas", DispatchProxy.Create<IFontAtlas, PresentationTestFixture.ReadyAtlasProxy>());
            var backend = Uninitialized<Dx11Win32Backend>();
            if (drawDataLock is not null)
                typeof(Dx11Win32Backend).GetField("drawDataLock", PrivateInstance)!.SetValue(backend, drawDataLock);

            this.SetField("backend", backend);
            return backend;
        }

        public void PrepareServiceDisposal(Action onBackendDispose)
        {
            // Assertion-handler disposal uses these native exports; retain the modules across fixtures.
            _ = AssertionLibraries.Value;
            foreach (var name in new[] { "clientState", "assertHandler" })
            {
                var fieldType = typeof(InterfaceManager).GetField(name, PrivateInstance)!.FieldType;
                var value = RuntimeHelpers.GetUninitializedObject(fieldType);
                GC.SuppressFinalize(value);
                this.SetField(name, value);
            }

            this.SetField("frameRetireActions", new List<Action>());
            this.SetField("pendingRetireQueue", new ConcurrentQueue<Action>());
            this.SetField("backend", new RecordingBackend(onBackendDispose));
        }

        private static nint[] LoadAssertionLibraries([CallerFilePath] string sourceFile = "")
        {
            var configuration = typeof(InterfaceShutdownTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            var nativeDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "bin", configuration));
            return new[] { "cimgui.dll", "cimplot.dll", "cimguizmo.dll" }
                .Select(name => NativeLibrary.Load(Path.Combine(nativeDirectory, name)))
                .ToArray();
        }

        public object? GetField(string field) => typeof(InterfaceManager).GetField(field, PrivateInstance)!.GetValue(this.manager);

        public void InvokeStoppedEventCallbacks()
        {
            ((InterfaceCallbackLifetime)this.GetField("callbackLifetime")!).Stop();

            // Invalid arguments expose any native or WndProc access after the lifetime has stopped.
            var value = default(ReShadeAddonInterface.ApiObject);
            foreach (var method in new[] { "ReShadeAddonInterfaceOnDestroySwapChain", "ReShadeAddonInterfaceOnInitSwapChain", "ReShadeAddonInterfaceOnReShadeOverlay" })
            {
                typeof(InterfaceManager).GetMethod(method, PrivateInstance)!
                    .CreateDelegate<ReShadeObjectCallback>(this.manager)(ref value);
            }

            typeof(InterfaceManager).GetMethod("ReShadeAddonInterfaceOnPresent", PrivateInstance)!
                .CreateDelegate<ReShadePresentCallback>(this.manager)(ref value, ref value, [], [], []);
            typeof(InterfaceManager).GetMethod("WndProcHookManagerOnPreWndProc", PrivateInstance)!
                .CreateDelegate<Action<WndProcEventArgs>>(this.manager)(null!);
            typeof(InterfaceManager).GetMethod("FrameworkOnUpdate", PrivateInstance)!
                .CreateDelegate<Action<IFramework>>(this.manager)(null!);
        }

        public void InvokeReShadeAddonPresentation(nint swapChain)
        {
            // Both addon callbacks use GetNative, the first vtable slot.
            GetNativeCallback getNative = _ => swapChain;
            var vtable = Marshal.AllocHGlobal(nint.Size);
            try
            {
                Marshal.WriteIntPtr(vtable, Marshal.GetFunctionPointerForDelegate(getNative));
                var target = Unsafe.As<nint, ReShadeAddonInterface.ApiObject>(ref vtable);
                var runtime = target;
                typeof(InterfaceManager).GetMethod("ReShadeAddonInterfaceOnPresent", PrivateInstance)!
                    .CreateDelegate<ReShadePresentCallback>(this.manager)(ref runtime, ref target, [], [], []);
                typeof(InterfaceManager).GetMethod("ReShadeAddonInterfaceOnReShadeOverlay", PrivateInstance)!
                    .CreateDelegate<ReShadeObjectCallback>(this.manager)(ref runtime);
            }
            finally
            {
                Marshal.FreeHGlobal(vtable);
                GC.KeepAlive(getNative);
            }
        }

        public IFakeHook InstallHook(string hookField, string fallbackField, Action original, Action fallback, int originalStatus = 7)
        {
            var hookType = typeof(InterfaceManager).GetField(hookField, PrivateInstance)!.FieldType;
            var delegateType = hookType.GetGenericArguments()[0];
            var originalDelegate = MakeContinuation(delegateType, original, originalStatus);
            var hook = (IFakeHook)typeof(FakeHook<>).MakeGenericType(delegateType)
                .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)[0]
                .Invoke([originalDelegate]);
            this.SetField(hookField, hook);
            this.SetField(fallbackField, MakeContinuation(delegateType, fallback));
            return hook;
        }

        public T BindDetour<T>(string name) where T : Delegate
        {
            var method = typeof(InterfaceManager).GetMethod(name, PrivateInstance)!;
            var argumentTypes = method.GetParameters().Select(parameter => parameter.ParameterType.IsPointer
                ? typeof(nint)
                : parameter.ParameterType.IsEnum ? Enum.GetUnderlyingType(parameter.ParameterType) : parameter.ParameterType).ToArray();
            var adapter = new DynamicMethod(name + "TestAdapter", method.ReturnType, [typeof(InterfaceManager), .. argumentTypes], typeof(InterfaceShutdownTests), true);
            var il = adapter.GetILGenerator();
            for (var index = 0; index <= argumentTypes.Length; index++)
                il.Emit(OpCodes.Ldarg, (short)index);
            il.Emit(OpCodes.Call, method);
            il.Emit(OpCodes.Ret);
            return adapter.CreateDelegate<T>(this.manager);
        }

        public Action<bool> CreateDetourInvoker(string name)
        {
            switch (name)
            {
                case "DxgiSwapChainPresentDetour":
                    var present = this.BindDetour<Func<nint, uint, uint, int>>(name);
                    return late => Assert.Equal(7, present(0, 0, late ? 0u : 1u));
                case "ReShadeDxgiSwapChainOnPresentDetour":
                    var reShadePresent = this.BindDetour<Action<nint, uint, nint>>(name);
                    return late => reShadePresent(0, late ? 0u : 1u, 0);
                case "AsHookDxgiSwapChainResizeBuffersDetour":
                case "AsReShadeAddonDxgiSwapChainResizeBuffersDetour":
                    var resize = this.BindDetour<Func<nint, uint, uint, uint, int, uint, int>>(name);
                    return late => Assert.Equal(7, resize(late ? 0 : this.swapChain, 0, 0, 0, 0, 0));
                case "KernelDeviceSwapChainPresentDetour":
                    var kernel = this.BindDetour<Action<nint>>(name);
                    return _ => kernel(0);
                case "SetCursorDetour":
                    var cursor = this.BindDetour<Func<nint, nint>>(name);
                    return _ => Assert.Equal((nint)7, cursor(0));
                default:
                    throw new ArgumentOutOfRangeException(nameof(name));
            }
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(this.swapChain);
            Marshal.FreeHGlobal(this.swapChainVTable);
            GC.KeepAlive(this.getDesc);
        }

        private static T Uninitialized<T>() where T : class
        {
            var instance = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
            GC.SuppressFinalize(instance);
            return instance;
        }

        private static Delegate MakeContinuation(Type delegateType, Action action, int returnValue = 7)
        {
            var signature = delegateType.GetMethod("Invoke")!;
            var parameters = signature.GetParameters().Select(parameter => parameter.ParameterType).ToArray();
            var method = new DynamicMethod("ShutdownTestContinuation", signature.ReturnType, [typeof(Action), .. parameters], typeof(InterfaceShutdownTests), true);
            var il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, typeof(Action).GetMethod(nameof(Action.Invoke))!);
            if (signature.ReturnType != typeof(void))
            {
                il.Emit(OpCodes.Ldc_I4, returnValue);
                if (signature.ReturnType == typeof(nint))
                    il.Emit(OpCodes.Conv_I);
            }

            il.Emit(OpCodes.Ret);
            return method.CreateDelegate(delegateType, action);
        }

        private void SetField(string field, object value) => typeof(InterfaceManager).GetField(field, PrivateInstance)!.SetValue(this.manager, value);
    }

    private sealed class FrameworkDispatcher : IDisposable
    {
        private readonly CancellationTokenSource frameworkDestroy = new();
        private readonly CancellationTokenSource frameworkDestroyed = new();
        private readonly ManualResetEventSlim stop = new();
        private readonly Thread thread;
        private readonly bool runDispatcher;

        public FrameworkDispatcher(Framework framework, bool unloading = false, bool destroyed = false)
        {
            var scheduler = new ThreadBoundTaskScheduler();
            this.thread = new Thread(() =>
            {
                while (!this.stop.IsSet)
                {
                    scheduler.Run();
                    this.stop.Wait(TimeSpan.FromMilliseconds(10));
                }
            }) { IsBackground = true, Name = "Service unload framework regression" };
            scheduler.BoundThread = this.thread;
            if (unloading)
                this.frameworkDestroy.Cancel();
            if (destroyed)
                this.frameworkDestroyed.Cancel();

            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(Framework).GetField("frameworkDestroy", privateInstance)!.SetValue(framework, this.frameworkDestroy);
            typeof(Framework).GetField("frameworkDestroyed", privateInstance)!.SetValue(framework, this.frameworkDestroyed);
            typeof(Framework).GetField("frameworkThreadTaskScheduler", privateInstance)!.SetValue(framework, scheduler);
            typeof(Framework).GetField("<FrameworkThreadTaskFactory>k__BackingField", privateInstance)!.SetValue(framework,
                new TaskFactory(this.frameworkDestroyed.Token, TaskCreationOptions.None, TaskContinuationOptions.None, scheduler));
            this.runDispatcher = !destroyed;
            if (this.runDispatcher)
                this.thread.Start();
        }

        public void Dispose()
        {
            this.stop.Set();
            if (this.runDispatcher)
                Assert.True(this.thread.Join(WorkerTimeout), "The framework dispatcher did not finish.");
            this.stop.Dispose();
            this.frameworkDestroy.Dispose();
            this.frameworkDestroyed.Dispose();
        }
    }

    private sealed class RecordingBackend(Action onDispose) : IWin32Backend
    {
        public event ImGuiBuildUiDelegate? BuildUi { add { } remove { } }

        public event ImGuiNewInputFrameDelegate? NewInputFrame { add { } remove { } }

        public event ImGuiNewRenderFrameDelegate? NewRenderFrame { add { } remove { } }

        public event Action? PostCopy { add { } remove { } }

        public bool UpdateCursor { get; set; }

        public string? IniPath { get; set; }

        public nint DeviceHandle => throw new NotSupportedException();

        public IImGuiInputHandler InputHandler => throw new NotSupportedException();

        public IImGuiRenderer Renderer => throw new NotSupportedException();

        public bool IsResizeInProgress => throw new NotSupportedException();

        public void Dispose() => onDispose();

        public void Step() => throw new NotSupportedException();

        public FrameTimingOutcome Step(FrameTimingProbe probe) => throw new NotSupportedException();

        public void Render() => throw new NotSupportedException();

        public bool TryReadViewportBackBuffer(uint viewportId, Action<nint> reader) => throw new NotSupportedException();

        public IDisposable BeginResize() => throw new NotSupportedException();

        public void OnPreResize() => throw new NotSupportedException();

        public void OnPostResize(int newWidth, int newHeight) => throw new NotSupportedException();

        public void InvalidateFonts() => throw new NotSupportedException();

        public bool IsImGuiCursor(nint cursorHandle) => throw new NotSupportedException();

        public bool IsAttachedToPresentationTarget(nint targetHandle) => throw new NotSupportedException();

        public bool IsMainViewportFullScreen() => throw new NotSupportedException();

        public nint? ProcessWndProcW(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam) => throw new NotSupportedException();
    }

    private sealed class ContinuationException : Exception
    {
    }
}
