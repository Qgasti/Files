# Files performance improvement plan

## Objective

Improve perceived folder navigation performance without regressing file operations, Shell integration, cloud storage, archives, or navigation state. Each phase should be measured before and after it is changed.

## Current measurements

Folder-load instrumentation records the following entries in `debug.log`:

- Time spent waiting for the enumeration semaphore.
- Time until the first UI batch is published.
- Enumeration duration and enumeration provider.
- `StorageFolder` provider-fetch elapsed and blocking wait durations, plus item-initialization, post-processing, and intermediate UI wait durations.
- Total initial folder-load duration.
- Collection semaphore and Dispatcher queue wait durations, separated from collection mutation duration.
- Cached/icon and generated-thumbnail counts, average duration, and maximum duration.
- Large watcher metadata burst path counts, batch counts, elapsed time, remaining backlog, and new-event interruptions.
- Persistent thumbnail-cache trim entry counts, released bytes, and elapsed time.

The instrumentation is implemented in `ShellViewModel.Performance.cs`. It does not improve performance by itself.

## Improvement backlog

| Priority | Area | Current behavior | Target behavior | Status |
| --- | --- | --- | --- | --- |
| Critical | Startup drive discovery | Drive roots initialize concurrently with a five-second bound. Capacity and filesystem queries run on worker threads after insertion, apply only their final values on the UI thread, and retain their own five-second query bound; thumbnails remain deferred until the drive UI is realized. | Keep disconnected mapped or network drives from blocking startup or the UI while drive roots, properties, and thumbnails are queried. | Implemented; normal local/network startup verified; disconnected-drive verification pending |
| Very high | Back navigation | A bounded in-memory snapshot restores items, selection, and Git context before background file-system and repository validation replace stale state. Folders above 2,000 items use a complete lightweight projection without thumbnails, storage objects, or extended properties. | Restore a recent folder snapshot immediately, including large folders, then validate it in the background using the directory watcher or a refresh. | Implemented and locally verified through 4,911 items |
| Very high | Enumeration critical path | Generic icon waits, shortcut/URL parsing, file symbolic-link resolution, ZIP association checks, and duplicate Git detection have been removed from Win32 enumeration. | Create basic items from `WIN32_FIND_DATA` first. Defer expensive enrichment, or run bounded enrichment only for visible items. | Implemented and locally verified |
| Very high | Virtual-location enumeration | `StorageFolder` items initialize in ordered groups of at most eight, Shell navigation resolves the root and first 32 items in one request, and the next page starts while the first UI batch is publishing. Shell, ZIP, and FTP items reuse properties returned by their enumeration, ZIP and FTP providers materialize their full listing only once per load, canceled callers stop waiting for non-cooperative operations, subsequent pages remain consolidated, and the final consolidated batch uses Normal Dispatcher priority. | Avoid repeated provider, archive, and network lookups, sequential per-item waits, serialized provider/UI work, small follow-up UI batches, and navigation stalls in Recycle Bin, archives, FTP, and other virtual providers. | Implemented; Recycle Bin, This PC, and ZIP verified; FTP endpoint verification pending |
| High | UI collection updates | Progressive batches merge sorted items into contiguous insertion ranges. Grouped batches also collect new members by group key and publish at most one range per touched group. After the first visible batch, progressive updates use low Dispatcher priority so navigation input can preempt them. Final projections coalesce contiguous inserts and removals, allow at most 64 range/move notifications and 1,024 affected items, and retain one Reset for larger, scattered, or wholly replaced projections. | Extend differential updates while preserving selection, grouping, and layout behavior. | Implemented; large flat and grouped range diffs verified |
| High | Extended properties and thumbnails | Realized containers drive enrichment, recycled containers now cancel per-item metadata, retry, and generated-thumbnail work, and thumbnail work runs before lower-priority metadata. Completed initial thumbnails survive container recycling, initial Shell calls and lower-priority metadata are bounded, validated thumbnails are persisted, and a bounded near-visible range is prefetched after enumeration. | Split enrichment by priority, cancel work outside the visible range, use bounded concurrency, persist validated thumbnail results, and add a bounded near-visible prefetch range. | Implemented and locally verified |
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
- [x] Move deferred capacity, free-space, filesystem, and cloud-quota queries off the UI Dispatcher; dispatch only final observable-property assignments.
- [x] Coalesce overlapping capacity refresh requests per drive while allowing a completed query to be refreshed later.
- [x] Stop awaiting generic icon lookup for every item during enumeration; visible containers load their icon or thumbnail through the existing enrichment callback.
- [x] Resolve Win32 item types in batches with at most eight concurrent initializations while preserving enumeration order, cancellation, the first 32-item publication threshold, ADS handling, and folder-size updates.
- [x] Confirm the Win32 first-pass display-name lookup only reads the process-local `StorageCacheService` dictionary and falls back to the raw file-system name; it performs no Shell or disk I/O.
- [x] Defer shortcut/URL parsing, file symbolic-link target resolution, and ZIP default-application checks until the existing visible-item enrichment callback.
- [x] Limit deferred shortcut and archive enrichment to four concurrent operations, debounce collection reordering, and cancel obsolete refresh work when navigation changes.
- [x] Reuse the Shell view model's Git result in Win32 enumeration instead of performing the same repository and HEAD detection a second time.
- [x] Reuse the detected Git HEAD when initializing the status bar, avoid re-reading it for every progressive collection batch, and update it only for a changed repository, Git watcher event, fetch completion, or checkout.
- [x] Make Git fetch return a tracked task, serialize overlapping fetches, keep fetch work off the UI thread, and clear the shared running state after canceled or failed operations.
- [x] Suppress automatic Git fetches for 30 seconds after a successful fetch, bound the per-repository timestamp cache to 64 entries, and let an explicit manual fetch bypass the cooldown.
- [x] Run repository path validation, HEAD and branch queries, and pure LibGit2 operation payloads away from the UI thread; record path-scan and HEAD timings separately.
- [x] Capture one cancellation token per folder load and cap intermediate Win32 batches at 128 items so a superseded load cannot observe a replacement token or monopolize the UI thread with an unbounded batch.
- [x] Cancel an active folder-load token as soon as a different working directory starts, before drive-root and Git repository detection delay the normal refresh cancellation path.
- [x] Profile the repository root: Git detection completed in 52.1 ms and was reused by enumeration, so a separate deferred item model is not justified by the local measurement. Re-profile unusually large or network-hosted repositories before changing that decision.
- [x] Initialize `StorageFolder` items in ordered groups of at most eight and propagate the folder-load cancellation token through page retrieval, per-item basic properties, and the file-by-file fallback.
- [x] Reuse basic size and date properties captured by Shell enumeration instead of resolving every Recycle Bin item by path a second time.
- [x] Stop awaiting a non-cooperative provider operation when navigation cancels its caller; request provider cancellation and observe any abandoned fault without retaining the folder-enumeration lock.
- [x] Keep virtual-provider updates at 32 items for the first visible batch and up to 300 afterward, resetting the elapsed-time sampler after publication so Dispatcher latency cannot fragment the next page into eight-item updates.
- [x] Start the next `StorageFolder` page before publishing the current intermediate batch, and record provider execution separately from the time it actually blocks enumeration.
- [x] Apply the final consolidated enumeration batch at Normal Dispatcher priority while retaining Low priority for supersedable intermediate batches.
- [x] Resolve a navigated Shell root and its first 32 items in one provider request, retain that page on the root object, and reuse it for the enumerator's first `GetItemsAsync` call.
- [x] Propagate cancellation through the combined Shell root/first-page request so a superseded navigation releases the folder-enumeration lock while non-cooperative Shell work finishes independently.
- [x] Materialize providers whose paging overload re-enumerates the complete source, currently ZIP and FTP, only once per folder load and slice subsequent pages from that result.
- [x] Preserve ZIP file and explicit-folder size and timestamps returned by both SevenZipSharp and SharpZipLib enumeration, then reuse those immutable snapshots during initial item creation instead of reopening and rescanning the archive for every item.
- [x] Reuse FTP size and timestamps returned by `GetListing` instead of opening a new connection and issuing `GetObjectInfo` for every listed file and folder.
- [x] Pass folder-load cancellation through FTP connection, listing, and fallback object-info calls so obsolete navigation work can stop inside FluentFTP.

