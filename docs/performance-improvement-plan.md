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
- Large watcher metadata burst path counts, batch counts, elapsed time, remaining backlog, and new-event interruptions.
- Persistent thumbnail-cache trim entry counts, released bytes, and elapsed time.

The instrumentation is implemented in `ShellViewModel.Performance.cs`. It does not improve performance by itself.

## Improvement backlog

| Priority | Area | Current behavior | Target behavior | Status |
| --- | --- | --- | --- | --- |
| Critical | Startup drive discovery | A disconnected mapped or network drive can block startup while drive properties, its `StorageFolder`, or its thumbnail are queried. | Perform drive initialization away from the UI thread, apply a bounded timeout, skip unavailable drives, and continue startup. | Implemented; disconnected-drive verification pending |
| Very high | Back navigation | A bounded in-memory snapshot is restored before a background enumeration replaces it with current file-system state. | Restore a recent folder snapshot immediately, then validate it in the background using the directory watcher or a refresh. | Implemented and locally verified |
| Very high | Enumeration critical path | Generic icon waits, shortcut/URL parsing, file symbolic-link resolution, ZIP association checks, and duplicate Git detection have been removed from Win32 enumeration. | Create basic items from `WIN32_FIND_DATA` first. Defer expensive enrichment, or run bounded enrichment only for visible items. | Implemented; profiling pending |
| High | UI collection updates | Flat, filtered, and grouped batches use sorted single-item notifications. Final projections use at most 64 Add/Remove/Move operations and fall back to one Reset only for larger or wholly replaced projections. | Extend differential updates while preserving selection, grouping, and layout behavior. | Implemented; grouped refresh verified |
| High | Extended properties and thumbnails | Realized containers drive enrichment, recycled containers now cancel per-item metadata, retry, and generated-thumbnail work, and thumbnail work runs before lower-priority metadata. Initial Shell calls and lower-priority metadata are bounded, and validated thumbnails are persisted. | Split enrichment by priority, cancel work outside the visible range, use bounded concurrency, and persist validated thumbnail results. | Implemented and locally verified |
| Medium | Sorting and grouping | Progressive batches are inserted in final sort order. Group members and group headers are reordered with Move notifications, avoiding a complete Reset for bounded diffs. | Keep the single final sort unless profiling proves incremental ordering is cheaper; avoid an additional full collection reset where possible. | Implemented; grouped refresh verified |
| High | Watcher modification bursts | Modified paths are deduplicated with a case-insensitive index, matched against one collection snapshot, and refreshed in batches of at most eight concurrent storage-property requests. | Keep watcher queueing and item matching linear while bounding storage and cloud work triggered by large sync, extraction, or build bursts. | Implemented and locally verified |

## Existing partial improvements

- Win32 and `StorageFolder` enumeration append unsorted intermediate batches and sort once after enumeration.
- Extended properties are initiated from virtualized item-container callbacks, so unrealized items are not eagerly enriched by that path.
- Non-cached thumbnail generation is protected by a semaphore.
- Generic icons remain cached in memory by folder/file extension.
- Validated file thumbnails are also persisted under the app's local cache folder; the advanced settings page reports, trims, and clears this cache.

## Planned sequence

### Phase 1: Measurement

- [x] Record enumeration, first-batch, complete-load, and thumbnail timing.
- [x] Collect local SSD, Git repository, and image-folder measurements from the instrumented development build.
- [x] Collect representative company OneDrive and mapped-network-drive measurements from the instrumented development build.
- [ ] Exercise the startup timeout with a genuinely disconnected mapped drive when that environment is available.

### Phase 2: Startup reliability and enumeration critical path

- [x] Bound drive discovery so an unavailable network drive cannot freeze startup.
- [x] Record skipped or timed-out drives in `debug.log`.
- [x] Remove drive-thumbnail retrieval from startup discovery, load sidebar drive icons after insertion, and coalesce duplicate icon requests for the same drive.
- [x] Stop awaiting generic icon lookup for every item during enumeration; visible containers load their icon or thumbnail through the existing enrichment callback.
- [x] Resolve Win32 item types in batches with at most eight concurrent initializations while preserving enumeration order, cancellation, the first 32-item publication threshold, ADS handling, and folder-size updates.
- [x] Confirm the Win32 first-pass display-name lookup only reads the process-local `StorageCacheService` dictionary and falls back to the raw file-system name; it performs no Shell or disk I/O.
- [x] Defer shortcut/URL parsing, file symbolic-link target resolution, and ZIP default-application checks until the existing visible-item enrichment callback.
- [x] Limit deferred shortcut and archive enrichment to four concurrent operations, debounce collection reordering, and cancel obsolete refresh work when navigation changes.
- [x] Reuse the Shell view model's Git result in Win32 enumeration instead of performing the same repository and HEAD detection a second time.
- [x] Capture one cancellation token per folder load and cap intermediate Win32 batches at 128 items so a superseded load cannot observe a replacement token or monopolize the UI thread with an unbounded batch.
- [x] Profile the repository root: Git detection completed in 52.1 ms and was reused by enumeration, so a separate deferred item model is not justified by the local measurement. Re-profile unusually large or network-hosted repositories before changing that decision.

