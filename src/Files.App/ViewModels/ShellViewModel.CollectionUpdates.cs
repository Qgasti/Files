// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Storage;

namespace Files.App.ViewModels
{
	public sealed partial class ShellViewModel
	{
		private const int MaxDifferentialCollectionOperations = 64;

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

			foreach (var group in groups.ToList())
			{
				var orderedItems = SortingHelper.OrderFileList(
					group.ToList(),
					folderSettings.DirectorySortOption,
					folderSettings.DirectorySortDirection,
					folderSettings.SortDirectoriesAlongsideFiles,
					folderSettings.SortFilesFirst).ToList();
				MoveItemsToOrder(group, orderedItems);
				group.IsSorted = true;
			}

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

			MoveItemsToOrder(groups, orderedGroups.ToList());
			groups.IsSorted = true;
		}

		private static void MoveItemsToOrder<T>(BulkConcurrentObservableCollection<T> collection, IReadOnlyList<T> orderedItems)
		{
			for (var targetIndex = 0; targetIndex < orderedItems.Count; targetIndex++)
			{
				if (ReferenceEquals(collection[targetIndex], orderedItems[targetIndex]))
					continue;

				var currentItems = collection.ToList();
				var currentIndex = currentItems.FindIndex(targetIndex, item => ReferenceEquals(item, orderedItems[targetIndex]));
				if (currentIndex >= 0)
					collection.Move(currentIndex, targetIndex);
			}
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
