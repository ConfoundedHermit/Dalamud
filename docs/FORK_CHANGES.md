# Dalamud Plus fork changes

Changes to Dalamud Plus relative to upstream Dalamud.

## Rendering and presentation

[Dx11Win32Backend](../Dalamud/Interface/ImGuiBackend/Dx11Win32Backend.cs) and [InterfaceManager](../Dalamud/Interface/Internal/InterfaceManager.cs) separate UI construction from presentation in every presentation mode:

- Game-thread `Step()` processes input, builds plugin/Dalamud UI, completes ImGui platform-window updates, captures the main viewport, and renders/presents secondary viewports.
- Presentation callbacks call `Render()` to replay the main viewport snapshot, including repeated calls and calls from worker threads.
- The game kernel `SwapChain::Present` hook is required for scene setup in default DXGI, legacy ReShade, addon Present, and addon overlay modes. Missing the signature faults setup and cleans up partial hooks.
- Successful, skipped, and failed Steps converge on one original kernel Present call.

New [DrawDataSnapshot](../Dalamud/Interface/ImGuiBackend/DrawDataSnapshot.cs) copies main-viewport draw commands, vertices, indices, and supporting headers into owned unmanaged buffers. Texture IDs and callback payloads are borrowed. Buffer/header growth preserves allocation ownership on failure. Replay is disabled before capture and published only after copying, secondary submission, and `PostCopy` succeed.

Secondary viewports draw and present once per Step on their game/window-owning thread. Worker replay only submits the main snapshot, avoiding secondary Present calls that can block on window messages while the game thread waits in native Present.

`RunBeforeImGuiRender` and `RunAfterImGuiRender` now bracket main snapshot replay on the presentation thread, after secondary presentation. `CumulativePresentCalls` counts admitted attempts after readiness checks and before queued work. Recorded draw callbacks can execute repeatedly and on presentation workers; copied draw-list headers expose copied vectors rather than a complete live ImGui object.

## Synchronization and resize

[ThreadSafety](../Dalamud/Utility/ThreadSafety.cs) adds a framework/render guard and a native-presentation handoff:

- Presentation uses nonblocking framework admission and skips the transaction on contention.
- The interface render lock serializes backend initialization, readiness checks, queues, counters, and replay. A same-thread flag rejects recursive backend construction.
- Backend configuration and callbacks are installed before publishing the backend and scene service.
- Graphics transactions enter the native `ID3D11Multithread` gate before backend rendering/snapshot locks. Step holds snapshot write exclusion; replay and viewport capture use read exclusion.
- Native framework update/finalize work uses the framework guard. Managed framework ticks execute before its acquisition.
- Original kernel Present temporarily releases every recursive level of framework ownership on the calling thread, then restores the exact depth, including exceptional paths.
- Test presents, unrelated output windows, unavailable backends, and unbuilt font atlases leave presentation queues and bookkeeping untouched.

Recognized NvPresent participation enables automatic `SetMultithreadProtected(true)` in every presentation mode. The backend reasserts protection after native device entry and disables it during disposal only if it observed a disabled-to-enabled transition. A false policy makes no protection-setting calls; explicit device-gate use is independent of that policy.

`BeginResize()` introduces independent, idempotent scopes that can finish on another thread. Pending/active scopes revoke replay and advance a generation under short writer transactions. Native `ResizeBuffers` calls and ReShade destroy/init gaps execute without retaining a thread-owned device or snapshot writer lock.

Step skips work if resize is active on entry. Resize overlapping an already-started Step completes ImGui platform bookkeeping, then skips capture, secondary submission, and retirement. Replay resumes after all resize scopes finish and a fresh Step succeeds.

[ReShade lifecycle handling](../Dalamud/Interface/Internal/InterfaceManager.AsReShadeAddon.cs) retains each destroy callback's exact backend and resize scope, keyed by API-object identity. It separates nested destroy/init scopes from failed-init recovery, preserves outer pending scopes during recovery, ignores unrelated targets, and keeps unsuccessful targets suspended until recovery or cleanup.

## Resource lifetime and textures

`InterfaceManager.DeferUntilFrameRetired(Action)` replaces end-of-render cleanup with two retirement batches. At `PostCopy`, the previous batch executes and pending actions become the next batch. Resources borrowed by a snapshot survive until a later successful capture replaces that frame. Resize, skipped Steps, and failures before `PostCopy` preserve the batches; shutdown drains both after backend disposal.

