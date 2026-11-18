using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Utility;
using Dalamud.Utility.Timing;

using TerraFX.Interop.DirectX;

using Xunit;

namespace Dalamud.Test.Interface;

[Collection("Native framework render synchronization")]
public class BackendInitializationAdmissionTests
{
    private static readonly TimeSpan WorkerTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void OverlappingFirstPresentationsEnterOnlyOneConstructor()
    {
        using var fixture = new PresentationTestFixture();
        using var constructorEntered = new ManualResetEventSlim();
        using var releaseConstructor = new ManualResetEventSlim();
        fixture.DuringConstruction = () =>
        {
            Assert.True(ThreadSafety.NativeFrameworkRenderSyncRoot.IsHeldByCurrentThread);
            Assert.True(fixture.RenderLock.IsHeldByCurrentThread);
            constructorEntered.Set();
            Assert.True(releaseConstructor.Wait(WorkerTimeout), "The first constructor was not released.");
        };

        Exception? firstFailure = null;
        var first = new Thread(() =>
        {
            try
            {
                fixture.Present();
            }
            catch (Exception exception)
            {
                firstFailure = exception;
            }
        }) { IsBackground = true };
        first.Start();
        try
        {
            Assert.True(constructorEntered.Wait(WorkerTimeout), "The first presentation did not enter construction.");
            var descriptorCalls = fixture.DescriptorCalls;
            fixture.Present();
            Assert.Equal(descriptorCalls, fixture.DescriptorCalls);
            Assert.Equal(1, fixture.ConstructorCalls);
            Assert.Null(fixture.Manager.Backend);
            Assert.Equal(0, fixture.Manager.CumulativePresentCalls);
        }
        finally
        {
            releaseConstructor.Set();
            Assert.True(first.Join(WorkerTimeout), "The initializer did not finish.");
        }

        fixture.CallbackFailure?.Throw();
        Assert.NotNull(firstFailure);
        Assert.Equal(PresentationTestFixture.InitializationError, firstFailure.HResult);
        Assert.Equal(1, fixture.ReferenceCount);
        Assert.Null(fixture.Manager.Backend);

        // A failed constructor must release ownership and allow retry.
        fixture.DuringConstruction = null;
        AssertInitializationFails(fixture);
        Assert.Equal(2, fixture.ConstructorCalls);
        Assert.Equal(1, fixture.ReferenceCount);
    }

