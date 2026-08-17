// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Files.App.ViewModels
{
	public sealed partial class ShellViewModel
	{
		private static long folderLoadSequence;
		private FolderLoadPerformanceMetrics? activeFolderLoadMetrics;

		private sealed class FolderLoadPerformanceMetrics
		{
			private readonly long startedTimestamp = Stopwatch.GetTimestamp();
			private long firstBatchTimestamp;
			private long thumbnailElapsedTicks;
			private long thumbnailMaxElapsedTicks;
			private int thumbnailCount;
			private int thumbnailSuccessCount;
			private int persistentThumbnailHitCount;
			private long generatedThumbnailElapsedTicks;
			private long generatedThumbnailMaxElapsedTicks;
			private int generatedThumbnailCount;
			private int generatedThumbnailSuccessCount;
			private int incrementalUpdateCount;
			private int incrementalItemCount;
			private int resetUpdateCount;

			public long LoadId { get; }

			public string CorrelationId { get; }

			public string PathIdentifier { get; }

			public FolderLoadPerformanceMetrics(long loadId, string path)
			{
				LoadId = loadId;
				CorrelationId = $"{Environment.ProcessId}:{loadId}";
				PathIdentifier = LogPathHelper.GetPathIdentifier(path);
			}

			public void RecordStarted()
			{
				App.Logger.LogInformation(
					"Folder load {CorrelationId} started for {Path} after {QueueWaitMs:F1} ms.",
					CorrelationId,
					PathIdentifier,
					GetElapsedMilliseconds(startedTimestamp));
			}

			public void RecordFirstBatch(int displayedItemCount)
			{
				var timestamp = Stopwatch.GetTimestamp();
				if (Interlocked.CompareExchange(ref firstBatchTimestamp, timestamp, 0) != 0)
					return;

				App.Logger.LogInformation(
					"Folder load {CorrelationId} published its first UI batch with {ItemCount} items after {ElapsedMs:F1} ms.",
					CorrelationId,
					displayedItemCount,
					GetElapsedMilliseconds(startedTimestamp, timestamp));
			}

			public void RecordEnumeration(string path, int result, int itemCount, long enumerationStartedTimestamp)
			{
				App.Logger.LogInformation(
					"Folder load {CorrelationId} enumerated {ItemCount} items from {Path} using {Enumerator} in {ElapsedMs:F1} ms.",
					CorrelationId,
					itemCount,
					LogPathHelper.GetPathIdentifier(path),
					GetEnumeratorName(result),
					GetElapsedMilliseconds(enumerationStartedTimestamp));
			}

			public void RecordSnapshotRestored(int itemCount, int selectedItemCount, TimeSpan age)
			{
				RecordFirstBatch(itemCount);
				App.Logger.LogInformation(
					"Folder load {CorrelationId} restored {ItemCount} items ({SelectedItemCount} selected) from a navigation snapshot aged {SnapshotAgeMs:F1} ms; background reconciliation is continuing.",
					CorrelationId,
					itemCount,
					selectedItemCount,
					age.TotalMilliseconds);
			}

			public void RecordThumbnail(long thumbnailStartedTimestamp, bool succeeded, bool generated, bool persistentCacheHit = false)
			{
				var elapsedTicks = Stopwatch.GetTimestamp() - thumbnailStartedTimestamp;
				var count = generated
					? Interlocked.Increment(ref generatedThumbnailCount)
					: Interlocked.Increment(ref thumbnailCount);

				if (generated)
				{
					Interlocked.Add(ref generatedThumbnailElapsedTicks, elapsedTicks);
					UpdateMaximum(ref generatedThumbnailMaxElapsedTicks, elapsedTicks);
					if (succeeded)
						Interlocked.Increment(ref generatedThumbnailSuccessCount);
				}
				else
				{
					Interlocked.Add(ref thumbnailElapsedTicks, elapsedTicks);
					UpdateMaximum(ref thumbnailMaxElapsedTicks, elapsedTicks);
					if (succeeded)
						Interlocked.Increment(ref thumbnailSuccessCount);
					if (persistentCacheHit)
						Interlocked.Increment(ref persistentThumbnailHitCount);
				}

				if (count == 1)
				{
					App.Logger.LogInformation(
						"Folder load {CorrelationId} completed its first {ThumbnailKind} thumbnail request in {ElapsedMs:F1} ms (success: {Succeeded}).",
						CorrelationId,
						generated ? "generated" : "cached/icon",
						TicksToMilliseconds(elapsedTicks),
						succeeded);
				}
			}

			public void RecordCollectionUpdate(bool incremental, int itemCount)
			{
				if (incremental)
				{
					Interlocked.Increment(ref incrementalUpdateCount);
					Interlocked.Add(ref incrementalItemCount, itemCount);
				}
				else
				{
					Interlocked.Increment(ref resetUpdateCount);
				}
			}

			public void RecordCompleted(int itemCount, bool succeeded)
			{
				var firstBatch = Volatile.Read(ref firstBatchTimestamp);
				var initialCount = Volatile.Read(ref thumbnailCount);
				var generatedCount = Volatile.Read(ref generatedThumbnailCount);

				App.Logger.LogInformation(
					"Folder load {CorrelationId} completed for {Path} in {ElapsedMs:F1} ms (success: {Succeeded}, items: {ItemCount}, first UI batch: {FirstBatchMs:F1} ms, incremental UI updates/affected items: {IncrementalUpdateCount}/{IncrementalItemCount}, reset UI updates: {ResetUpdateCount}, cached/icon thumbnails: {ThumbnailSuccessCount}/{ThumbnailCount} ({PersistentThumbnailHitCount} persistent), avg/max: {ThumbnailAverageMs:F1}/{ThumbnailMaxMs:F1} ms, generated thumbnails: {GeneratedSuccessCount}/{GeneratedCount}, avg/max: {GeneratedAverageMs:F1}/{GeneratedMaxMs:F1} ms).",
					CorrelationId,
					PathIdentifier,
					GetElapsedMilliseconds(startedTimestamp),
					succeeded,
					itemCount,
					firstBatch == 0 ? -1d : GetElapsedMilliseconds(startedTimestamp, firstBatch),
					Volatile.Read(ref incrementalUpdateCount),
					Volatile.Read(ref incrementalItemCount),
					Volatile.Read(ref resetUpdateCount),
					Volatile.Read(ref thumbnailSuccessCount),
					initialCount,
					Volatile.Read(ref persistentThumbnailHitCount),
					GetAverageMilliseconds(Volatile.Read(ref thumbnailElapsedTicks), initialCount),
					TicksToMilliseconds(Volatile.Read(ref thumbnailMaxElapsedTicks)),
					Volatile.Read(ref generatedThumbnailSuccessCount),
					generatedCount,
					GetAverageMilliseconds(Volatile.Read(ref generatedThumbnailElapsedTicks), generatedCount),
					TicksToMilliseconds(Volatile.Read(ref generatedThumbnailMaxElapsedTicks)));
			}

			private static string GetEnumeratorName(int result) => result switch
			{
				0 => "Win32",
				1 => "StorageFolder",
				2 => "StorageFolderWithWin32Watcher",
				_ => "Failed"
			};

			private static double GetElapsedMilliseconds(long startedTimestamp)
				=> GetElapsedMilliseconds(startedTimestamp, Stopwatch.GetTimestamp());

			private static double GetElapsedMilliseconds(long startedTimestamp, long completedTimestamp)
				=> TicksToMilliseconds(completedTimestamp - startedTimestamp);

			private static double GetAverageMilliseconds(long elapsedTicks, int count)
				=> count == 0 ? 0d : TicksToMilliseconds(elapsedTicks) / count;

			private static double TicksToMilliseconds(long elapsedTicks)
				=> elapsedTicks * 1000d / Stopwatch.Frequency;

			private static void UpdateMaximum(ref long target, long value)
			{
				var current = Volatile.Read(ref target);
				while (value > current)
				{
					var previous = Interlocked.CompareExchange(ref target, value, current);
					if (previous == current)
						return;

					current = previous;
				}
			}
		}
	}
}