The texture/font changes apply this timing throughout rendering:

- Deferred texture disposal and font-lock release follow snapshot retirement.
- [ImGuiHelpers](../Dalamud/Interface/Utility/ImGuiHelpers.cs) queues blur-payload return when recording commands, replacing return on first replay. `BlurCallbackDataPool` rents/returns pooled slots atomically.
- [DrawListTextureWrap](../Dalamud/Interface/Textures/TextureWraps/Internal/DrawListTextureWrap.cs), [ViewportTextureWrap](../Dalamud/Interface/Textures/TextureWraps/Internal/ViewportTextureWrap.cs), and debug-preview eviction retain exact replaced COM references in cleanup closures. Replacement allocation completes before old ownership transfers.
- Offscreen draw-list drawing holds the framework guard across state capture, clearing, rendering, alpha conversion, and restoration, including empty-data paths.
- Viewport capture reads the effective main display chain or resolves a secondary viewport's managed renderer handle under graphics/snapshot exclusion, replacing the assumed raw renderer-user-data layout. Resize suspension retries capture, including one-shot requests.
- Texture tracking and capture task completion execute outside backend graphics locks.
- [TextureManager](../Dalamud/Interface/Textures/Internal/TextureManager.cs) runs `RunDuringPresent` inline only when the caller owns the interface render lock; other callers queue work.
- `ITextureProvider.CreateFromTexFile(TexFile)` directly converts/creates on the calling thread without loader throttling, replacing blocking on the asynchronous loader's result.
- The alpha-fill drawer owns a 1x1 immutable white texture/SRV instead of fetching the shared white asset while holding graphics locks.

## Initialization and render-state fixes

Backend construction owns rollback for ImGui/ImPlot contexts, device references, renderer, input, and viewports. Renderer/input cleanup runs while the owned ImGui context is alive/current, followed by context, snapshot, and COM cleanup. Failure restores prior contexts, and repeated backend cleanup is guarded even after early device-acquisition failure.

Input initialization retains its viewport handler before failure can occur. Cleanup destroys windows before detaching callbacks and unregisters only a class it successfully registered. Renderer/input constructors release partial state before propagating failure.

Renderer state backup moves before render-target mutation, restoring caller state on empty and exceptional paths. [D3D11DeviceContextStateBackup](../Dalamud/Interface/ImGuiBackend/Helpers/D3D11/D3D11DeviceContextStateBackup.cs) fixes trailing UAV-slot trimming to decrement the upper bound and offsets the restored UAV pointer to its first restored slot.

## Swap-chain discovery and ownership

The ReShade-only internal unwrapper is replaced by [separate COM, ReShade, and NvPresent helpers](../Dalamud/Interface/Internal/Unwrapper/). Selected paths repeatedly peel ReShade/NvPresent in either nesting order, sharing cycle detection and a limit of 64 recognized wrappers.

COM checks cover mapped ranges, vtables/function pointers, exact `S_OK` with non-null query results, self-reference, and forward progress. Memory traversal uses actual region ends. The backend's separate ReShade/GShade peeler receives matching bounds and progress fixes.

`SwapChainForwardingResolver` derives NvPresent's wrapped chain from its `GetDesc` forwarding code. The decoder requires receiver/descriptor forwarding to vtable slot 12 and limits analysis to 128 bytes, 64 instructions, four aligned pointer fields, and field displacement `0x10000`. Unsupported instructions, control flow, argument clobbering, or forwarding layouts fail the selected strict unwrap path.

`NvPresentDetector.Inspect()` examines an owned local reference before hook-mode selection and global unwrapping:

| Result | Behavior |
| --- | --- |
| `Detected` | NvPresent module ownership is identified; enables device protection in every mode. |
| `NotDetected` | Discovery reaches a system-directory DXGI/D3D11 terminal without NvPresent. |
| `Unknown` | Opaque, invalid, cyclic, or unresolved discovery logs a warning and retains the conditional fallback. |

Detection is independent of private forwarding decoding. Successful addon/legacy ReShade selection bypasses strict NvPresent unwrapping while using detection for protection policy.

[SwapChainHelper](../Dalamud/Interface/Internal/SwapChainHelper.cs) retains the acquired unwrapped COM reference until hooks/backend resources are disposed, replacing a cache whose pointer could outlive its temporary owner. Reset explicitly releases that reference.

