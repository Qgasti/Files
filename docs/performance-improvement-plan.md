# Files performance improvement plan

## Objective

Improve perceived folder navigation performance without regressing file operations, Shell integration, cloud storage, archives, or navigation state. Each phase should be measured before and after it is changed.

## Current measurements

Folder-load instrumentation records the following entries in `debug.log`:

- Time spent waiting for the enumeration semaphore.
- Time until the first UI batch is published.
- Enumeration duration and enumeration provider.
- Total initial folder-load duration.
- Cached/icon and generated-thumbnail counts, average duration, and maximum duration.

The instrumentation is implemented in `ShellViewModel.Performance.cs`. It does not improve performance by itself.

## Improvement backlog

| Priority | Area | Current behavior | Target behavior | Status |
| --- | --- | --- | --- | --- |
| Critical | Startup drive discovery | A disconnected mapped or network drive can block startup while drive properties, its `StorageFolder`, or its thumbnail are queried. | Perform drive initialization away from the UI thread, apply a bounded timeout, skip unavailable drives, and continue startup. | Implemented; disconnected-drive verification pending |
| Very high | Back navigation | Navigating back calls `RefreshItems`, clears the current collection, and enumerates the folder again. | Restore a recent folder snapshot immediately, then validate it in the background using the directory watcher or a refresh. | Not started |
| Very high | Enumeration critical path | Generic icon waits have been removed, but Win32 enumeration still performs shortcut parsing, ZIP association checks, symbolic-link resolution, and Git-related work while walking entries. | Create basic items from `WIN32_FIND_DATA` first. Defer expensive enrichment, or run bounded enrichment only for visible items. | In progress |
| High | UI collection updates | Ungrouped and unfiltered intermediate batches now append only new items. Grouped or filtered views and the final sorted projection still perform `Clear`, `AddRange`, and a collection `Reset`. | Extend differential updates while preserving selection, grouping, and layout behavior. | In progress |
| High | Extended properties and thumbnails | Visible containers request thumbnails, overlays, cloud state, tags, and media metadata through one large enrichment operation. Generated thumbnails are serialized, but they are not persisted by Files. | Split enrichment by priority, cancel work outside the visible range, use bounded concurrency, and persist validated thumbnail results. | Partially present |
| Medium | Sorting and grouping | A complete sort and collection reset still occurs after enumeration. | Keep the single final sort unless profiling proves incremental ordering is cheaper; avoid an additional full collection reset where possible. | Partially complete |

## Existing partial improvements

- Win32 and `StorageFolder` enumeration append unsorted intermediate batches and sort once after enumeration.
- Extended properties are initiated from virtualized item-container callbacks, so unrealized items are not eagerly enriched by that path.
- Non-cached thumbnail generation is protected by a semaphore.
- Generic icons are cached in memory by folder/file extension, but the cache is not a persistent per-item thumbnail cache.
- Thumbnail-cache settings exist in the UI, but cache size and clear operations remain TODOs.

## Planned sequence

### Phase 1: Measurement

- [x] Record enumeration, first-batch, complete-load, and thumbnail timing.
- [ ] Collect representative baselines for local SSD, large folder, Git repository, image folder, cloud folder, and network share.

### Phase 2: Startup reliability and enumeration critical path

- [x] Bound drive discovery so an unavailable network drive cannot freeze startup.
- [x] Record skipped or timed-out drives in `debug.log`.
- [x] Stop awaiting generic icon lookup for every item during enumeration; visible containers load their icon or thumbnail through the existing enrichment callback.
- [x] Resolve Win32 item types in batches with at most eight concurrent initializations while preserving enumeration order, cancellation, the first 32-item publication threshold, ADS handling, and folder-size updates.
- [x] Confirm the Win32 first-pass display-name lookup only reads the process-local `StorageCacheService` dictionary and falls back to the raw file-system name; it performs no Shell or disk I/O.
- [ ] Defer shortcut, ZIP, symbolic-link, and other Shell/file-system enrichment from the first-display path.
- [ ] Preserve cancellation when navigation changes during deferred work.

### Phase 3: Collection updates

- [x] Append ungrouped, unfiltered intermediate batches without clearing or resetting already displayed items.
- [ ] Add differential updates for grouped and filtered intermediate batches.
- [ ] Preserve filtering, grouping, selection, focus, and scroll position.
- [ ] Perform the final sort without an avoidable second full reset.

### Phase 4: Back-navigation snapshots

- [ ] Cache a bounded number of folder snapshots with timestamps and sort/layout identity.
- [ ] Restore a snapshot before starting disk enumeration.
- [ ] Reconcile additions, removals, and modifications using watcher events or a background refresh.
- [ ] Invalidate snapshots after relevant file operations or settings changes.
- [ ] Apply strict memory limits and avoid retaining thumbnail image objects indefinitely.

### Phase 5: Thumbnail and metadata pipeline

- [ ] Separate icon, thumbnail, cloud-state, tag, and media-property priorities.
- [ ] Track the visible and near-visible ranges and cancel obsolete requests.
- [ ] Tune bounded concurrency separately for cached lookup and thumbnail generation.
- [ ] Implement the existing persistent thumbnail-cache settings, size limit, eviction, and clear operation.
- [ ] Include file identity or modification information in cache validation.

## Verification gates

Every behavior-changing phase must satisfy all applicable gates:

1. Build `src/Files.App/Files.App.csproj` for `Debug|x64` with restore.
2. Confirm startup completes when a mapped network drive is unavailable.
3. Compare first-batch and complete-load timings against the recorded baseline.
4. Verify forward, back, refresh, layout switching, sorting, grouping, filtering, and selection.
5. Verify local, network, cloud, ZIP, shortcut, symbolic-link, Git, and recycle-bin locations as applicable.
6. Confirm rapid navigation cancels obsolete enumeration, metadata, and thumbnail work.
7. Inspect `debug.log` for timeouts, unhandled exceptions, and failed folder-load measurements.

## Verification log

### 2026-08-14

- `Debug|x64` build of `src/Files.App/Files.App.csproj` completed successfully with MSBuild 18.9.1.
- The generated development package was registered and launched successfully.
- Startup initialized the available C, D, W, Y, and Z drives and proceeded into folder enumeration.
- The generic-icon removal built successfully and the visible-item pipeline subsequently completed icon/thumbnail requests.
- The bounded Win32 initialization build completed successfully using an IDE-isolated intermediate output directory.
- A Start Menu Programs folder containing 26 file-system entries, including 10 shortcuts, produced 25 Files items; the excluded item was the hidden `desktop.ini`, matching the current visibility settings.
- A genuinely disconnected mapped drive was not available during this run, so the five-second timeout path still requires an environment-specific verification.

## Risks

- File-system and Shell data can change between snapshot restoration and reconciliation.
- Incremental collection notifications behave differently across WinUI GridView, ListView, and DataGrid layouts.
- Shell, COM, network, archive, and cloud calls do not all support cooperative cancellation.
- Increasing concurrency can reduce latency but increase disk seeks, Shell contention, memory pressure, and antivirus work.
- Persisted thumbnails must not become stale after a file is replaced or modified.