### Phase 3: Collection updates

- [x] Append ungrouped, unfiltered intermediate batches without clearing or resetting already displayed items.
- [x] Merge each sorted ungrouped progressive batch against the current projection and publish contiguous runs with `InsertRange` instead of one notification per item.
- [x] Update directory summary, empty-state, and network-state UI on the first visible progressive batch and the final projection instead of repeating that work for every intermediate batch.
- [x] Insert filtered intermediate and final-tail items using the active sort order without rebuilding already displayed items.
- [x] Apply final projections with at most 64 range/move notifications and 1,024 affected items; coalesce contiguous inserts and removals, and use one Reset for larger, scattered, or wholly replaced projections.
- [x] Add grouped intermediate and final projection updates, including range insertion for progressive members and Move-based member and group-header ordering.
- [x] Plan grouped reordering before applying it, cap differential updates at 64 Move notifications, and use one Reset for larger reorders without first applying a partial Move sequence.
- [x] Verify selection and scroll preservation during a grouped refresh; an 85-item folder retained its selection and exact 19.0439% scroll position with no Reset notification.
- [ ] Verify keyboard focus plus selection and scroll preservation across every filter, sort, layout, and snapshot-reconciliation combination.
- [x] Avoid the final flat-view Reset when progressively inserted items already match the final sorted projection.
- [x] Keep the first visible batch at normal Dispatcher priority, then publish later progressive batches at low priority so navigation input can cancel a large obsolete load.
- [x] Record final ordering, projection construction, and UI-thread diff planning separately from collection scheduling and mutation.
- [x] Sort each progressive batch before entering the UI Dispatcher, retain the 32-item first batch, and consolidate fast Win32 follow-up batches to 256 items while slow enumeration still publishes every 500 ms.
- [x] Reuse a completed flat, unfiltered UI projection as the authoritative sorted collection only after validating its reference set and order; retain the full sort fallback for grouped, filtered, changed, or inconsistent projections.