## Asynchronous shutdown and native callbacks

[ServiceManager](../Dalamud/Service/ServiceManager.cs) introduces one shared asynchronous unload task for explicit unload and initialization-failure cleanup. New `IInternalAsyncDisposableService` supports awaitable global and scoped disposal, and `Dalamud.Unload` signals completion after the sequence finishes.

Each global unset starts on the framework thread while available and not unloading, then can yield that thread while disposal completes. IoC registration is removed before disposal; service-locator access lasts until completion or failure logging.

New [InterfaceCallbackLifetime](../Dalamud/Interface/Internal/InterfaceCallbackLifetime.cs) tracks presentation, kernel Present, resize, ReShade events, the interface's main-window WndProc handler, cursor, and deferred setup. Native detour scopes cover original native continuations; event/setup scopes cover managed handlers. Stopping rejects new backend work while retaining hook resources for running callbacks. Late detours use saved native targets after hooks are disposed.

Interface teardown becomes asynchronous:

1. Stop admission, detach handlers, and disable interception on the framework thread, retaining hooks/trampolines for running callbacks.
2. Await callback/native continuation completion off-thread without graphics/framework guards, then dispose hooks and the ReShade receiver and release resize scopes.
3. Return graphics/window teardown to the framework thread; dispose font resources, backend/contexts and secondary windows, release display-chain ownership, and drain retirement queues.

Scene-setup failure uses the same hook cleanup before releasing the chain, without synchronously waiting inside the failing setup callback.

ReShade addon disposal replaces native callback unregistration with atomic managed-receiver detachment. One rooted process-lifetime registration keeps native callbacks valid and inert after detachment. Successful registration can accept a replacement receiver; repeat disposal of an old receiver cannot detach its replacement. Partial registration remains rooted and unavailable for retry. Temporary import-hook callbacks and original delegates are retained for callers that already fetched a detour.

ReShade export discovery is deferred and uses the process-module cache instead of running in the outer class's static initializer.

## Hook registration and diagnostics

[HookManager](../Dalamud/Hooking/Internal/HookManager.cs) rejects a second undisposed hook with the same callback invocation list at the same resolved target. Reloaded, SafetyHook, and function-pointer-variable backends perform the check under the registration lock before native allocation/mutation. Disabled hooks reserve registration until disposal; duplicate creation throws `InvalidOperationException`.

Managed callback identity uses method and receiver reference across delegate types. Native delegate comparison includes the native pointer, and invocation-list order/length matters. Different callbacks can chain, and one callback can target different addresses.

`HookInfo` retains the resolved target after disposal. Verbose address-description work is skipped when verbose logging is disabled. [HookWidget](../Dalamud/Interface/Internal/Windows/Data/Widgets/HookWidget.cs) captures a separate original continuation per diagnostic hook and disposes the previous manual hook before replacement.

## Addon lifecycle dispatch

[AddonLifecycle](../Dalamud/Game/Addon/Lifecycle/AddonLifecycle.cs) restores the prior `isInvokingListeners` value in `finally` around complete dispatch. Empty dispatch no longer leaves later mutations deferred, and nested dispatch no longer clears the outer guard.

## Module-cache refactor

[CurrentProcessModules](../Dalamud/Support/CurrentProcessModules.cs) replaces its explicit static backing field with property backing storage and clarifies the cache/invalidation documentation.

## Build and dependencies

| Change | Effect |
| --- | --- |
| `DalamudVersion`: **15.0.3.9 -> 15.0.4.0** | Updates assembly/file/package versions through the existing version properties. |
| `tools/find-cmake.cmd`: `vswhere -products *` | Includes standalone Visual Studio Build Tools in CMake discovery. |
| `sqlite-net-pcl`: **1.9.172 -> 1.11.285** | Updates the database dependency/native packaging graph to SQLitePCLRaw 3.0.3 and SourceGear.sqlite3 3.53.3. |
| Injector direct `System.Security.Cryptography.Pkcs` **8.0.2** | Overrides PeNet's transitive 8.0.1 dependency. |
| Standalone testbed direct `Newtonsoft.Json` **13.0.4** | Overrides the old 9.0.1 resolution through Veldrid in the project that disables central package management. |

## Fork identity and documentation