### Phase 3: Collection updates

- [x] Append ungrouped, unfiltered intermediate batches without clearing or resetting already displayed items.
- [x] Insert filtered intermediate and final-tail items using the active sort order without rebuilding already displayed items.
- [x] Apply flat final projections with at most 64 single-item Add, Remove, or Move notifications; use one Reset for larger or wholly replaced projections.
- [x] Add safe single-item updates for grouped intermediate and final projections, including Move-based member and group-header ordering.
- [x] Plan grouped reordering before applying it, cap differential updates at 64 Move notifications, and use one Reset for larger reorders without first applying a partial Move sequence.
- [x] Verify selection and scroll preservation during a grouped refresh; an 85-item folder retained its selection and exact 19.0439% scroll position with no Reset notification.
- [ ] Verify keyboard focus plus selection and scroll preservation across every filter, sort, layout, and snapshot-reconciliation combination.
- [x] Avoid the final flat-view Reset when progressively inserted items already match the final sorted projection.

### Phase 4: Back-navigation snapshots

- [x] Cache up to four recent folder snapshots for two minutes, with per-folder and total item limits plus sort, group, layout, filter, and visibility identity.
- [x] Restore a compatible snapshot before starting disk enumeration.
- [x] Reconcile additions, removals, and modifications by suppressing intermediate UI batches while a full background enumeration builds the authoritative replacement collection.
- [x] Invalidate a parent-folder snapshot after watcher-observed item changes, and clear snapshots after enumeration-affecting visibility, sizing, sort, group, or layout setting changes.
- [x] Apply strict count and lifetime limits so snapshot-held item and thumbnail objects are not retained indefinitely.

### Phase 5: Thumbnail and metadata pipeline

- [x] Load the visible item's icon or thumbnail before shortcut/archive and storage-property enrichment; keep cloud state, tags, and media properties on the lower-priority UI update path.
- [x] Restrict enrichment to realized item containers and cancel per-item metadata, thumbnail-generation, and retry work when a container enters the recycle queue; near-visible prefetch remains unimplemented.
- [x] Limit initial cached-thumbnail, icon, and overlay Shell calls to six concurrent operations while keeping non-cached thumbnail generation serialized.
- [x] Implement the existing persistent thumbnail-cache settings, size limit, least-recently-used eviction, size reporting, and clear operation.
- [x] Validate persistent entries by normalized path, requested pixel size, modification timestamp, and file size.
- [x] Cancel queued persistent-cache writes when their item leaves the realized range or its folder load is superseded, without faulting fire-and-forget tasks canceled before acquiring the cache I/O semaphore.
- [x] Limit persistent-cache reads to eight concurrent operations across tabs and propagate cancellation instead of treating a canceled disk read as a cache miss followed by a Shell fallback.
- [x] Track cache size incrementally under the serialized write lock and perform a full LRU enumeration and sort only when the configured size limit is actually exceeded.
- [x] Limit lower-priority storage, cloud, tag, and media-property enrichment to four concurrent operations across tabs while allowing each realized item's thumbnail to load first.
- [x] Prevent cloud thumbnail retries from replacing an active generated-thumbnail request or overlapping an existing timer retry, and avoid repeating icon-overlay Shell queries on thumbnail-only retries.

### Phase 6: Watcher reconciliation

- [x] Replace the modified-path queue's linear duplicate checks with a case-insensitive path index that is updated as entries are dequeued.
- [x] Materialize each watcher update's requested paths once and match them against one collection snapshot instead of repeatedly copying and cross-scanning the folder collection.
- [x] Limit watcher-triggered storage, cloud, and basic-property refreshes to batches of eight concurrent items.
- [x] Reorder watcher-affected group members and headers with Move notifications, processing only groups marked unsorted instead of issuing a full group order Reset.
- [x] Drain consecutive metadata batches without an artificial 200 ms wait while yielding at batch boundaries when new watcher events arrive.
- [x] Log watcher metadata drains larger than 32 items or slower than 500 ms without including file-system paths.
- [x] Profile a 96-file build-output-style modification burst in an interactive development package; the deduplicated 93-path backlog drained in three batches without leaving queued work.
- [x] Release the folder-enumeration semaphore while reading watcher-triggered storage and cloud properties, cancel obsolete batches on navigation, and validate the folder-load generation before applying results.

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

