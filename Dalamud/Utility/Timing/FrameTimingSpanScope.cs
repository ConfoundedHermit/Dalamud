namespace Dalamud.Utility.Timing;

/// <summary>A scope that completes a timing probe once.</summary>
/// <param name="probe">The active span, if timing is enabled.</param>
/// <param name="target">The presentation target when known.</param>
/// <param name="snapshot">The replay snapshot when applicable.</param>
internal ref struct FrameTimingSpanScope(
    FrameTimingProbe probe,
    FrameTimingTarget target = FrameTimingTarget.Unknown,
    FrameTimingSnapshot snapshot = default)
{
    private readonly FrameTimingProbe probe = probe;
    private bool ended;

    /// <summary>Completes the scope with an observed outcome.</summary>
    /// <param name="outcome">The measured operation's outcome.</param>
    /// <param name="nativeStatus">The native return status, if available.</param>
    /// <returns>The sampled end tick or zero if inactive or already ended.</returns>
    public long End(FrameTimingOutcome outcome = FrameTimingOutcome.Completed, int? nativeStatus = null)
    {
        if (this.ended)
            return 0;

        this.ended = true;
        return this.probe.End(outcome, target, nativeStatus, snapshot);
    }

    /// <summary>Records failure if the scope exits before an outcome is supplied.</summary>
    public void Dispose() => this.End(FrameTimingOutcome.Failed);
}
