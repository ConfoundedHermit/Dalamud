using System.Diagnostics;

namespace Dalamud.Utility.Timing;

/// <summary>A synchronous measurement tied to the session that admitted the root operation.</summary>
internal readonly ref struct FrameTimingProbe
{
    private readonly FrameTimingSession session;
    private readonly long operationId;
    private readonly FrameTimingSource source;
    private readonly FrameTimingSpan span;
    private readonly long startTick;
    private readonly int threadId;
    private readonly ref Admission admission;
    private readonly bool isRoot;

    /// <summary>Initializes a new instance of the <see cref="FrameTimingProbe"/> struct.</summary>
    /// <param name="session">The session that owns the measurement.</param>
    /// <param name="operationId">The ID shared by the root and its children.</param>
    /// <param name="source">The callback source, if any.</param>
    /// <param name="span">The phase being measured.</param>
    /// <param name="startTick">The sampled entry tick for this phase.</param>
    /// <param name="threadId">The managed thread that entered the root operation.</param>
    /// <param name="admission">The caller-local admission shared with this operation.</param>
    /// <param name="isRoot">Whether this probe releases session ownership when ended.</param>
    internal FrameTimingProbe(
        FrameTimingSession session,
        long operationId,
        FrameTimingSource source,
        FrameTimingSpan span,
        long startTick,
        int threadId,
        ref Admission admission,
        bool isRoot)
    {
        this.session = session;
        this.operationId = operationId;
        this.source = source;
        this.span = span;
        this.startTick = startTick;
        this.threadId = threadId;
        this.admission = ref admission;
        this.isRoot = isRoot;
    }

    private bool IsActive => this.session is not null &&
                             this.admission.Session == this.session && this.admission.OperationId == this.operationId;

    /// <summary>Starts a child measurement that must end before the root releases admission.</summary>
    /// <param name="child">The child phase to measure.</param>
    /// <returns>An active child probe, or a default probe when the parent is inactive.</returns>
    internal FrameTimingProbe Begin(FrameTimingSpan child) =>
        this.IsActive
            ? new(this.session, this.operationId, this.source, child, Stopwatch.GetTimestamp(), this.threadId, ref this.admission, false)
            : default;

    /// <summary>Completes the measurement and releases admission for a root probe.</summary>
    /// <remarks>Completing any copy of the root makes its copies and children inactive.</remarks>
    /// <param name="outcome">The result of the measured operation.</param>
    /// <param name="target">The presentation target classification, when available.</param>
    /// <param name="nativeStatus">The native return status, when available.</param>
    /// <param name="snapshot">The replayed snapshot. Replay metadata is recorded only for a snapshot from this session.</param>
    /// <returns>The sampled end tick, or zero when the probe is inactive.</returns>
    internal long End(
        FrameTimingOutcome outcome = FrameTimingOutcome.Completed,
        FrameTimingTarget target = FrameTimingTarget.Unknown,
        int? nativeStatus = null,
        FrameTimingSnapshot snapshot = default)
    {
        if (!this.IsActive)
            return 0;

        if (this.isRoot)
            this.admission = default;

        try
        {
            var stop = this.session.Buffer.BeginWrite(out var sequence);
            var knownSnapshot = snapshot.Id > 0 && snapshot.SessionId == this.session.Id && snapshot.RenderedTick <= this.startTick;
            this.session.Buffer.Write(sequence, new(
                this.operationId,
                this.startTick,
                stop,
                this.threadId,
                this.source,
                this.span,
                FrameTimingRecordKind.Elapsed,
                outcome,
                target,
                nativeStatus,
                knownSnapshot ? snapshot.Id : null,
                knownSnapshot ? this.startTick - snapshot.RenderedTick : null));
            return stop;
        }
        finally
        {
            if (this.isRoot)
                this.session.Release();
        }
    }

    /// <summary>Associates a finalized UI frame with a session-local snapshot ID.</summary>
    /// <param name="renderedTick">The UI finalization tick, or zero when unavailable.</param>
    /// <returns>The snapshot metadata, or a default value when the probe or finalization tick is unavailable.</returns>
    internal FrameTimingSnapshot CreateSnapshot(long renderedTick) =>
        this.IsActive && renderedTick > 0 ? new(this.session.Id, this.session.NextSnapshotId(), renderedTick) : default;

    /// <summary>Records snapshot publication under the caller's snapshot write lock.</summary>
    /// <param name="snapshot">The metadata stored with the published draw data.</param>
    internal void RecordPublication(FrameTimingSnapshot snapshot)
    {
        if (!this.IsActive || snapshot.Id == 0)
            return;

        var tick = this.session.Buffer.BeginWrite(out var sequence);
        this.session.Buffer.Write(sequence, new(
            this.operationId, tick, tick, this.threadId, this.source, this.span, FrameTimingRecordKind.Publication,
            FrameTimingOutcome.Completed, SnapshotId: snapshot.Id));
    }

    /// <summary>Records a point event within the measured operation.</summary>
    /// <param name="point">The event being recorded.</param>
    /// <param name="target">The presentation target classification, when available.</param>
    internal void RecordPoint(FrameTimingSpan point, FrameTimingTarget target = FrameTimingTarget.Unknown)
    {
        if (!this.IsActive)
            return;

        var tick = this.session.Buffer.BeginWrite(out var sequence);
        this.session.Buffer.Write(sequence, new(
            this.operationId, tick, tick, this.threadId, this.source, point, FrameTimingRecordKind.Point, Target: target));
    }

    /// <summary>Caller-local admission shared by the root and its child measurements.</summary>
    internal readonly record struct Admission(FrameTimingSession Session, long OperationId);
}