### Phase 4: Back-navigation snapshots

- [x] Cache up to four recent folder snapshots for two minutes, with per-folder and total item limits plus sort, group, layout, filter, and visibility identity.
- [x] Restore a compatible snapshot before starting disk enumeration.
- [x] Reconcile additions, removals, and modifications by suppressing intermediate UI batches while a full background enumeration builds the authoritative replacement collection.
- [x] Invalidate a parent-folder snapshot after watcher-observed item changes, and clear snapshots after enumeration-affecting visibility, sizing, sort, group, or layout setting changes.
- [x] Apply strict count and lifetime limits so snapshot-held item and thumbnail objects are not retained indefinitely.
- [x] Store repository path, HEAD, and validity with each bounded folder snapshot; reuse them before enumeration and coalesce background Git validation per path.
- [x] Preserve complete 2,001-10,000-item folders as lightweight item projections, retain direct enriched objects only through 2,000 items, and cap all snapshots at 12,000 items per tab.

### Phase 5: Thumbnail and metadata pipeline

- [x] Load the visible item's icon or thumbnail before shortcut/archive and storage-property enrichment; keep cloud state, tags, and media properties on the lower-priority UI update path.
- [x] Restrict enrichment to realized item containers and cancel per-item metadata, thumbnail-generation, and retry work when a container enters the recycle queue.
- [x] Preserve completed initial thumbnails across container recycling while invalidating the state for active generated-thumbnail work, explicit icon refreshes, size changes, and watcher-observed file identity changes.
- [x] Prefetch at most eight near-visible items through one global operation after enumeration and visible-item work complete; cancel obsolete, off-range, navigation, and newly realized-item requests.
- [x] Limit initial cached-thumbnail, icon, and overlay Shell calls to six concurrent operations while keeping non-cached thumbnail generation serialized.
- [x] Implement the existing persistent thumbnail-cache settings, size limit, least-recently-used eviction, size reporting, and clear operation.
- [x] Validate persistent entries by normalized path, requested pixel size, modification timestamp, and file size.
- [x] Cancel queued persistent-cache writes when their item leaves the realized range or its folder load is superseded, without faulting fire-and-forget tasks canceled before acquiring the cache I/O semaphore.
- [x] Limit persistent-cache reads to eight concurrent operations across tabs and propagate cancellation instead of treating a canceled disk read as a cache miss followed by a Shell fallback.
- [x] Track cache size incrementally under the serialized write lock and perform a full LRU enumeration and sort only when the configured size limit is actually exceeded.
- [x] Coalesce repeated persistent-cache LastAccessTime writes for five minutes per entry, bound the in-memory access index, and process allowed touches through one background worker.
- [x] Limit lower-priority storage, cloud, tag, and media-property enrichment to four concurrent operations across tabs while allowing each realized item's thumbnail to load first.
- [x] Prevent cloud thumbnail retries from replacing an active generated-thumbnail request or overlapping an existing timer retry, and avoid repeating icon-overlay Shell queries on thumbnail-only retries.
- [x] Propagate per-item cancellation through font thumbnails, bitmap decoding, cloud state, FRN, media properties, and grouped header thumbnails; skip an obsolete Shell STA request before entering COM and discard its result after return.
- [x] Stop canceled callers from waiting for non-preemptible Shell STA thumbnail work so obsolete requests release their bounded thumbnail slot while the native call finishes independently.
- [x] Run `STATask` continuations asynchronously so completion cannot execute arbitrary caller continuations inline on the Shell STA thread.

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