### 2026-08-17

- Added bounded per-tab folder navigation snapshots with a two-minute lifetime, a maximum of four folders, 2,000 items per folder, and 4,000 items in total.
- Snapshot compatibility includes layout, sort, grouping, filtering, hidden/system/dot-file visibility, alternate data streams, and folder-size settings.
- A restored snapshot is displayed immediately while a complete enumeration runs without appending duplicate intermediate batches; the final sorted collection remains authoritative.
- Watcher-observed item changes invalidate the affected parent-folder snapshot, and enumeration or layout setting changes clear cached snapshots.
- Removed shortcut/URL parsing, file symbolic-link resolution, and ZIP default-application checks from Win32 enumeration. Visible items now perform those checks through a four-operation bounded enrichment path; folder/archive classification changes share one debounced collection refresh.
- Replaced the asynchronous Win32 file-item factory with a synchronous basic-item factory now that its Shell work is deferred.
- The combined snapshot and deferred-enrichment changes completed a real isolated-output `Debug|x64` build with exit code 0 and produced a new `Files.dll` dated 2026-08-17.
- The loose Debug package registered and its process launched from the new output, but the tool session could not enumerate an interactive top-level window; automated back-navigation input did not execute and is not counted as runtime verification.
- Flat and filtered progressive batches now insert at their active sorted positions, including the final tail. Final projections use at most 64 single-item differential operations and otherwise fall back to one Reset; grouped views retain the existing Reset path.
- Initial cached-thumbnail, icon, and overlay requests now use a six-operation semaphore separate from the existing single-operation non-cached thumbnail generator.
- The integrated collection and thumbnail-concurrency changes completed an isolated-output `Debug|x64` build with exit code 0.
- The snapshot implementation completed a focused `Debug|x64` build using the IDE-isolated intermediate output directory. Interactive back-navigation and stale-item reconciliation verification remain pending.
- Added a persistent thumbnail cache keyed by normalized path, physical pixel size, modification timestamp, and file size. Shell-cache hits and newly generated thumbnails populate it; cache hits skip Shell thumbnail generation.
- Connected the advanced thumbnail-cache settings to actual size reporting, trimming, least-recently-used eviction, and clearing.
- Recycled containers now cancel active per-item metadata, generated-thumbnail, and delayed retry work rather than setting an otherwise unchecked cancellation flag.
- Grouped progressive and bounded final updates now use Add, Remove, and Move notifications for both members and group headers. Large or wholly replaced projections retain one Reset fallback.
- Removed the duplicate Git repository and HEAD lookup from Win32 enumeration and reused the view model's existing result.
- Three isolated-output `Debug|x64` builds after the cache, grouped-update, and cancellation changes completed with exit code 0. The first restore attempt was blocked by the host's NuGet TLS credential state; the approved external restore and subsequent builds succeeded.
- Registered the generated `FilesDev_4.2.6.0_x64` package from `bin\codex`, launched `Files.exe`, and confirmed the process remained responsive. Automated keyboard navigation did not produce folder-load log entries, so it is not counted as back-navigation verification.
- Fixed a startup failure mode exposed by the isolated output: a missing tray icon previously faulted an unobserved initialization task and left the splash screen visible. Main-window initialization is now awaited and logged, and tray-icon creation fails independently without blocking the app. The development build subsequently reached the main view and remained responsive; measured initialization was 747-1,358 ms.
- Navigating back to a 20-item folder restored its snapshot and published the first UI state in 12.9 ms while the authoritative background enumeration completed in 922.5 ms.
- A grouped refresh of an 85-item image folder used zero Reset notifications. The selected item remained selected and the scroll position remained 19.0439% before and after refresh.
- The first image-folder run persisted 48 validated thumbnails occupying 261,348 bytes. A second launch accessed all 48 without rewriting them; a later refresh reported 18 persistent hits among 20 cached/icon requests and generated no thumbnails.
- Repository-root profiling measured Git detection at 52.1 ms, the first 21-item UI batch at 507.4 ms, and the completed load at 1,270.4 ms with zero Reset notifications.
- Repeated refresh profiling of the 85-item folder completed in 281.8 ms, published its first batch in 79.1 ms, and used zero Reset notifications.
- A rapid-navigation stress test switched from the 4,911-item `C:\Windows\System32` folder to the 13-item source folder. Before the cancellation-token correction, the obsolete load completed as successful, issued one Reset, and delayed the next load by 990.1 ms. After the correction, the obsolete load completed as unsuccessful after processing 680 items, issued no Reset, and released the next load after 169.5 ms; the final source-folder load completed successfully and the window remained responsive.
- The post-cancellation-change isolated-output `Debug|x64` build completed with exit code 0.
- The company OneDrive root loaded 8 items with a 74.7 ms first batch and a 1,374.0 ms complete load; the mapped `W:` network root loaded 8 items with a 120.0 ms first batch and a 766.4 ms complete load. Both used zero Reset notifications and remained responsive.
- `Shell:RecycleBinFolder` loaded 318 items through the StorageFolder provider with a 930.9 ms first batch and a 3,686.0 ms complete load, using zero Reset notifications while the window remained responsive.