    [Fact]
    public void DeviceOwningCallbackSkipsInitializationWhileFrameworkAdmissionIsUnavailable()
    {
        using var fixture = new PresentationTestFixture();
        var deviceGate = new Lock();
        var queuedWorkRan = false;
        fixture.BeforeRender.Enqueue(() => queuedWorkRan = true);
        using var callbackReturned = new ManualResetEventSlim();
        Exception? workerFailure = null;
        var worker = new Thread(() =>
        {
            try
            {
                // Model an incoming middleware callback that already owns the device critical section.
                using (deviceGate.EnterScope())
                    fixture.Present();
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
            finally
            {
                callbackReturned.Set();
            }
        }) { IsBackground = true };

        try
        {
            using (ThreadSafety.NativeFrameworkRenderSyncRoot.EnterScope())
            {
                worker.Start();
                Assert.True(callbackReturned.Wait(WorkerTimeout), "Presentation waited on framework admission while owning the device gate.");
                Assert.Equal(0, fixture.DescriptorCalls);
                Assert.Equal(0, fixture.ConstructorCalls);
                Assert.Equal(0, fixture.Manager.CumulativePresentCalls);
                Assert.False(queuedWorkRan);
                Assert.Single(fixture.BeforeRender);
            }
        }
        finally
        {
            // Join after framework ownership is released, including on assertion failure.
            Assert.True(worker.Join(WorkerTimeout), "The presentation worker did not finish after framework release.");
        }

        Assert.Null(workerFailure);
        Assert.Null(fixture.Manager.Backend);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReentrantPresentationDoesNotConstructAnotherBackend(bool recording)
    {
        const int originalStatus = 17;
        using var shutdown = new InterfaceShutdownTests.ShutdownFixture();
        using var fixture = new PresentationTestFixture(manager: shutdown.Manager);
        var originalCalls = 0;
        shutdown.InstallHook(
            "dxgiSwapChainPresentHook",
            "dxgiSwapChainPresentFallback",
            () => originalCalls++,
            () => throw new InvalidOperationException("Unexpected DXGI Present fallback."),
            originalStatus);
        var present = shutdown.BindDetour<Func<nint, uint, uint, int>>("DxgiSwapChainPresentDetour");
        int Present() => present(fixture.SwapChain, 0, 0);

        // Reenter the detour before the outer call can publish a backend.
        fixture.DuringConstruction = () =>
        {
            if (fixture.ConstructorCalls == 1)
                Assert.Equal(originalStatus, Present());
            Assert.Equal(1, fixture.ConstructorCalls);
            Assert.Null(fixture.Manager.Backend);
            Assert.True(ThreadSafety.NativeFrameworkRenderSyncRoot.IsHeldByCurrentThread);
            Assert.True(fixture.RenderLock.IsHeldByCurrentThread);
        };

        var recorder = FrameTimingRecorder.Instance;
        Assert.True(recorder.SetEnabled(recording));
        try
        {
            var before = recorder.ReadSummary();
            var failure = Assert.ThrowsAny<Exception>(() => Present());
            Assert.Equal(PresentationTestFixture.InitializationError, failure.HResult);
            fixture.CallbackFailure?.Throw();
            Assert.Equal(1, fixture.ConstructorCalls);
            Assert.Equal(1, fixture.ReferenceCount);
            Assert.Equal(1, originalCalls);
            Assert.Equal(0, fixture.Manager.CumulativePresentCalls);
            Assert.Null(fixture.Manager.Backend);
            Assert.False(ThreadSafety.NativeFrameworkRenderSyncRoot.IsHeldByCurrentThread);
            Assert.False(fixture.RenderLock.IsHeldByCurrentThread);

            var after = recorder.ReadSummary();
            if (!recording)
            {
                Assert.Equal(before?.SessionId, after?.SessionId);
                if (before is not null)
                    Assert.Equal<FrameTimingRecord>(before.Records, after!.Records);
                return;
            }

            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.Equal(before.SessionId, after.SessionId);
            var entries = after.Records.Where(record => record.Kind == FrameTimingRecordKind.Cadence).ToArray();
            Assert.Equal(2, entries.Length);
            var callbacks = after.Records.Where(record => record.Kind == FrameTimingRecordKind.Elapsed &&
                record.Span == FrameTimingSpan.OverlayCallback).ToArray();
            Assert.Equal(2, callbacks.Length);
            var outer = Assert.Single(callbacks, record => record.Outcome == FrameTimingOutcome.Failed);
            var inner = Assert.Single(callbacks, record => record.Outcome == FrameTimingOutcome.BackendUnavailable);
            Assert.NotEqual(outer.OperationId, inner.OperationId);
            Assert.True(outer.StartTick <= inner.StartTick && inner.EndTick <= outer.EndTick);

            foreach (var callback in callbacks)
            {
                var entry = Assert.Single(entries, record => record.OperationId == callback.OperationId);
                Assert.Equal(FrameTimingSource.DxgiPresent, entry.Source);
                Assert.Equal(FrameTimingSource.DxgiPresent, callback.Source);
                Assert.Equal(FrameTimingSpan.OverlayCallback, entry.Span);
                Assert.Equal(entry.StartTick, callback.StartTick);
                Assert.Equal(FrameTimingTarget.Display, callback.Target);
                var admission = Assert.Single(after.Records, record => record.OperationId == callback.OperationId &&
                    record.Span == FrameTimingSpan.MainAdmission);
                Assert.Equal(callback.Outcome, admission.Outcome);
            }

            var native = Assert.Single(after.Records, record => record.Span == FrameTimingSpan.DxgiNativePresent);
            Assert.Equal(inner.OperationId, native.OperationId);
            Assert.Equal(FrameTimingSource.DxgiPresent, native.Source);
            Assert.Equal(FrameTimingRecordKind.Elapsed, native.Kind);
            Assert.Equal(FrameTimingOutcome.Completed, native.Outcome);
            Assert.Equal(originalStatus, native.NativeStatus);
            Assert.True(inner.StartTick <= native.StartTick && native.EndTick <= inner.EndTick);
        }
        finally
        {
            recorder.SetEnabled(false);
            await shutdown.ClearHooks().WaitAsync(WorkerTimeout);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void InitializationFailureRestoresCallingThreadsFrameworkRecursion(int recursionDepth)
    {
        using var fixture = new PresentationTestFixture();
        var frameworkGate = ThreadSafety.NativeFrameworkRenderSyncRoot;
        for (var i = 0; i < recursionDepth; i++)
            frameworkGate.Enter();

        try
        {
            AssertInitializationFails(fixture);
            Assert.Null(fixture.Manager.Backend);
            Assert.False(fixture.RenderLock.IsHeldByCurrentThread);
            Assert.Equal(0, fixture.Manager.CumulativePresentCalls);
            Assert.Equal(1, fixture.ReferenceCount);
            for (var i = 0; i < recursionDepth; i++)
            {
                Assert.True(frameworkGate.IsHeldByCurrentThread);
                frameworkGate.Exit();
            }

            Assert.False(frameworkGate.IsHeldByCurrentThread);
            AssertInitializationFails(fixture);
            Assert.Equal(2, fixture.ConstructorCalls);
        }
        finally
        {
            while (frameworkGate.IsHeldByCurrentThread)
                frameworkGate.Exit();
        }
    }

    [Fact]
    public void TestPresentationAndForeignTargetDoNotInitializeOrDispatchWork()
    {
        using var fixture = new PresentationTestFixture();
        fixture.BeforeRender.Enqueue(() => throw new InvalidOperationException("Ineligible presentation dispatched queued work."));

        fixture.Present(DXGI.DXGI_PRESENT_TEST);
        Assert.Equal(0, fixture.DescriptorCalls);
        fixture.PresentForeignTarget();

        Assert.Equal(0, fixture.ConstructorCalls);
        Assert.Null(fixture.Manager.Backend);
        Assert.Equal(0, fixture.Manager.CumulativePresentCalls);
        Assert.Single(fixture.BeforeRender);
        Assert.False(ThreadSafety.NativeFrameworkRenderSyncRoot.IsHeldByCurrentThread);
        Assert.False(fixture.RenderLock.IsHeldByCurrentThread);
    }

    private static void AssertInitializationFails(PresentationTestFixture fixture)
    {
        var failure = Assert.ThrowsAny<Exception>(() => fixture.Present());
        Assert.Equal(PresentationTestFixture.InitializationError, failure.HResult);
    }
}