- Added bounded per-tab folder navigation snapshots with a two-minute lifetime and a maximum of four folders. Up to 2,000 items retain their existing enriched objects; folders through 10,000 items store lightweight projections, with 12,000 items allowed across all snapshots.
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
- Folder status updates now reuse the Git HEAD already obtained by `SetWorkingDirectoryAsync`. A large Git subfolder loaded 1,017 items in 10 progressive updates without another HEAD read per batch; its first batch appeared in 473.1 ms, enumeration completed in 1,330.0 ms, the full load completed in 1,748.4 ms, and the branch name remained visible.
- Git fetch now returns a real task, runs blocking LibGit2 work on the thread pool, serializes concurrent requests, and records start, completion, cancellation, and active-operation counts. An interactive fetch completed in 576.5 ms with its active count returning from one to zero and no exception.
- Successful automatic Git fetches now have a 30-second per-repository cooldown with a 64-entry bound, while the manual Fetch command forces a refresh. In an interactive round trip, the initial fetch completed in 474.2 ms and returning after 3,786.3 ms logged one cooldown skip without starting a second fetch; the window remained responsive.
- Navigating from the repository root to its `src` folder initialized the Git context once and emitted one fetch start/finish pair; the second folder reused the same repository context and did not fetch again. Both loads used zero Reset notifications and the window remained responsive.
- Git detection results are committed only if their requested working directory is still current, preventing a slower obsolete HEAD read from overwriting a newer rapid-navigation context.
- Git repository path scans, validity checks, HEAD and branch reads, and shared LibGit2 payloads now execute on the thread pool. A cold `src` navigation spent 328.3 ms scanning parent paths and 69.7 ms reading HEAD, while the address-bar action returned in 78.5 ms and the window remained responsive throughout the background detection.
- A LibGit2 `Repository.Discover` experiment was reverted: it increased the cold path scan from 240.1 ms to 305.9 ms and still used 62.0 ms warm, so the original recursive lookup remains in place with only its execution context changed.
- During a System32-to-`src` rapid-navigation test, the obsolete System32 load canceled after 416 items with zero Reset notifications. The destination restored its 13-item snapshot in 10.8 ms, retained the correct Git repository context, and remained responsive.

### 2026-08-21

