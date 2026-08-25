// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Storage;

namespace Files.App.ViewModels
{
	public sealed partial class ShellViewModel
	{
		private const int MaxDifferentialCollectionOperations = 64;
		private const int MaxDifferentialCollectionAffectedItems = 1024;

		private sealed class ProgressiveCollectionUpdateCoalescer(
			ShellViewModel owner,
			FolderLoadPerformanceMetrics? loadMetrics,
			CancellationToken cancellationToken)
		{
			private readonly object syncRoot = new();
			private List<ListedItem> pendingItems = [];
			private Task processingTask = Task.CompletedTask;
			private bool isProcessing;

			public void Enqueue(IReadOnlyList<ListedItem> items)
			{
				if (items.Count == 0 || cancellationToken.IsCancellationRequested)
					return;

				lock (syncRoot)
				{
					pendingItems.AddRange(items);
					if (isProcessing)
						return;

					isProcessing = true;
					processingTask = ProcessAsync();
				}
			}

			public Task FlushAsync()
			{
				lock (syncRoot)
					return processingTask;
			}

			private async Task ProcessAsync()
			{
				while (true)
				{
					List<ListedItem> items;
					lock (syncRoot)
					{
						if (cancellationToken.IsCancellationRequested)
						{
							pendingItems.Clear();
							isProcessing = false;
							return;
						}

						if (pendingItems.Count == 0)
						{
							isProcessing = false;
							return;
						}

						items = pendingItems;
						pendingItems = [];
					}

					await owner.AppendFilesAndFoldersAsync(items, loadMetrics, cancellationToken);
				}
			}
		}

		private static List<SortedInsertionRange> CreateSortedInsertionRanges(
			IReadOnlyList<ListedItem> currentItems,
			IReadOnlyList<ListedItem> newItems,
			IComparer<ListedItem> comparer)
		{
			var ranges = new List<SortedInsertionRange>();
			var currentIndex = 0;
			var newIndex = 0;
			var targetIndex = 0;

			while (newIndex < newItems.Count)
			{
				while (currentIndex < currentItems.Count && comparer.Compare(currentItems[currentIndex], newItems[newIndex]) <= 0)
				{
					currentIndex++;
					targetIndex++;
				}

				var rangeIndex = targetIndex;
				var rangeItems = new List<ListedItem>();
				while (newIndex < newItems.Count &&
					(currentIndex >= currentItems.Count || comparer.Compare(currentItems[currentIndex], newItems[newIndex]) > 0))
				{
					rangeItems.Add(newItems[newIndex]);
					newIndex++;
					targetIndex++;
				}

				if (rangeItems.Count > 0)
					ranges.Add(new SortedInsertionRange(rangeIndex, rangeItems));
			}

			return ranges;
		}

		private bool TryApplyFlatCollectionDiff(
			IReadOnlyList<ListedItem> targetItems,
			out int operationCount,
			out int affectedItemCount,
			out long planningElapsedTicks)
		{
			var planningStartedTimestamp = Stopwatch.GetTimestamp();
			var currentItems = FilesAndFolders.ToList();
			if (!TryCreateFlatCollectionDiff(currentItems, targetItems, out var operations, out affectedItemCount))
			{
				planningElapsedTicks = Stopwatch.GetTimestamp() - planningStartedTimestamp;
				operationCount = operations.Count;
				return false;
			}
			planningElapsedTicks = Stopwatch.GetTimestamp() - planningStartedTimestamp;

			foreach (var operation in operations)
			{
				switch (operation.Kind)
				{
					case FlatCollectionOperationKind.InsertRange:
						if (operation.Items.Count == 1)
							FilesAndFolders.Insert(operation.Index, operation.Items[0]);
						else
							FilesAndFolders.InsertRange(operation.Index, operation.Items);
						break;

					case FlatCollectionOperationKind.RemoveRange:
						if (operation.Items.Count == 1)
							FilesAndFolders.RemoveAt(operation.Index);
						else
							FilesAndFolders.RemoveRange(operation.Index, operation.Items.Count);
						break;

					case FlatCollectionOperationKind.Move:
						FilesAndFolders.Move(operation.OldIndex, operation.Index);
						break;
				}
			}

			operationCount = operations.Count;
			return true;
		}

		private void OrderGroupsWithMoves()
		{
			var groups = FilesAndFolders.GroupedCollection;
			if (groups is null)
				return;

			foreach (var group in groups.Where(group => !group.IsSorted).ToList())
			{
				var orderedItems = SortingHelper.OrderFileList(
					group.ToList(),
					folderSettings.DirectorySortOption,
					folderSettings.DirectorySortDirection,
					folderSettings.SortDirectoriesAlongsideFiles,
					folderSettings.SortFilesFirst).ToList();
				ApplyOrderWithBoundedMoves(group, orderedItems);
				group.IsSorted = true;
			}

			if (groups.IsSorted)
				return;

			IEnumerable<GroupedCollection<ListedItem>> orderedGroups;
			if (folderSettings.DirectoryGroupDirection == SortDirection.Ascending)
			{
				orderedGroups = folderSettings.DirectoryGroupOption == GroupOption.Size
					? groups.OrderBy(group => group.First().PrimaryItemAttribute != StorageItemTypes.Folder || group.First().IsArchive)
						.ThenBy(group => group.Model.SortIndexOverride)
						.ThenBy(group => group.Model.Text)
					: groups.OrderBy(group => group.Model.SortIndexOverride).ThenBy(group => group.Model.Text);
			}
			else
			{
				orderedGroups = folderSettings.DirectoryGroupOption == GroupOption.Size
					? groups.OrderBy(group => group.First().PrimaryItemAttribute != StorageItemTypes.Folder || group.First().IsArchive)
						.ThenByDescending(group => group.Model.SortIndexOverride)
						.ThenByDescending(group => group.Model.Text)
					: groups.OrderByDescending(group => group.Model.SortIndexOverride).ThenByDescending(group => group.Model.Text);
			}

			ApplyOrderWithBoundedMoves(groups, orderedGroups.ToList());
			groups.IsSorted = true;
		}

