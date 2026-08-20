// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Storage;

namespace Files.App.ViewModels
{
	public sealed partial class ShellViewModel
	{
		private const int MaxDifferentialCollectionOperations = 64;

		private static int FindSortedInsertionIndex(IList<ListedItem> items, ListedItem item, IComparer<ListedItem> comparer)
		{
			var lowerBound = 0;
			var upperBound = items.Count;
			while (lowerBound < upperBound)
			{
				var middle = lowerBound + ((upperBound - lowerBound) / 2);
				if (comparer.Compare(items[middle], item) <= 0)
					lowerBound = middle + 1;
				else
					upperBound = middle;
			}

			return lowerBound;
		}

		private bool TryApplyFlatCollectionDiff(IReadOnlyList<ListedItem> targetItems, out int operationCount)
		{
			var currentItems = FilesAndFolders.ToList();
			if (!TryCreateFlatCollectionDiff(currentItems, targetItems, out var operations))
			{
				operationCount = 0;
				return false;
			}

			foreach (var operation in operations)
			{
				switch (operation.Kind)
				{
					case FlatCollectionOperationKind.Insert:
						FilesAndFolders.Insert(operation.Index, operation.Item);
						break;

					case FlatCollectionOperationKind.Remove:
						FilesAndFolders.RemoveAt(operation.Index);
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
			out List<FlatCollectionOperation> operations)
		{
			operations = [];
			var workingItems = currentItems.ToList();
			var targetSet = new HashSet<ListedItem>(targetItems, ReferenceEqualityComparer.Instance);
			if (currentItems.Count > 0 && targetItems.Count > 0 && !currentItems.Any(targetSet.Contains))
				return false;

			for (var index = workingItems.Count - 1; index >= 0; index--)
			{
				if (targetSet.Contains(workingItems[index]))
					continue;

				operations.Add(new(FlatCollectionOperationKind.Remove, index, -1, workingItems[index]));
				workingItems.RemoveAt(index);
				if (operations.Count > MaxDifferentialCollectionOperations)
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
					operations.Add(new(FlatCollectionOperationKind.Move, targetIndex, existingIndex, targetItem));
					workingItems.RemoveAt(existingIndex);
					workingItems.Insert(targetIndex, targetItem);
				}
				else
				{
					operations.Add(new(FlatCollectionOperationKind.Insert, targetIndex, -1, targetItem));
					workingItems.Insert(targetIndex, targetItem);
				}

				if (operations.Count > MaxDifferentialCollectionOperations)
					return false;
			}

			return true;
		}

		private enum FlatCollectionOperationKind
		{
			Insert,
			Remove,
			Move
		}

		private readonly record struct FlatCollectionOperation(
			FlatCollectionOperationKind Kind,
			int Index,
			int OldIndex,
			ListedItem Item);
	}
}