- Progressive enumeration now updates directory summary, empty-state, and network-state UI for the first visible batch and the final authoritative projection instead of repeating the same non-collection work for every intermediate batch. Collection publication frequency and the 32-item first batch are unchanged.
- Against the same-day original System32 control run, three warmed runs reduced median intermediate UI wait from 1,631.6 ms to 1,535.3 ms and median enumeration time from 2,193.7 ms to 2,021.9 ms. Median complete-load time was 2,518.4 ms, all 4,911 items were shown with zero Reset notifications, and UI Automation confirmed the final status count.
- A rapid System32-to-`src` navigation still canceled the obsolete load after 416 items. The destination remained responsive and its final status count was 13 items.
- Single-threaded initialization and 32/128-item worker-partition experiments were reverted. Single-threading increased initialization to 754.9 ms, the 32-item partition did not produce a stable improvement, and the 128-item partition delayed cancellation until 4,896 obsolete items had been processed.
- Persistent thumbnail-cache hits now update each entry's LastAccessTime at most once every five minutes per app process. The access index is bounded to 4,096 entries, newly stored thumbnails are recorded without another metadata write, and clearing the cache also clears the index and pending queue. Allowed touches run sequentially on one background worker, so thumbnail reads no longer wait for the metadata write.
- In the 85-item AppTiles folder, the previous build rewrote LastAccessTime for 22 cache files on every refresh. The updated build touched the same 22 entries in the background on the first load of a new process, preserving LRU recency, then changed zero access times on immediate refreshes while the window remained responsive.
- Thumbnail completion is now tracked separately from lower-priority item metadata. A successfully initialized thumbnail survives container recycling, while active generated work, delayed retries, explicit icon reloads, icon-size changes, and watcher-observed modification or size changes invalidate it as required.
- UI Automation scrolled the 85-item AppTiles folder from the first items to the final items and back three times. The development build emitted one aggregate event confirming that an initialized thumbnail was reused without another cache or Shell request, returned to the original top items, and remained responsive.
- Near-visible thumbnail prefetch now starts only after authoritative enumeration completes and the visible-item queue becomes idle. Each layout supplies at most eight adjacent candidates, all tabs share one prefetch slot, and range changes, navigation, or realization cancel obsolete work before visible loading proceeds.
- In the 85-item AppTiles folder, the first batch appeared in 130.2 ms and enumeration completed in 560.6 ms before the first prefetch event. Runtime diagnostics then confirmed a completed prefetch, an obsolete prefetch cancellation, and direct reuse when the prefetched item became realized; the window remained responsive with zero Reset notifications.

### 2026-08-24