### 2026-08-20

- Fixed a selection-snapshot race where navigation captured the outgoing folder's selected items, then the incoming page overwrote that snapshot with its initially empty selection. Selection is now captured only before navigation changes the active content page.
- The focused isolated-output `Debug|x64` build completed with exit code 0 after the selection-snapshot correction.
- Windows App Runtime 2.4 was already registered for the interactive user, so the attempted 2.3.1 runtime installation was correctly rejected as a downgrade and no runtime was installed or changed. The current build launched successfully through the existing `FilesDev` package identity and remained responsive.
- Watcher modification bursts now use a case-insensitive path index instead of repeated queue scans, perform two linear collection passes instead of per-path full scans, and limit storage-property refreshes to eight concurrent items. Interactive burst profiling is recorded below.
- Watcher-driven date or size changes that move an item between groups now reuse differential Move ordering; already sorted groups and group headers are skipped.
- The combined watcher queue, bounded property refresh, and differential group-ordering changes completed an isolated-output `Debug|x64` build with exit code 0.
- Group member and header ordering now plans moves from one collection snapshot. Reorders requiring more than 64 moves use one bulk Reset, avoiding both repeated collection copies and an unbounded stream of UI notifications.
- The bounded grouped-reorder implementation completed an isolated-output `Debug|x64` build with exit code 0 together with the watcher burst changes.
- Large watcher metadata backlogs now drain continuously in 32-item batches instead of waiting 200 ms between later batches. Newly queued watcher operations interrupt the drain at the next batch boundary so they can be coalesced or prioritized before continuing.
- The continuous watcher-drain and burst-instrumentation changes completed an isolated-output `Debug|x64` build with exit code 0.
- Persistent thumbnail writes now inherit the item or generated-thumbnail cancellation token. Obsolete writes stop waiting on the serialized cache I/O queue and release their retained image buffers sooner.
- The cancelable persistent-thumbnail-write changes completed an isolated-output `Debug|x64` build with exit code 0.
- Persistent thumbnail reads now use an eight-operation service-level semaphore shared across tabs. Canceled reads propagate immediately instead of continuing into the Shell thumbnail fallback path.
- The bounded persistent-thumbnail-read changes completed an isolated-output `Debug|x64` build with exit code 0.
- Persistent thumbnail writes now update an in-memory size total from the overwritten and replacement file lengths. The previous unconditional full LRU sort after every 16 writes is removed; actual trims log removed entries, released bytes, and elapsed time.
- The incremental thumbnail-cache size tracking changes completed an isolated-output `Debug|x64` build with exit code 0.
- A real `Debug|x64` rebuild completed with exit code 0, and its output was exercised through the existing `FilesDev` development package. The main window initialized normally and remained responsive.
- Returning to the 85-item AppTiles development folder restored its navigation snapshot in 20.3 ms with one selected item. UI Automation confirmed `Logo.ico` remained selected after reconciliation, which completed with zero Reset notifications.
- A 96-file build-output-style modification burst produced 93 deduplicated watcher metadata paths. The backlog drained continuously in three batches over 993.6 ms with zero remaining paths, no new-event interruption, and a responsive main window.
- Lower-priority storage, cloud, tag, and media-property enrichment now shares four work slots across tabs. Thumbnail loading remains ahead of this bounded section, and recycled or navigated-away items cancel while waiting for a slot.
- The bounded metadata-enrichment change completed an isolated-output `Debug|x64` build with exit code 0. The resulting development DLL loaded the 4,911-item System32 folder with an initial batch in 69.9 ms, completed in 2,776.7 ms with zero Reset notifications, and remained responsive when navigating back to a 13-item snapshot.
- Thumbnail-only retries no longer repeat icon-overlay Shell queries. A cloud retry also leaves an active generated-thumbnail request or existing timer retry in place instead of canceling and replacing it, and its delayed task observes navigation cancellation.
- The deduplicated thumbnail-retry change completed an isolated-output `Debug|x64` build with exit code 0. An interactive 48-item test folder loaded its first batch in 80.1 ms and completed in 1,337.0 ms with zero Reset notifications. Twelve invalid PNG files each scheduled and ran exactly one timer retry; replacing them with valid PNG data triggered one watcher debounce per item without another failure or timer cycle, and the window remained responsive.
- Drive discovery no longer waits for a thumbnail that the sidebar subsequently queried again. Sidebar entries are inserted first, then populate a coalesced per-drive icon task in the background; a preloaded icon also bypasses both Shell fallback calls.
- On the same interactive account with C, D, W, Y, and Z available, the interval from app launch to the final drive addition fell from about 4.00 seconds to 1.90 seconds. The post-main-window interval fell from about 3.07 seconds to 0.98 seconds. All five drives remained present, each realized sidebar entry exposed its image element, and no background icon-loading exception was recorded.
- Watcher metadata now holds the enumeration semaphore only while capturing and applying a collection snapshot. Storage and cloud property I/O runs outside the lock with linked watcher and folder-load cancellation, and results apply only to item instances still present in the same load generation.
- During a 256-file modification burst, navigation began its new folder load about 184 ms after Enter and restored its first snapshot batch about 196 ms after Enter instead of waiting for the metadata drain. A separate 96-file control burst processed 95 deduplicated paths in three batches over 988.9 ms with zero remaining work, proving the non-navigation apply path still completed normally.
- Progressive folder batches now sort only their newly enumerated items and use binary search to insert them into the existing sorted collection. The shared comparer preserves folder/file priority, natural name ordering, tag handling, direction, and name tie-breaking without repeatedly sorting the entire growing collection.
- The incremental-sort change completed an isolated-output `Debug|x64` build with exit code 0. An interactive System32 load added 4,911 items in 42 batches with zero Reset notifications and completed in 3,624.3 ms. A name-descending refresh exercised 41 incremental batches, preserved visible natural descending order, completed in 2,235.8 ms, and left the window responsive.
- Folder-load diagnostics now record the average and maximum UI collection-update duration separately for incremental and Reset updates, allowing dispatcher wait, item enumeration, collection mutation, and thumbnail work to be distinguished in later profiling.
- A 256-item progressive-batch experiment reduced System32 updates from about 42 to 23, but rapid navigation could not reacquire the address bar for about 2.60 seconds and the next folder started 454.5 ms late. The larger batch and a follow-up low-priority update experiment were therefore reverted rather than trading throughput for input latency.
- Win32 enumeration diagnostics now divide wall-clock time into bounded item-initialization waits, post-processing, intermediate UI waits, and remaining enumeration work. System32 profiling showed that item initialization used only 216-378 ms and remaining enumeration 28-116 ms, while intermediate UI waits used 1,362-2,314 ms.
- Automatic thumbnail, cloud, tag, media, and Git enrichment for realized containers is now deferred until the authoritative folder enumeration completes. Explicit user-driven refreshes still run immediately, recycled containers leave the deferred queue, and navigation clears obsolete deferred work.
- In a three-run System32 comparison, deferring realized-item enrichment reduced median intermediate UI wait from about 1,573 ms to 1,424 ms and median enumeration time from about 2,223 ms to 1,814 ms. The first thumbnail request began only after enumeration completed.
- An 85-item AppTiles image folder subsequently realized 78 image elements and automatically ran 25 cached/icon requests plus generated-thumbnail work. During a System32-to-source rapid-navigation test, the canceled load started no thumbnails; the source folder began after 72.5 ms and published its snapshot after 78.0 ms while the window remained responsive.

## Risks

- File-system and Shell data can change between snapshot restoration and reconciliation.
- Incremental collection notifications behave differently across WinUI GridView, ListView, and DataGrid layouts.
- Shell, COM, network, archive, and cloud calls do not all support cooperative cancellation.
- Increasing concurrency can reduce latency but increase disk seeks, Shell contention, memory pressure, and antivirus work.
- Persisted thumbnails must not become stale after a file is replaced or modified.