		private static void ApplyOrderWithBoundedMoves<T>(BulkConcurrentObservableCollection<T> collection, IReadOnlyList<T> orderedItems)
			where T : class
		{
			if (TryCreateCollectionMoves(collection.ToList(), orderedItems, out var moves))
			{
				foreach (var move in moves)
					collection.Move(move.OldIndex, move.NewIndex);
				return;
			}

			collection.BeginBulkOperation();
			try
			{
				collection.Clear();
				collection.AddRange(orderedItems);
			}
			finally
			{
				collection.EndBulkOperation();
			}
		}

		private static bool TryCreateCollectionMoves<T>(IReadOnlyList<T> currentItems, IReadOnlyList<T> orderedItems, out List<(int OldIndex, int NewIndex)> moves)
			where T : class
		{
			moves = [];
			if (currentItems.Count != orderedItems.Count)
				return false;

			var workingItems = currentItems.ToList();
			for (var targetIndex = 0; targetIndex < orderedItems.Count; targetIndex++)
			{
				if (ReferenceEquals(workingItems[targetIndex], orderedItems[targetIndex]))
					continue;

				var currentIndex = workingItems.FindIndex(targetIndex, item => ReferenceEquals(item, orderedItems[targetIndex]));
				if (currentIndex < 0)
					return false;

				moves.Add((currentIndex, targetIndex));
				if (moves.Count > MaxDifferentialCollectionOperations)
					return false;

				var item = workingItems[currentIndex];
				workingItems.RemoveAt(currentIndex);
				workingItems.Insert(targetIndex, item);
			}

			return true;
		}

		private static bool TryCreateFlatCollectionDiff(
			IReadOnlyList<ListedItem> currentItems,
			IReadOnlyList<ListedItem> targetItems,
			out List<FlatCollectionOperation> operations,
			out int affectedItemCount)
		{
			operations = [];
			affectedItemCount = 0;
			var workingItems = currentItems.ToList();
			var targetSet = new HashSet<ListedItem>(targetItems, ReferenceEqualityComparer.Instance);
			if (currentItems.Count > 0 && targetItems.Count > 0 && !currentItems.Any(targetSet.Contains))
				return false;

			for (var index = workingItems.Count - 1; index >= 0; index--)
			{
				if (targetSet.Contains(workingItems[index]))
					continue;

				var rangeEnd = index;
				while (index >= 0 && !targetSet.Contains(workingItems[index]))
					index--;

				var rangeStart = index + 1;
				var rangeItems = workingItems.GetRange(rangeStart, rangeEnd - rangeStart + 1);
				operations.Add(new(FlatCollectionOperationKind.RemoveRange, rangeStart, -1, rangeItems));
				workingItems.RemoveRange(rangeStart, rangeItems.Count);
				affectedItemCount += rangeItems.Count;
				if (operations.Count > MaxDifferentialCollectionOperations || affectedItemCount > MaxDifferentialCollectionAffectedItems)
					return false;
			}

			for (var targetIndex = 0; targetIndex < targetItems.Count; targetIndex++)
			{
				var targetItem = targetItems[targetIndex];
				if (targetIndex < workingItems.Count && ReferenceEquals(workingItems[targetIndex], targetItem))
					continue;

				var existingIndex = targetIndex < workingItems.Count
					? workingItems.FindIndex(targetIndex, item => ReferenceEquals(item, targetItem))
					: -1;
				if (existingIndex >= 0)
				{
					operations.Add(new(FlatCollectionOperationKind.Move, targetIndex, existingIndex, [targetItem]));
					workingItems.RemoveAt(existingIndex);
					workingItems.Insert(targetIndex, targetItem);
					affectedItemCount++;
				}
				else
				{
					var rangeItems = new List<ListedItem>();
					while (targetIndex + rangeItems.Count < targetItems.Count)
					{
						var candidate = targetItems[targetIndex + rangeItems.Count];
						if (workingItems.FindIndex(targetIndex, item => ReferenceEquals(item, candidate)) >= 0)
							break;

						rangeItems.Add(candidate);
					}

					operations.Add(new(FlatCollectionOperationKind.InsertRange, targetIndex, -1, rangeItems));
					workingItems.InsertRange(targetIndex, rangeItems);
					affectedItemCount += rangeItems.Count;
					targetIndex += rangeItems.Count - 1;
				}

				if (operations.Count > MaxDifferentialCollectionOperations || affectedItemCount > MaxDifferentialCollectionAffectedItems)
					return false;
			}

			return true;
		}

		private enum FlatCollectionOperationKind
		{
			InsertRange,
			RemoveRange,
			Move
		}

		private readonly record struct FlatCollectionOperation(
			FlatCollectionOperationKind Kind,
			int Index,
			int OldIndex,
			IReadOnlyList<ListedItem> Items);

		private readonly record struct SortedInsertionRange(
			int Index,
			IReadOnlyList<ListedItem> Items);
	}
}