- Ungrouped progressive batches now merge their sorted incoming items with the existing sorted projection and emit one `InsertRange` notification per contiguous run. Grouped views keep their existing single-item insertion path to avoid changing group-order behavior.
- Folder-load diagnostics now report incremental batch, collection-notification, and affected-item counts separately so notification reduction can be measured without confusing it with batch size.
- Folder-load diagnostics now split collection semaphore wait, Dispatcher queue wait, and collection mutation time. System32 profiling measured zero semaphore wait; Dispatcher queueing accounted for 382-449 ms in two warm 128-item runs while mutations averaged 0.8-1.4 ms per batch.
- The focused `Debug|x64` app build completed with exit code 0. The first restore was blocked by the host's NuGet TLS credential state; the approved external restore and build succeeded.
- In the 85-item AppTiles development folder, the same two progressive batches fell from 54 collection notifications to 3, with zero Reset notifications. A refresh repeated the 2/3/85 result, preserved visible sort order, and retained the selected `BadgeLogo.scale-100.png` item.
- For the 4,911-item System32 folder, the immediate pre-change control used 44 batches and 4,880 collection notifications in 4,678.7 ms. Three range-insertion runs used 83-92 notifications across 41-43 batches and completed in 2,922.4-4,104.2 ms, all with zero Reset notifications. The notification count fell by about 98%; wall-clock time remains variable because Shell and thumbnail work also contribute.
- An initial UI Automation rapid-navigation attempt did not produce the destination load event and left the exercised window unresponsive, so that run is not counted. A clean keyboard-driven retry reached the 13-item source folder with one notification, zero Reset notifications, and a responsive window. However, the obsolete System32 load had already enumerated all 4,911 items and delayed the destination load by about 441 ms, so early-cancellation responsiveness remains a follow-up benchmark.
- A 256-item follow-up reduced warm Dispatcher wait from a 415 ms midpoint to about 314 ms and reduced progressive batches from about 40 to 21-22. It also delayed processing of the second address-bar navigation by roughly another 0.5-0.6 seconds, so the larger batch was reverted to preserve input responsiveness.
- A changed working directory now cancels the active folder token before awaiting Git repository detection. The 128-item candidate restored the destination's 13-item snapshot after 287 ms and remained responsive, compared with 465 ms in the preceding 256-item run. The automated second navigation still reached the UI after System32 enumeration had finished, so cancellation before enumeration completion remains unproven rather than being claimed from this run.
- Recent folder snapshots now retain their Git repository path, HEAD, and validity. Returning uses that context immediately while one coalesced background task per path verifies repository identity and HEAD; a changed repository triggers an authoritative refresh and a changed HEAD updates Git UI state.
- The first `src` load spent 728.8 ms detecting its repository. After leaving and returning, the 13-item snapshot appeared in 6.8 ms while the 649.9 ms Git validation ran in the background and confirmed that neither repository identity nor HEAD changed.
- A non-Git five-item system folder initially spent 86.6 ms determining that no repository existed. Returning restored it in 6.1 ms and the 138.9 ms background validation confirmed `repository: false`, showing that negative Git results are also reused without misclassification.
- The coalesced Git-context candidate completed a focused `Debug|x64` build with exit code 0. Final Git and non-Git return runs published their snapshots in 5.8 ms and 5.3 ms respectively, performed no Reset notification or corrective reload, and left the development window responsive.
- Low-priority progressive updates were retested after contiguous `InsertRange` notifications reduced per-batch UI work. The first visible batch remains at normal priority; only later batches yield to navigation and other normal-priority input.
- Two warm 4,911-item System32 runs with low-priority follow-up batches completed in 1,498.9 ms and 1,317.6 ms. Their first batches appeared in 157.6 ms and 27.2 ms, all items were present, and neither run used a Reset notification.
- In an early System32-to-`src` switch, the obsolete load canceled successfully after publishing only 552 items in six batches and twelve notifications, with zero Reset notifications. The destination load started after 31.0 ms, published its 13-item first batch after 41.5 ms, and the window remained responsive. The same timing previously failed to reacquire the address bar while normal-priority follow-up updates were rendering.
- Thumbnail and extended-property cancellation now reaches WinRT operations that previously ignored the per-item token, including font thumbnail retrieval, bitmap decoding, cloud placeholder state, FRN, media properties, and file-type group header thumbnails. Shell COM calls remain non-preemptible once entered, but obsolete work is skipped immediately before the call and no longer performs decoding or UI assignment after it returns.
- A 600-image rapid-navigation run canceled the obsolete load after its first 32-item batch with zero Reset or thumbnail completions. In a separate 300-image run, navigation began after enumeration completed and visible thumbnail work had started; the destination `src` snapshot appeared in 2.2 ms, the address bar succeeded on its first attempt, the window remained responsive, and the obsolete load emitted no thumbnail completion after navigation.
- The cancellation candidate and grouped-header follow-up both completed the focused `Debug|x64` build with exit code 0.
- Grouped progressive updates now reuse the sorted contiguous insertion ranges used by flat views. `BulkConcurrentObservableCollection` groups each incoming range by key and emits one member Add range per touched group instead of one outer and one inner notification per item.
- A 300-item folder containing 100 PNG, 100 text, and 100 log files was refreshed twice while grouped by file type. Both runs displayed all 300 items with zero Reset notifications and 15 recorded collection notifications across four progressive batches; the selected `log-005.log` item remained selected after refresh and the window stayed responsive. The grouped range candidate completed the focused `Debug|x64` build with exit code 0.
- Final projection diffs now count notifications separately from affected items. Contiguous additions and removals use `InsertRange` and `RemoveRange`, while reordering remains bounded to single-item Move notifications. Diffs exceeding 64 notifications, 1,024 affected items, or sharing no item retain the single-Reset fallback.
- Filtering a 300-item folder to 100 matching items and clearing the filter each affected 200 items with one range notification. Flat and file-type-grouped runs completed in approximately 0.48-0.68 seconds, retained the selected `keep-005.png` item, showed the correct 100/300 status counts, and kept the window responsive.
- A 1,200-item control verified the safety bound: filtering to 100 items and restoring all items each affected 1,100 entries, logged the bounded-diff rejection, used one Reset, completed in approximately 0.45 seconds, and left the window responsive. The range-diff candidate completed the focused `Debug|x64` build with exit code 0.
- Before the `StorageFolder` changes, `Shell:RecycleBinFolder` displayed its first 32 of 318 items in 930.9 ms and completed in 3,686.0 ms. Three independent 324-item runs after bounded initialization and Shell-property reuse displayed the first batch in 465.0-713.7 ms and completed in 1,386.1-2,366.1 ms, with zero Reset notifications and responsive windows. Initialization accounted for only 0.2-0.3 ms; provider page retrieval became the dominant measured cost.
- Consolidating virtual-provider follow-up batches reduced the same 324-item Recycle Bin load from five UI batches and 146 collection notifications to two or three batches and 28-43 notifications. A warmed run displayed the first batch in 157.3 ms and completed in 1,174.0 ms; provider waits were 519.6 ms and intermediate UI waits were 14.6 ms, with zero Reset notifications.
- A Recycle Bin rapid-navigation run canceled the obsolete load after publishing 32 items, reported `success: False`, issued zero Reset notifications, and allowed the destination load to complete normally. UI input scheduling remained variable during this stress test, so eliminating all navigation latency is not claimed from this run.
- The bounded `StorageFolder` initialization, Shell-property reuse, consolidated batching, and caller-side provider cancellation changes each completed a focused `Debug|x64` build with exit code 0.
- Starting the next Shell page before publishing the first batch measured 494.6 ms of provider execution but only 283.2 ms of blocking wait, overlapping about 211 ms with UI work. Promoting only the final consolidated batch from Low to Normal priority reduced collection Dispatcher wait from 578.4 ms to 200.0-295.4 ms in three independent 324-item runs; intermediate batches remain Low priority.
- Combining Shell root resolution with its first 32-item page reduced three-run median Recycle Bin first-batch time from 966.1 ms to 491.1 ms and median complete-load time from 1,619.2 ms to 1,065.7 ms. The median provider blocking wait inside `UniversalStorageEnumerator` fell from 296.0 ms to 172.3 ms. All final runs displayed 324 items with two UI batches, 28 collection notifications, zero Reset notifications, and responsive windows.
- `Shell:MyComputerFolder` also completed successfully through the combined request path with 12 items, a 379.8 ms first batch, a 682.0 ms complete load, and zero Reset notifications. A root-query cancellation stress run stopped the obsolete Recycle Bin load in 215.9 ms before publishing any items; the destination displayed its first batch in 21.5 ms and both loads issued zero Reset notifications.
- Before ZIP property reuse, a 700-file archive displayed its first batch in 339.2 ms and completed in 5,509.1 ms. Provider retrieval took only 22.1 ms while 5,067.4 ms remained in enumeration because each file reopened and rescanned the archive for basic properties.
- Reusing the properties already returned with each ZIP entry reduced the same archive's first batch to 52.3 ms and complete load to 935.0 ms. Remaining enumeration fell to 32.1 ms, all 700 items appeared in four range notifications with zero Reset notifications, and the window remained responsive. The remaining 728.1 ms collection Dispatcher wait is intentionally preemptible UI scheduling rather than archive I/O.
- Allowing ZIP follow-up pages to accumulate up to 1,000 items reduced that archive from four range notifications to two. A fresh-process run completed in 828.9 ms with a 135.5 ms first batch and zero Reset notifications; collection Dispatcher wait fell from 728.1 ms to 410.7 ms while the window remained responsive.
- A separate 700-file Shift-JIS archive exercised the SharpZipLib enumeration path. After encoding detection restarted the load, the correctly decoded archive displayed its first batch in 78.7 ms and completed in 519.7 ms with two range notifications and zero Reset notifications. UI Automation confirmed visible Japanese file names were not corrupted.
- FTP enumeration now performs one cancellable `GetListing` and constructs initial item properties from each returned `FtpListItem`, removing the previous additional connection and `GetObjectInfo` call per item. The focused `Debug|x64` build completed with exit code 0; runtime latency and cancellation verification remain pending because no FTP endpoint is configured in this environment.
- Large navigation snapshots now clone display, sorting, grouping, shortcut, and Git identity state while dropping bitmap, preloaded-icon, storage-object, and extended-property references. Visible containers reload enrichment after restoration, and authoritative enumeration reuses unchanged lightweight items to avoid a reconciliation Reset.
- Creating the complete 4,911-item lightweight System32 snapshot took 31.7 ms. Returning restored it and one selected item in 62.6 ms; background Win32 reconciliation enumerated all 4,911 items in 957.6 ms and completed with zero collection notifications and zero Reset updates. The final process remained responsive with a 396.6 MB working set and 221.5 MB private memory.
- Drive capacity and filesystem retrieval no longer runs as an async delegate rooted on the UI Dispatcher after startup timeout coverage ends, and overlapping widget/sidebar refreshes share one in-flight query per drive. A development startup added both local drives and three existing network drives, retained a responsive window while System32 loaded, and completed the focused `Debug|x64` build with exit code 0. No disconnected mapping is available in this environment, so the five-second skip/warning path remains unclaimed.
- The page-overlap, final-priority, combined root/first-page, and root-cancellation candidates each completed a focused `Debug|x64` build with exit code 0.
- Folder-load diagnostics now separate final ordering, projection construction, and UI-thread diff planning. An instrumented 4,911-item System32 control measured 171.3 ms of final ordering, 0.1 ms of projection work, and 3.2 ms of diff planning, proving that the final diff algorithm was not the remaining load-time bottleneck.
- Moving progressive-batch sorting off the UI thread and testing a 512-item follow-up batch reduced three-run median System32 updates from 43 batches and 91 notifications in the immediate 128-item control to 14 batches and 29 notifications. Median collection Dispatcher wait fell from 1,703.1 ms to 1,289.2 ms and median complete-load time was 3,279.1 ms, but a WinSxS cancellation test showed about 527 ms before an address-bar request reached navigation, so 512 was rejected as too aggressive.
- The final 256-item follow-up batch retained the 32-item first batch and the 500 ms slow-enumeration timer. A warmed System32 navigation displayed its first batch in 101.0 ms and all 4,911 items in 2,413.9 ms using 22 batches, 46 notifications, zero Reset updates, and 634.5 ms of Dispatcher wait; the slowest recorded collection mutation was 2.5 ms.
- Validating and adopting the already sorted flat projection reduced final System32 ordering from roughly 198-208 ms in three pre-adoption runs to 7.4-20.5 ms, with `display order reused: True` recorded and all 4,911 items retained. Grouped, filtered, mismatched, or unsorted projections continue through the original full-sort fallback.
- In the final 256-item WinSxS stress run, the address-path cancellation request completed in 183.7 ms. The obsolete load stopped successfully after publishing 288 items in two batches, used zero Reset updates, and the 13-item destination snapshot appeared in 168.7 ms while the window remained responsive.
- A grouped refresh of the 85-item AppTiles development folder completed in 156.3 ms with two batches, three notifications, zero Reset updates, and the deliberately selected `BadgeLogo.scale-100.png` item still selected afterward. The final candidate completed a focused `Debug|x64` build with exit code 0.
- Shell thumbnail diagnostics now report started requests, caller-detached waits, and residual native-call time. The caller awaits STA work with its cancellation token; cancellation returns immediately while any already-entered native call finishes naturally, and the per-tab six-operation thumbnail semaphore is released by the caller.
- The Shell STA cancellation candidate completed focused `Files.App.Storage` and `Files.App` `Debug|x64` builds with exit code 0. A UI Automation run switched folders immediately after the first Shell thumbnail request began: the path update returned in 33.0 ms and the destination snapshot appeared in 3.7 ms with a responsive window. The native request completed before cancellation detached the wait, so that run recorded zero detached waits and is not claimed as residual-work runtime proof.

## Risks

- File-system and Shell data can change between snapshot restoration and reconciliation.
- Incremental collection notifications behave differently across WinUI GridView, ListView, and DataGrid layouts.
- Shell, COM, network, archive, and cloud calls do not all support cooperative cancellation.
- Increasing concurrency can reduce latency but increase disk seeks, Shell contention, memory pressure, and antivirus work.
- Persisted thumbnails must not become stale after a file is replaced or modified.