- README and About/Changelog windows identify **Dalamud Plus / ConfoundedHermit/DalamudPlus** and direct fork support to its issue tracker.
- CODEOWNERS changes from the upstream maintainer group to `@ConfoundedHermit`.
- The contribution policy accepts AI-assisted contributions with contributor responsibility, replacing upstream's prohibition.
- README replaces an obsolete component entry and adds build/test, release ZIP/checksum, injection/directory-override, Wine, and launcher-reversion instructions.
- New bug/feature issue forms collect reproduction/use cases, fork tag/commit, launcher/host, GPU/driver, frame generation, ReShade/overlays, plugin details, and supporting logs.
- New [FORK-MAINTENANCE.md](FORK-MAINTENANCE.md) defines upstream mirror `master`, fork development/release `main`, upstream ingestion, automation settings, and release handling.

## CI and release automation

### Build CI

[Build CI](../.github/workflows/main.yml) uses explicit read-only repository permissions and removes upstream certificate signing, attestation, and distribution-repository deployment. The PR API-compatibility job moves from .NET 7 to .NET 10.0.100. Checkout steps across workflows are pinned to checkout v4.3.1 by SHA.

### Release tags and packaging

[tools/release.ps1](../tools/release.ps1) creates one local annotated date tag on clean `main`, replacing automatic version edits, commits, and pushing. Tags use `dalamud-plus/vYYYYMMDD` with an optional positive same-day sequence. The helper checks calendar dates and existing tag names and includes the pinned framework version in the annotation.

New [release.yml](../.github/workflows/release.yml) handles published fork releases. It checks tag/checkout identity and fork/main history, builds/tests Release, creates the hash manifest, checks required bundle files, and packages `bin/Release` contents at the ZIP root. It uploads `Dalamud-win-x64.zip` and its SHA-256 companion using job-scoped write permission. Reruns can replace matching assets with `--clobber`.

### Changelogs

[Changelog generation](../.github/generate_changelog.py) changes from latest-numeric-tag/associated-PR queries and Discord posting to offline Git generation for an explicit fork tag. It accepts an ancestral previous tag or selects the nearest reachable earlier fork release. Direct and merge commits include authors and commit/compare links, with escaped subjects/authors.

Release-note publication replaces only a marked generated section and preserves handwritten text. Tag pushes/manual dispatch produce Markdown artifacts; published releases use a separate write-permission job to update notes. PR API queries, the `requests` dependency, and Discord webhook publication are removed.

### Upstream integration

New [upstream-sync.yml](../.github/workflows/upstream-sync.yml) and [sync_upstream.py](../.github/sync_upstream.py) maintain the clean upstream mirror and open integration PRs into `main`. They fetch tips without tags, stop on mirror divergence or merge conflicts, prepare a merge on a detached checkout, and publish the mirror/candidate branch through a non-forcing atomic push. Candidate branches identify the source revision pair, and existing closed PRs for that pair are respected.

### Dependency updates and backups

[Submodule automation](../tools/update-fork-submodule.py) replaces forced master-targeting branches with detached worktrees and immutable update branches targeting fork `main`. It can update ClientStructs or Lumina.Excel and opens PRs for those updates. Scheduled runs require `ENABLE_SUBMODULE_UPDATES=true`.

[Backup automation](../tools/mirror-fork.py) replaces unconditional daily forced pushes to upstream forge mirrors with optional fork-owned GitLab/Codeberg destinations. Pushes are atomic per destination, without forcing or deleting refs. Scheduled runs require `ENABLE_MIRROR_BACKUP=true` and configured destinations/keys.

Upstream sync, dependency updates, and backups run on six-hour schedules, with manual dispatch available. `tag-build.yml` and `rollup.yml` are removed from `main`, retiring automatic numeric tagging and the upstream `api16` rollup.

## Regression-test additions

New fixtures cover snapshot allocation/resize, backend construction/admission, draw-list and texture dispatch synchronization, secondary-viewport presentation, native-presentation lock handoff, callback draining, asynchronous shutdown, ReShade registration lifetime, NvPresent forwarding decoding, duplicate hooks, and addon dispatch. A shared presentation fixture supplies controlled swap-chain/backend behavior.

The blocking simulated ReShade dispatcher uses a dedicated long-running task instead of `Task.Run`, preventing ThreadPool contention from delaying its paused callback.
