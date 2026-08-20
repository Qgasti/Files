// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Storage;

namespace Files.App.Utils.Storage
{
	public static class SortingHelper
	{
		public static Func<ListedItem, object> GetSortFunc(SortOption directorySortOption)
		{
			return directorySortOption switch
			{
				SortOption.Name => item => item.Name,
				SortOption.DateModified => item => item.ItemDateModifiedReal,
				SortOption.DateCreated => item => item.ItemDateCreatedReal,
				SortOption.FileType => item => item.ItemType,
				SortOption.Size => item => item.FileSizeBytes,
				SortOption.SyncStatus => item => item.SyncStatusString,
				SortOption.FileTag => item => item.FileTags?.FirstOrDefault(),
				SortOption.Path => item => item.ItemPath,
				SortOption.OriginalFolder => item => (item as RecycleBinItem)?.ItemOriginalFolder,
				SortOption.DateDeleted => item => (item as RecycleBinItem)?.ItemDateDeletedReal,
				_ => item => item.Name,
			};
		}

		public static IComparer<ListedItem> GetComparer(SortOption directorySortOption, SortDirection directorySortDirection,
			bool sortDirectoriesAlongsideFiles, bool sortFilesFirst)
			=> new ListedItemComparer(directorySortOption, directorySortDirection, sortDirectoriesAlongsideFiles, sortFilesFirst);

		public static IEnumerable<ListedItem> OrderFileList(IList<ListedItem> filesAndFolders, SortOption directorySortOption, SortDirection directorySortDirection,
			bool sortDirectoriesAlongsideFiles, bool sortFilesFirst)
			=> filesAndFolders.OrderBy(item => item, GetComparer(
				directorySortOption,
				directorySortDirection,
				sortDirectoriesAlongsideFiles,
				sortFilesFirst));

		private sealed class ListedItemComparer : IComparer<ListedItem>
		{
			private readonly SortOption directorySortOption;
			private readonly SortDirection directorySortDirection;
			private readonly bool sortDirectoriesAlongsideFiles;
			private readonly bool sortFilesFirst;
			private readonly Func<ListedItem, object> orderFunc;
			private readonly IComparer<object> naturalStringComparer;

			public ListedItemComparer(SortOption directorySortOption, SortDirection directorySortDirection,
				bool sortDirectoriesAlongsideFiles, bool sortFilesFirst)
			{
				this.directorySortOption = directorySortOption;
				this.directorySortDirection = directorySortDirection;
				this.sortDirectoriesAlongsideFiles = sortDirectoriesAlongsideFiles;
				this.sortFilesFirst = sortFilesFirst;
				orderFunc = GetSortFunc(directorySortOption);
				naturalStringComparer = NaturalStringComparer.GetForProcessor();
			}

			public int Compare(ListedItem? x, ListedItem? y)
			{
				if (ReferenceEquals(x, y))
					return 0;
				if (x is null)
					return -1;
				if (y is null)
					return 1;

				if (!sortDirectoriesAlongsideFiles)
				{
					var priorityComparison = PrioritizeFilesOrFolders(x).CompareTo(PrioritizeFilesOrFolders(y));
					if (priorityComparison != 0)
						return priorityComparison;
				}

				var xKey = orderFunc(x);
				var yKey = orderFunc(y);
				if (directorySortOption == SortOption.FileTag)
				{
					var emptyTagComparison = string.IsNullOrEmpty(xKey as string).CompareTo(string.IsNullOrEmpty(yKey as string));
					if (emptyTagComparison != 0)
						return emptyTagComparison;
				}

				var primaryComparison = CompareKeys(xKey, yKey, directorySortOption == SortOption.Name);
				if (primaryComparison != 0)
					return primaryComparison;

				return directorySortOption == SortOption.Name
					? 0
					: CompareKeys(x.Name, y.Name, useNaturalStringComparison: true);
			}

			private bool PrioritizeFilesOrFolders(ListedItem item)
				=> (item.PrimaryItemAttribute == StorageItemTypes.File || item.IsShortcut || item.IsArchive) ^ sortFilesFirst;

			private int CompareKeys(object? x, object? y, bool useNaturalStringComparison)
			{
				var comparer = useNaturalStringComparison ? naturalStringComparer : Comparer<object>.Default;
				return directorySortDirection == SortDirection.Ascending
					? comparer.Compare(x!, y!)
					: comparer.Compare(y!, x!);
			}
		}
	}
}
