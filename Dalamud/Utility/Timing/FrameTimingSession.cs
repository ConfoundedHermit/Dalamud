using System.Diagnostics;
using System.Threading;

namespace Dalamud.Utility.Timing;

/// <summary>Tracks buffer ownership and timing bounds for one collection session.</summary>
/// <param name="id">The recorder-assigned session ID.</param>
/// <param name="buffer">The history owned by this session.</param>
internal sealed class FrameTimingSession(long id, FrameTimingBuffer buffer)
{
    // The high bit closes probe admission. The remaining bits count admitted roots and summary readers.
    private long ownership;
    private long startedTick;
    private long openThroughTick;
    private long closedByTick;
    private long operationId;
    private long snapshotId;

    /// <summary>Gets the ID carried by summaries and snapshot metadata.</summary>
    internal long Id { get; } = id;

    /// <summary>Gets the history protected by the session's ownership count.</summary>
    internal FrameTimingBuffer Buffer { get; } = buffer;

    /// <summary>Gets the inclusive session start tick, or zero while it is being initialized.</summary>
    internal long StartedTick => Volatile.Read(ref this.startedTick);

    /// <summary>Gets the sampled bounds on session closure, with zero for unavailable bounds.</summary>
    internal (long OpenThroughTick, long ClosedByTick) CloseBounds
    {
        get
        {
            var closedByTick = Volatile.Read(ref this.closedByTick);
            return closedByTick == 0 ? default : (this.openThroughTick, closedByTick);
        }
    }

    /// <summary>Gets a value indicating whether admission is closed and all owners have released the history.</summary>
    internal bool CanReuseBuffer => Volatile.Read(ref this.ownership) == long.MinValue;

    /// <summary>Initializes the session start tick before probes sample their own timestamps.</summary>
    /// <param name="openedByTick">A known tick within the session, or zero to sample the clock.</param>
    internal void EnsureStarted(long openedByTick = 0)
    {
        if (this.StartedTick == 0)
            Interlocked.CompareExchange(ref this.startedTick, openedByTick == 0 ? Stopwatch.GetTimestamp() : openedByTick, 0);
    }

    /// <summary>Acquires ownership for a root probe while admission is open.</summary>
    /// <returns><c>true</c> if the caller owns a reference that must be released.</returns>
    internal bool TryAdmit()
    {
        if (Interlocked.Increment(ref this.ownership) >= 0)
        {
            this.EnsureStarted();
            return true;
        }

        this.Release();
        return false;
    }

    /// <summary>Pins the history for a summary read. Call under the recorder lifecycle lock.</summary>
    internal void RetainReader() => Interlocked.Increment(ref this.ownership);

    /// <summary>Releases ownership held by a root probe or summary reader.</summary>
    internal void Release() => Interlocked.Decrement(ref this.ownership);

    /// <summary>Closes probe admission and publishes the sampled closure bounds.</summary>
    /// <param name="openThroughTick">A tick when admission was known open, or zero if unconfirmed.</param>
    internal void Close(long openThroughTick)
    {
        // A session can close immediately after publication, before its start tick is initialized.
        this.EnsureStarted(openThroughTick);
        var closedByTick = Stopwatch.GetTimestamp();
        if (Interlocked.Or(ref this.ownership, long.MinValue) >= 0)
        {
            this.openThroughTick = openThroughTick;
            // Publish the closed bound last so readers can see both samples together.
            Volatile.Write(ref this.closedByTick, closedByTick);
        }
    }

    /// <summary>Allocates the next session-local operation ID.</summary>
    /// <returns>The ID shared by related records from one root operation.</returns>
    internal long NextOperationId() => Interlocked.Increment(ref this.operationId);

    /// <summary>Allocates the next session-local snapshot ID.</summary>
    /// <returns>The ID shared by publication and replay records for one snapshot.</returns>
    internal long NextSnapshotId() => Interlocked.Increment(ref this.snapshotId);
}
