using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace Dalamud.Utility.Timing;

/// <summary>Controls collection and manages a bounded pool of live-history buffers across sessions.</summary>
/// <param name="capacity">The record capacity of each buffer.</param>
/// <param name="beforeStopCommit">An optional callback between sampling the open bound and closing admission.</param>
internal sealed class FrameTimingRecorder(int capacity = 32 * 1024, Action<FrameTimingRecorder>? beforeStopCommit = null)
{
    private const int BufferCount = 2;
    private static readonly object ShutDown = new();
    private static long nextSessionId;
    private readonly Lock lifecycle = new();
    private readonly FrameTimingSession?[] sessions = new FrameTimingSession?[BufferCount];
    // Null is disabled, a session admits probes, and ShutDown is terminal.
    private object? admission;
    private FrameTimingSession? latest;

    /// <summary>Gets the recorder shared by the game's timing probes.</summary>
    internal static FrameTimingRecorder Instance { get; } = new();

    /// <summary>Starts collection when requested by the startup preference.</summary>
    /// <param name="enabled">Whether to begin collection at startup.</param>
    /// <returns><c>true</c> if collection was unrequested or could be enabled.</returns>
    internal bool ApplyStartupPreference(bool enabled) => !enabled || this.SetEnabled(true);

    /// <summary>Enables or disables collection. Call off the rendering path.</summary>
    /// <param name="enabled">Whether to collect timing records.</param>
    /// <param name="beforeEnableCommit">An optional callback before the prepared session is published.</param>
    /// <param name="afterEnableCommit">An optional callback after publication and before this call initializes the start tick.</param>
    /// <returns>Whether the request succeeded. An enable can fail if shutdown has begun or storage is unavailable.</returns>
    internal bool SetEnabled(bool enabled, Action? beforeEnableCommit = null, Action? afterEnableCommit = null)
    {
        using var guard = this.lifecycle.EnterScope();
        var admission = Volatile.Read(ref this.admission);
        if (!enabled)
        {
            if (admission is FrameTimingSession active)
            {
                var openThroughTick = Stopwatch.GetTimestamp();
                beforeStopCommit?.Invoke(this);
                if (Interlocked.CompareExchange(ref this.admission, null, active) == active)
                    active.Close(openThroughTick);
            }

            return true;
        }

        if (admission == ShutDown)
            return false;

        if (admission is FrameTimingSession)
            return true;

        for (var i = 0; i < this.sessions.Length; i++)
        {
            var previous = this.sessions[i];
            if (previous is not null && !previous.CanReuseBuffer)
                continue;

            FrameTimingSession session;
            try
            {
                var buffer = previous?.Buffer.CreateSuccessor() ?? new FrameTimingBuffer(capacity);
                session = new(Interlocked.Increment(ref nextSessionId), buffer);
            }
            catch (OutOfMemoryException)
            {
                return false;
            }

            beforeEnableCommit?.Invoke();
            if (Interlocked.CompareExchange(ref this.admission, session, null) is not null)
                return false;

            afterEnableCommit?.Invoke();
            session.EnsureStarted();
            this.sessions[i] = session;
            this.latest = session;
            return true;
        }

        return false;
    }

    /// <summary>Closes admission immediately, without waiting on readers or in-flight operations.</summary>
    internal void Shutdown()
    {
        var observedAdmission = Volatile.Read(ref this.admission);
        var openThroughTick = Stopwatch.GetTimestamp();
        beforeStopCommit?.Invoke(this);
        if (Interlocked.Exchange(ref this.admission, ShutDown) is FrameTimingSession session)
        {
            // A replacement session has no confirmed open bound from the shutdown sample.
            session.Close(observedAdmission == session ? openThroughTick : 0);
        }
    }

    /// <summary>Measures a root operation and records a cadence entry for a named callback source.</summary>
    /// <param name="source">The source of the cadence entry, or <see cref="FrameTimingSource.None"/> for elapsed timing only.</param>
    /// <param name="span">The root phase to measure.</param>
    /// <param name="root">Fresh caller-local storage shared by synchronous probes until the root ends.</param>
    /// <returns>A root probe to end on completion, or a default probe when admission is unavailable.</returns>
    internal FrameTimingProbe Begin(FrameTimingSource source, FrameTimingSpan span, [UnscopedRef] out FrameTimingProbe.Admission root)
    {
        root = default;
        if (Volatile.Read(ref this.admission) is not FrameTimingSession session || !session.TryAdmit())
            return default;

        // Sample the cadence tick before the final admission check so accepted entries precede detachment.
        var cadenceTick = source == FrameTimingSource.None ? 0 : session.Buffer.BeginCadenceWrite();
        if (Volatile.Read(ref this.admission) != session)
        {
            if (source != FrameTimingSource.None)
                session.Buffer.CancelCadenceWrite();

            session.Release();
            return default;
        }

        var thread = Environment.CurrentManagedThreadId;
        var operationId = session.NextOperationId();
        var startTick = source == FrameTimingSource.None ? Stopwatch.GetTimestamp() : cadenceTick;
        if (source != FrameTimingSource.None)
            session.Buffer.WriteCadence(new(operationId, cadenceTick, cadenceTick, thread, source, span, FrameTimingRecordKind.Cadence));

        root = new(session, operationId);
        return new(session, operationId, source, span, startTick, thread, ref root, true);
    }

    /// <summary>Measures an operation whose caller has no callback cadence source.</summary>
    /// <param name="span">The phase to measure.</param>
    /// <param name="root">Fresh caller-local storage for this operation.</param>
    /// <returns>A root probe to end on completion, or a default probe when admission is unavailable.</returns>
    internal FrameTimingProbe Begin(FrameTimingSpan span, [UnscopedRef] out FrameTimingProbe.Admission root) =>
        this.Begin(FrameTimingSource.None, span, out root);

    /// <summary>Reads retained records and coverage for a session.</summary>
    /// <remarks>Call outside rendering guards.</remarks>
    /// <param name="sessionId">The requested session ID, or <c>null</c> for the latest session.</param>
    /// <param name="beforeCutoffSampled">An optional callback after pinning the session and before sampling the read cutoff.</param>
    /// <returns>An immutable summary, or <c>null</c> if the requested session is unavailable.</returns>
    internal FrameTimingSummary? ReadSummary(long? sessionId = null, Action? beforeCutoffSampled = null)
    {
        FrameTimingSession? session;
        using (this.lifecycle.EnterScope())
        {
            session = sessionId is null ? this.latest : Array.Find(this.sessions, candidate => candidate?.Id == sessionId);
            if (session is null)
                return null;

            session.RetainReader();
        }

        try
        {
            return session.Buffer.Read(session.Id, session.StartedTick, ReadAdmission, beforeCutoffSampled: beforeCutoffSampled);
        }
        finally
        {
            session.Release();
        }

        (bool AdmitsProbes, long OpenThroughTick, long ClosedByTick) ReadAdmission()
        {
            // Detachment is permanent for this session, even while publication of its close bounds is pending.
            var admitsProbes = Volatile.Read(ref this.admission) == session;
            var (openThroughTick, closedByTick) = session.CloseBounds;
            return (admitsProbes, openThroughTick, closedByTick);
        }
    }
}
