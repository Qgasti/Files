// Copyright (c) Files Community
// Licensed under the MIT License.

using System.IO;

namespace Files.App.ViewModels
{
	public sealed partial class ShellViewModel
	{
		private const int MaxFolderNavigationSnapshots = 4;
		private const int MaxItemsPerFolderNavigationSnapshot = 2_000;
		private const int MaxTotalFolderNavigationSnapshotItems = 4_000;
		private static readonly TimeSpan FolderNavigationSnapshotLifetime = TimeSpan.FromMinutes(2);

		private readonly Dictionary<string, FolderNavigationSnapshot> folderNavigationSnapshots = new(StringComparer.OrdinalIgnoreCase);
		private readonly object folderNavigationSnapshotsLock = new();

		private void CaptureFolderNavigationSnapshot(string? path)
		{
			if (!IsFolderNavigationSnapshotEligible(path))
				return;

			var snapshotKey = GetFolderNavigationSnapshotKey(path);
			var items = FilesAndFolders.ToList();
			if (items.Count > MaxItemsPerFolderNavigationSnapshot)
			{
				RemoveFolderNavigationSnapshot(snapshotKey);
				return;
			}

			var now = DateTimeOffset.UtcNow;
			var selectedPaths = GetSelectedItemPathsForSnapshot(path);
			var snapshot = new FolderNavigationSnapshot(items, selectedPaths, CreateFolderNavigationSnapshotSignature(), now, now);

			lock (folderNavigationSnapshotsLock)
			{
				PruneExpiredFolderNavigationSnapshots(now);
				folderNavigationSnapshots[snapshotKey] = snapshot;
				TrimFolderNavigationSnapshots(snapshotKey);
			}
		}

		private bool TryRestoreFolderNavigationSnapshot(string path, out IReadOnlyList<ListedItem> items, out IReadOnlySet<string> selectedPaths, out TimeSpan age)
		{
			items = [];
			selectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			age = TimeSpan.Zero;

			if (!IsFolderNavigationSnapshotEligible(path))
				return false;

			var snapshotKey = GetFolderNavigationSnapshotKey(path);
			var now = DateTimeOffset.UtcNow;
			lock (folderNavigationSnapshotsLock)
			{
				PruneExpiredFolderNavigationSnapshots(now);
				if (!folderNavigationSnapshots.TryGetValue(snapshotKey, out var snapshot) ||
					snapshot.Signature != CreateFolderNavigationSnapshotSignature())
				{
					folderNavigationSnapshots.Remove(snapshotKey);
					return false;
				}

				folderNavigationSnapshots[snapshotKey] = snapshot with { LastAccessed = now };
				items = snapshot.Items;
				selectedPaths = snapshot.SelectedPaths;
				age = now - snapshot.CapturedAt;
				return true;
			}
		}

		public void UpdateFolderNavigationSnapshotSelection(IReadOnlyList<ListedItem> selectedItems)
		{
			var path = WorkingDirectory;
			if (!IsFolderNavigationSnapshotEligible(path))
				return;

			var snapshotKey = GetFolderNavigationSnapshotKey(path);
			var selectedPaths = selectedItems
				.Select(item => item.ItemPath)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			lock (folderNavigationSnapshotsLock)
			{
				if (folderNavigationSnapshots.TryGetValue(snapshotKey, out var snapshot))
					folderNavigationSnapshots[snapshotKey] = snapshot with { SelectedPaths = selectedPaths };
			}
		}

		private IReadOnlySet<string> GetSelectedItemPathsForSnapshot(string path)
		{
			if (!path.Equals(WorkingDirectory, StringComparison.OrdinalIgnoreCase) ||
				!ReferenceEquals(ContentPageContext.ShellPage?.ShellViewModel, this))
			{
				return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			}

			return ContentPageContext.SelectedItems
				.Select(item => item.ItemPath)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
		}

		private void ReuseUnchangedSnapshotItems(IReadOnlyDictionary<string, ListedItem>? snapshotItemsByPath)
		{
			if (snapshotItemsByPath is null || snapshotItemsByPath.Count == 0)
				return;

			filesAndFolders = new ConcurrentCollection<ListedItem>(filesAndFolders.Select(item =>
				snapshotItemsByPath.TryGetValue(item.ItemPath, out var snapshotItem) && CanReuseSnapshotItem(snapshotItem, item)
					? snapshotItem
					: item));
		}

