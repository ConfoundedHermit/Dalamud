namespace Dalamud.Utility.Timing;

/// <summary>The source of a callback timing measurement.</summary>
internal enum FrameTimingSource : byte
{
    /// <summary>A standalone operation without a callback cadence source.</summary>
    None,

    /// <summary>Entry to the game framework update detour.</summary>
    FrameworkUpdate,

    /// <summary>Entry to the game kernel swap-chain Present detour.</summary>
    KernelPresent,

    /// <summary>Entry to the DXGI swap-chain Present detour.</summary>
    DxgiPresent,

    /// <summary>Entry to the detour on the legacy ReShade swap-chain Present handler.</summary>
    LegacyReShadePresent,

    /// <summary>Delivery of a ReShade addon Present event.</summary>
    ReShadePresent,

    /// <summary>Delivery of a ReShade addon overlay event.</summary>
    ReShadeOverlay,
}

/// <summary>A measured phase or event within an operation.</summary>
internal enum FrameTimingSpan : byte
{
    /// <summary>An unspecified measurement boundary.</summary>
    None,

    /// <summary>The full framework update detour, including managed work and native update.</summary>
    FrameworkUpdate,

    /// <summary>The managed framework tick and its callbacks.</summary>
    FrameworkManagedTick,

    /// <summary>Acquisition of the framework guard before native update.</summary>
    FrameworkGuard,

    /// <summary>The call to the original game framework update.</summary>
    FrameworkNativeUpdate,

    /// <summary>The full kernel Present detour, including Step and framework guard restoration.</summary>
    KernelPresent,

    /// <summary>The callback and backend readiness checks that precede Step.</summary>
    KernelStepAdmission,

    /// <summary>The call to the original game kernel Present.</summary>
    KernelNativePresent,

    /// <summary>Restoration of the framework guard's recursion depth after native Present.</summary>
    FrameworkReacquisition,

    /// <summary>The full presentation detour or addon callback, including its continuation when available.</summary>
    OverlayCallback,

    /// <summary>The framework guard attempt, render lock acquisition, and backend readiness checks.</summary>
    MainAdmission,

    /// <summary>The nonblocking framework guard attempt for main presentation.</summary>
    MainFrameworkGuard,

    /// <summary>Acquisition of the interface render lock before backend readiness checks.</summary>
    MainRenderGuard,

    /// <summary>The point when an admitted main render attempt is counted, before queued work.</summary>
    MainReadyAttempt,

    /// <summary>Draining of actions queued before main replay.</summary>
    MainPreQueue,

    /// <summary>Draining of actions queued after the main render attempt.</summary>
    MainPostQueue,

    /// <summary>The call to the original DXGI Present target.</summary>
    DxgiNativePresent,

    /// <summary>The call to the original legacy ReShade Present handler.</summary>
    LegacyReShadeContinuation,

    /// <summary>The backend update that builds the UI, captures the main snapshot, and submits secondary viewports.</summary>
    Step,

    /// <summary>ImGui frame setup, input handling, and UI construction callbacks.</summary>
    UiBuild,

    /// <summary>Finalization of ImGui draw data by <c>ImGui.Render()</c>.</summary>
    UiFinalize,

    /// <summary>Acquisition of the D3D11 device guard in Step, including any protection update.</summary>
    StepDeviceGuard,

    /// <summary>Acquisition of snapshot write access in Step.</summary>
    StepWriterGuard,

    /// <summary>The ImGui platform-window update.</summary>
    Platform,

    /// <summary>Copying of the main viewport draw data into the replay snapshot.</summary>
    SnapshotCopy,

    /// <summary>Traversal, drawing, and presentation of eligible secondary viewports.</summary>
    SecondaryWork,

    /// <summary>Renderer drawing for an individual secondary viewport.</summary>
    SecondaryDraw,

    /// <summary>The native Present call for an individual secondary viewport.</summary>
    SecondaryPresent,

    /// <summary>Post-copy callbacks, including deferred release of resources used by earlier frames.</summary>
    Retirement,

    /// <summary>The backend main render attempt, including guard acquisition and snapshot checks.</summary>
    MainRender,

    /// <summary>Acquisition of the D3D11 device guard for replay, including any protection update.</summary>
    ReplayDeviceGuard,

    /// <summary>Acquisition of the backend render lock before snapshot read access.</summary>
    ReplayRenderGuard,

    /// <summary>Acquisition of snapshot read access for replay.</summary>
    ReplayReaderGuard,

    /// <summary>The renderer call for the published main viewport snapshot.</summary>
    MainReplay,
}

/// <summary>The result observed at a measured boundary.</summary>
internal enum FrameTimingOutcome : byte
{
    /// <summary>The record supplies no outcome.</summary>
    Unknown,

    /// <summary>The measured phase reached its normal completion.</summary>
    Completed,

    /// <summary>The measured phase ended exceptionally or reported a failure status.</summary>
    Failed,

    /// <summary>Callback shutdown prevented access to the backend.</summary>
    Shutdown,

    /// <summary>The nonblocking framework guard attempt encountered contention.</summary>
    Contended,

    /// <summary>A DXGI Present status test skipped overlay rendering.</summary>
    Test,

    /// <summary>The callback's output window differed from the game display window.</summary>
    OtherTarget,

    /// <summary>The rendering backend or required font atlas was unavailable.</summary>
    BackendUnavailable,

    /// <summary>Resize or a changed resize generation prevented the operation.</summary>
    Suspended,

    /// <summary>Replay found no valid published draw data.</summary>
    MissingSnapshot,

    /// <summary>A precondition prevented the measured work, such as an unresolved target or missing device guard.</summary>
    Skipped,
}

/// <summary>The presentation target's relationship to the game display.</summary>
internal enum FrameTimingTarget : byte
{
    /// <summary>The game display relationship was unavailable or irrelevant to the measurement.</summary>
    Unknown,

    /// <summary>The callback target matched the game display.</summary>
    Display,

    /// <summary>The callback target was classified as a different output window.</summary>
    Other,
}

/// <summary>The timing data represented by a record.</summary>
internal enum FrameTimingRecordKind : byte
{
    /// <summary>A callback entry sampled independently of its completion.</summary>
    Cadence,

    /// <summary>A measured phase with a start and end tick.</summary>
    Elapsed,

    /// <summary>A point at which a snapshot became available for replay.</summary>
    Publication,

    /// <summary>An observed operation event with a single timestamp.</summary>
    Point,
}

/// <summary>Session and UI finalization metadata stored with a published snapshot.</summary>
internal readonly record struct FrameTimingSnapshot(long SessionId, long Id, long RenderedTick);

/// <summary>A callback entry, completed span, or point-in-time event in a recorder session.</summary>
/// <remarks>
/// Ticks use a common monotonic clock. Cadence samples record CPU callback entry times independently of completion.
/// Elapsed spans include nested work and can overlap across threads. The operation ID ties related records together
/// within a session. Native status is the measured call's return status, when available.
/// Replay age runs from UI finalization to replay entry for snapshots from the same session. An unknown native status,
/// snapshot ID, or snapshot age is represented by null.
/// </remarks>
internal readonly record struct FrameTimingRecord(
    long OperationId,
    long StartTick,
    long EndTick,
    int ThreadId,
    FrameTimingSource Source,
    FrameTimingSpan Span,
    FrameTimingRecordKind Kind,
    FrameTimingOutcome Outcome = FrameTimingOutcome.Unknown,
    FrameTimingTarget Target = FrameTimingTarget.Unknown,
    int? NativeStatus = null,
    long? SnapshotId = null,
    long? SnapshotAgeTicks = null);
