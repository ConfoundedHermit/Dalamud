using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Utility;
using Dalamud.Utility.Timing;

using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;

using Xunit;

namespace Dalamud.Test.Game;

[Collection("Native framework render synchronization")]
public class FrameworkTimingTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void UpdateRecordsCadenceAndAttributesDelaysToManagedAndNativeSpans()
    {
        var recorder = FrameTimingRecorder.Instance;
        Assert.True(recorder.SetEnabled(true));
        try
        {
            long managedStart = 0;
            long managedEnd = 0;
            long nativeStart = 0;
            long nativeEnd = 0;
            var nativeCalls = 0;
            var framework = CreateFramework(() =>
            {
                nativeCalls++;
                Assert.True(ThreadSafety.NativeFrameworkRenderSyncRoot.IsHeldByCurrentThread);
                nativeStart = Stopwatch.GetTimestamp();
                Thread.Sleep(25);
                nativeEnd = Stopwatch.GetTimestamp();
                return false;
            });
            framework.BeforeUpdate += _ =>
            {
                managedStart = Stopwatch.GetTimestamp();
                Thread.Sleep(25);
                managedEnd = Stopwatch.GetTimestamp();
            };

            ExceptionDispatchInfo? failure = null;
            var worker = new Thread(() =>
            {
                try
                {
                    var tick = typeof(Framework).GetMethod("HandleFrameworkUpdate", PrivateInstance)!;
                    Assert.False(Assert.IsType<bool>(tick.Invoke(framework, [null])));
                    Assert.False(ThreadSafety.NativeFrameworkRenderSyncRoot.IsHeldByCurrentThread);
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            }) { IsBackground = true };
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)), "The framework update did not finish.");
            failure?.Throw();

            Assert.Equal(1, nativeCalls);
            var summary = recorder.ReadSummary();
            Assert.NotNull(summary);
            var entry = Assert.Single(summary.Records, record =>
                record.Source == FrameTimingSource.FrameworkUpdate && record.Kind == FrameTimingRecordKind.Cadence);

            var full = GetSpan(FrameTimingSpan.FrameworkUpdate);
            var managed = GetSpan(FrameTimingSpan.FrameworkManagedTick);
            var guard = GetSpan(FrameTimingSpan.FrameworkGuard);
            var native = GetSpan(FrameTimingSpan.FrameworkNativeUpdate);
            Assert.Equal(entry.StartTick, full.StartTick);
            Assert.Equal(0, native.NativeStatus);
            Assert.InRange(managedStart, managed.StartTick, managed.EndTick);
            Assert.InRange(managedEnd, managedStart, managed.EndTick);
            Assert.InRange(nativeStart, native.StartTick, native.EndTick);
            Assert.InRange(nativeEnd, nativeStart, native.EndTick);
            Assert.InRange(guard.StartTick, managed.EndTick, native.StartTick);
            Assert.InRange(guard.EndTick, guard.StartTick, native.StartTick);
            Assert.InRange(native.EndTick, nativeEnd, full.EndTick);

            FrameTimingRecord GetSpan(FrameTimingSpan span)
            {
                var record = Assert.Single(summary.Records, record =>
                    record.OperationId == entry.OperationId && record.Kind == FrameTimingRecordKind.Elapsed && record.Span == span);
                Assert.Equal(FrameTimingOutcome.Completed, record.Outcome);
                return record;
            }
        }
        finally
        {
            recorder.SetEnabled(false);
        }
    }

    private static Framework CreateFramework(Func<bool> continuation)
    {
        var framework = (Framework)RuntimeHelpers.GetUninitializedObject(typeof(Framework));
        SetField("configuration", new DalamudConfiguration { FrameworkUpdateHitch = double.MaxValue });
        SetField("frameworkThreadTaskScheduler", new ThreadBoundTaskScheduler());
        SetField("updateStopwatch", new Stopwatch());
        SetField("tickDelayedTaskCompletionSources", new ConcurrentDictionary<TaskCompletionSource, (ulong, CancellationToken)>());

        // Adapt the managed continuation to the native tick delegate's pointer signature.
        var argumentType = typeof(CSFramework.Delegates.Tick).GetMethod("Invoke")!.GetParameters()[0].ParameterType;
        var adapter = new DynamicMethod("FrameworkTimingContinuation", typeof(bool), [typeof(Func<bool>), argumentType]);
        var il = adapter.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, typeof(Func<bool>).GetMethod(nameof(Func<bool>.Invoke))!);
        il.Emit(OpCodes.Ret);
        SetField("updateHook", new TickHook(adapter.CreateDelegate<CSFramework.Delegates.Tick>(continuation)));
        return framework;

        void SetField(string name, object value) => typeof(Framework).GetField(name, PrivateInstance)!.SetValue(framework, value);
    }

    private sealed class TickHook(CSFramework.Delegates.Tick original) : Hook<CSFramework.Delegates.Tick>((nint)1)
    {
        public override CSFramework.Delegates.Tick Original => original;
    }
}