		private static bool CanReuseSnapshotItem(ListedItem snapshotItem, ListedItem currentItem)
			=> snapshotItem.GetType() == currentItem.GetType() &&
				snapshotItem.PrimaryItemAttribute == currentItem.PrimaryItemAttribute &&
				snapshotItem.ItemNameRaw == currentItem.ItemNameRaw &&
				snapshotItem.FileExtension == currentItem.FileExtension &&
				snapshotItem.ItemDateModifiedReal == currentItem.ItemDateModifiedReal &&
				snapshotItem.FileSizeBytes == currentItem.FileSizeBytes &&
				snapshotItem.IsHiddenItem == currentItem.IsHiddenItem;

		private void RemoveFolderNavigationSnapshot(string? path)
		{
			if (string.IsNullOrEmpty(path))
				return;

			lock (folderNavigationSnapshotsLock)
				folderNavigationSnapshots.Remove(GetFolderNavigationSnapshotKey(path));
		}

		private void InvalidateFolderNavigationSnapshotForItem(string itemPath)
		{
			var parentPath = Path.GetDirectoryName(itemPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			RemoveFolderNavigationSnapshot(parentPath);
		}

		private void ClearFolderNavigationSnapshots()
		{
			lock (folderNavigationSnapshotsLock)
				folderNavigationSnapshots.Clear();
		}

		private FolderNavigationSnapshotSignature CreateFolderNavigationSnapshotSignature()
			=> new(
				folderSettings.LayoutMode,
				folderSettings.DirectorySortOption,
				folderSettings.DirectorySortDirection,
				folderSettings.DirectoryGroupOption,
				folderSettings.DirectoryGroupDirection,
				FilesAndFoldersFilter,
				UserSettingsService.FoldersSettingsService.ShowHiddenItems,
				UserSettingsService.FoldersSettingsService.ShowProtectedSystemFiles,
				UserSettingsService.FoldersSettingsService.ShowDotFiles,
				UserSettingsService.FoldersSettingsService.AreAlternateStreamsVisible,
				UserSettingsService.FoldersSettingsService.CalculateFolderSizes);

		private static bool IsFolderNavigationSnapshotEligible(string? path)
			=> !string.IsNullOrWhiteSpace(path) &&
				path is not "Home" and not "ReleaseNotes" and not "Settings" &&
				!path.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) &&
				!path.StartsWith(Constants.UserEnvironmentPaths.RecycleBinPath, StringComparison.Ordinal) &&
				!path.EndsWith(ShellLibraryItem.EXTENSION, StringComparison.OrdinalIgnoreCase) &&
				!ZipStorageFolder.IsZipPath(path);

		private static string GetFolderNavigationSnapshotKey(string path)
			=> Path.TrimEndingDirectorySeparator(path);

		private void PruneExpiredFolderNavigationSnapshots(DateTimeOffset now)
		{
			foreach (var path in folderNavigationSnapshots
				.Where(entry => now - entry.Value.CapturedAt > FolderNavigationSnapshotLifetime)
				.Select(entry => entry.Key)
				.ToArray())
			{
				folderNavigationSnapshots.Remove(path);
			}
		}

		private void TrimFolderNavigationSnapshots(string mostRecentPath)
		{
			while (folderNavigationSnapshots.Count > MaxFolderNavigationSnapshots ||
				folderNavigationSnapshots.Values.Sum(snapshot => snapshot.Items.Count) > MaxTotalFolderNavigationSnapshotItems)
			{
				var oldest = folderNavigationSnapshots
					.Where(entry => !entry.Key.Equals(mostRecentPath, StringComparison.OrdinalIgnoreCase))
					.MinBy(entry => entry.Value.LastAccessed);

				if (string.IsNullOrEmpty(oldest.Key))
				{
					folderNavigationSnapshots.Remove(mostRecentPath);
					return;
				}

				folderNavigationSnapshots.Remove(oldest.Key);
			}
		}

		private sealed record FolderNavigationSnapshot(
			IReadOnlyList<ListedItem> Items,
			IReadOnlySet<string> SelectedPaths,
			FolderNavigationSnapshotSignature Signature,
			DateTimeOffset CapturedAt,
			DateTimeOffset LastAccessed);

		private readonly record struct FolderNavigationSnapshotSignature(
			FolderLayoutModes LayoutMode,
			SortOption SortOption,
			SortDirection SortDirection,
			GroupOption GroupOption,
			SortDirection GroupDirection,
			string? Filter,
			bool ShowHiddenItems,
			bool ShowProtectedSystemFiles,
			bool ShowDotFiles,
			bool ShowAlternateDataStreams,
			bool CalculateFolderSizes);
	}
}
