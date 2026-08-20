// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.SizeProvider;
using Files.Shared.Helpers;
using System.IO;
using Windows.Storage;
using FileAttributes = System.IO.FileAttributes;

namespace Files.App.Utils.Storage
{
	public static class Win32StorageEnumerator
	{
		public readonly record struct PerformanceTimings(
			int ItemCount,
			long TotalElapsedTicks,
			long ItemInitializationWaitTicks,
			long PostProcessingTicks,
			long IntermediateUpdateWaitTicks);

		private const int MaxConcurrentItemInitializations = 8;
		private const int MaxIntermediateBatchSize = 128;

		private static readonly ISizeProvider folderSizeProvider = Ioc.Default.GetService<ISizeProvider>();
		private static readonly IStorageCacheService fileListCache = Ioc.Default.GetRequiredService<IStorageCacheService>();

		private static readonly string folderTypeTextLocalized = Strings.Folder.GetLocalizedResource();

		public static async Task<List<ListedItem>> ListEntries(
			string path,
			IntPtr hFile,
			Win32PInvoke.WIN32_FIND_DATA findData,
			CancellationToken cancellationToken,
			int countLimit,
			bool isGitRepo,
			Func<List<ListedItem>, Task> intermediateAction,
			Action<PerformanceTimings>? performanceCallback = null
		)
		{
			var startedTimestamp = Stopwatch.GetTimestamp();
			long itemInitializationWaitTicks = 0;
			long postProcessingTicks = 0;
			long intermediateUpdateWaitTicks = 0;
			var sampler = new IntervalSampler(500);
			var tempList = new List<ListedItem>();
			var pendingItems = new List<Task<ListedItem>>(MaxConcurrentItemInitializations);
			var count = 0;
			var firstBatchPublished = false;

			IUserSettingsService userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
			bool calculateFolderSizes = userSettingsService.FoldersSettingsService.CalculateFolderSizes;
			bool showHiddenItems = userSettingsService.FoldersSettingsService.ShowHiddenItems;
			bool showProtectedSystemFiles = userSettingsService.FoldersSettingsService.ShowProtectedSystemFiles;
			bool showDotFiles = userSettingsService.FoldersSettingsService.ShowDotFiles;
			bool areAlternateStreamsVisible = userSettingsService.FoldersSettingsService.AreAlternateStreamsVisible;

			try
			{
				do
				{
					cancellationToken.ThrowIfCancellationRequested();
					var attributes = (FileAttributes)findData.dwFileAttributes;
					var isSystem = (attributes & FileAttributes.System) == FileAttributes.System;
					var isHidden = (attributes & FileAttributes.Hidden) == FileAttributes.Hidden;
					var startWithDot = findData.cFileName.StartsWith('.');
					var isDirectory = (attributes & FileAttributes.Directory) == FileAttributes.Directory;
					var shouldInclude = (!isHidden ||
						(showHiddenItems && (!isSystem || showProtectedSystemFiles))) &&
						(!startWithDot || showDotFiles) &&
						(!isDirectory || (findData.cFileName != "." && findData.cFileName != ".."));

					if (shouldInclude)
					{
						var entryData = findData;
						pendingItems.Add(Task.Run(async () => isDirectory
							? await GetFolder(entryData, path, isGitRepo, cancellationToken)
							: GetFile(entryData, path, isGitRepo, cancellationToken)));

						if (pendingItems.Count >= MaxConcurrentItemInitializations)
						{
							await ResolvePendingItemsAsync();
							await PublishIntermediateItemsAsync();
						}
					}

					if (cancellationToken.IsCancellationRequested || (countLimit >= 0 && count >= countLimit))
						break;
				} while (Win32PInvoke.FindNextFile(hFile, out findData));

				await ResolvePendingItemsAsync();
				cancellationToken.ThrowIfCancellationRequested();
			}
			finally
			{
				Win32PInvoke.FindClose(hFile);
			}

			performanceCallback?.Invoke(new(
				count,
				Stopwatch.GetTimestamp() - startedTimestamp,
				itemInitializationWaitTicks,
				postProcessingTicks,
				intermediateUpdateWaitTicks));
			return tempList;

			async Task ResolvePendingItemsAsync()
			{
				if (pendingItems.Count == 0)
					return;

				cancellationToken.ThrowIfCancellationRequested();
				var initializationStartedTimestamp = Stopwatch.GetTimestamp();
				var resolvedItems = await Task.WhenAll(pendingItems);
				itemInitializationWaitTicks += Stopwatch.GetTimestamp() - initializationStartedTimestamp;
				cancellationToken.ThrowIfCancellationRequested();
				pendingItems.Clear();

				var postProcessingStartedTimestamp = Stopwatch.GetTimestamp();
				foreach (var item in resolvedItems)
				{
					if (item is null)
						continue;

					tempList.Add(item);
					++count;

					if (areAlternateStreamsVisible)
						tempList.AddRange(EnumAdsForPath(item.ItemPath, item));

					if (calculateFolderSizes && item.IsFolder)
					{
						if (folderSizeProvider.TryGetSize(item.ItemPath, out var size))
						{
							item.FileSizeBytes = (long)size;
							item.FileSize = size.ToSizeString();
						}

						_ = folderSizeProvider.UpdateAsync(item.ItemPath, cancellationToken);
					}

					if (countLimit >= 0 && count >= countLimit)
						break;
				}
				postProcessingTicks += Stopwatch.GetTimestamp() - postProcessingStartedTimestamp;
			}

			async Task PublishIntermediateItemsAsync()
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (intermediateAction is not null && tempList.Count > 0 &&
					((!firstBatchPublished && count >= 32) || tempList.Count >= MaxIntermediateBatchSize || sampler.CheckNow()))
				{
					var updateStartedTimestamp = Stopwatch.GetTimestamp();
					await intermediateAction(tempList);
					intermediateUpdateWaitTicks += Stopwatch.GetTimestamp() - updateStartedTimestamp;
					cancellationToken.ThrowIfCancellationRequested();
					tempList.Clear();
					firstBatchPublished = true;
				}
			}
		}

		private static IEnumerable<ListedItem> EnumAdsForPath(string itemPath, ListedItem main)
		{
			foreach (var ads in Win32Helper.GetAlternateStreams(itemPath))
				yield return GetAlternateStream(ads, main);
		}

		public static ListedItem GetAlternateStream((string Name, long Size) ads, ListedItem main)
		{
			string itemType = Strings.File.GetLocalizedResource();
			string itemFileExtension = null;

			if (ads.Name.Contains('.'))
			{
				itemFileExtension = Path.GetExtension(ads.Name);
				itemType = itemFileExtension.Trim('.') + " " + itemType;
			}

			string adsName = ads.Name.Substring(1, ads.Name.Length - 7); // Remove ":" and ":$DATA"

			return new AlternateStreamItem()
			{
				PrimaryItemAttribute = StorageItemTypes.File,
				FileExtension = itemFileExtension,
				FileImage = null,
				LoadFileIcon = false,
				ItemNameRaw = adsName,
				IsHiddenItem = false,
				Opacity = Constants.UI.DimItemOpacity,
				ItemDateModifiedReal = main.ItemDateModifiedReal,
				ItemDateAccessedReal = main.ItemDateAccessedReal,
				ItemDateCreatedReal = main.ItemDateCreatedReal,
				ItemType = itemType,
				ItemPath = $"{main.ItemPath}:{adsName}",
				FileSize = ads.Size.ToSizeString(),
				FileSizeBytes = ads.Size
			};
		}

		public static async Task<ListedItem> GetFolder(
			Win32PInvoke.WIN32_FIND_DATA findData,
			string pathRoot,
			bool isGitRepo,
			CancellationToken cancellationToken
		)
		{
			if (cancellationToken.IsCancellationRequested)
				return null;

			DateTime itemModifiedDate;
			DateTime itemCreatedDate;

			try
			{
				Win32PInvoke.FileTimeToSystemTime(ref findData.ftLastWriteTime, out Win32PInvoke.SYSTEMTIME systemModifiedTimeOutput);
				itemModifiedDate = systemModifiedTimeOutput.ToDateTime();

				Win32PInvoke.FileTimeToSystemTime(ref findData.ftCreationTime, out Win32PInvoke.SYSTEMTIME systemCreatedTimeOutput);
				itemCreatedDate = systemCreatedTimeOutput.ToDateTime();
			}
			catch (ArgumentException)
			{
				// Invalid date means invalid findData, do not add to list
				return null;
			}

			var itemPath = Path.Combine(pathRoot, findData.cFileName);

			string itemName = await fileListCache.GetDisplayName(itemPath, cancellationToken);
			if (string.IsNullOrEmpty(itemName))
				itemName = findData.cFileName;

			bool isHidden = (((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden);
			double opacity = 1;

			if (isHidden)
				opacity = Constants.UI.DimItemOpacity;

			if (isGitRepo)
			{
				return new GitItem()
				{
					PrimaryItemAttribute = StorageItemTypes.Folder,
					ItemNameRaw = itemName,
					ItemDateModifiedReal = itemModifiedDate,
					ItemDateCreatedReal = itemCreatedDate,
					ItemType = folderTypeTextLocalized,
					FileImage = null,
					IsHiddenItem = isHidden,
					Opacity = opacity,
					LoadFileIcon = false,
					ItemPath = itemPath,
					FileSize = null,
					FileSizeBytes = 0,
				};
			}
			else
			{
				return new ListedItem(null)
				{
					PrimaryItemAttribute = StorageItemTypes.Folder,
					ItemNameRaw = itemName,
					ItemDateModifiedReal = itemModifiedDate,
					ItemDateCreatedReal = itemCreatedDate,
					ItemType = folderTypeTextLocalized,
					FileImage = null,
					IsHiddenItem = isHidden,
					Opacity = opacity,
					LoadFileIcon = false,
					ItemPath = itemPath,
					FileSize = null,
					FileSizeBytes = 0,
				};
			}
		}

		public static ListedItem GetFile(
			Win32PInvoke.WIN32_FIND_DATA findData,
			string pathRoot,
			bool isGitRepo,
			CancellationToken cancellationToken
		)
		{
			var itemPath = Path.Combine(pathRoot, findData.cFileName);
			var itemName = findData.cFileName;

			DateTime itemModifiedDate, itemCreatedDate, itemLastAccessDate;

			try
			{
				Win32PInvoke.FileTimeToSystemTime(ref findData.ftLastWriteTime, out Win32PInvoke.SYSTEMTIME systemModifiedDateOutput);
				itemModifiedDate = systemModifiedDateOutput.ToDateTime();

				Win32PInvoke.FileTimeToSystemTime(ref findData.ftCreationTime, out Win32PInvoke.SYSTEMTIME systemCreatedDateOutput);
				itemCreatedDate = systemCreatedDateOutput.ToDateTime();

				Win32PInvoke.FileTimeToSystemTime(ref findData.ftLastAccessTime, out Win32PInvoke.SYSTEMTIME systemLastAccessOutput);
				itemLastAccessDate = systemLastAccessOutput.ToDateTime();
			}
			catch (ArgumentException)
			{
				// Invalid date means invalid findData, do not add to list
				return null;
			}

			long itemSizeBytes = findData.GetSize();
			var itemSize = itemSizeBytes.ToSizeString();
			string itemType = Strings.File.GetLocalizedResource();
			string itemFileExtension = null;

			if (findData.cFileName.Contains('.'))
			{
				itemFileExtension = Path.GetExtension(itemPath);
				itemType = itemFileExtension.Trim('.') + " " + itemType;
			}

			bool itemThumbnailImgVis = false;
			bool itemEmptyImgVis = true;

			if (cancellationToken.IsCancellationRequested)
				return null;

			bool isHidden = ((FileAttributes)findData.dwFileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden;
			double opacity = isHidden ? Constants.UI.DimItemOpacity : 1;

			// https://learn.microsoft.com/openspecs/windows_protocols/ms-fscc/c8e77b37-3909-4fe6-a4ea-2b9d423b1ee4
			bool isReparsePoint = ((FileAttributes)findData.dwFileAttributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
			bool isSymlink = isReparsePoint && findData.dwReserved0 == Win32PInvoke.IO_REPARSE_TAG_SYMLINK;

			if (isSymlink)
			{
				return CreateUnresolvedShortcutItem(
					isGitRepo,
					isUrl: false,
					isSymlink: true,
					itemPath,
					itemName,
					itemFileExtension,
					isHidden,
					opacity,
					itemModifiedDate,
					itemLastAccessDate,
					itemCreatedDate,
					itemSize,
					itemSizeBytes);
			}
			else if (FileExtensionHelpers.IsShortcutOrUrlFile(findData.cFileName))
			{
				var isUrl = FileExtensionHelpers.IsWebLinkFile(findData.cFileName);
				return CreateUnresolvedShortcutItem(
					isGitRepo,
					isUrl,
					isSymlink: false,
					itemPath,
					itemName,
					itemFileExtension,
					isHidden,
					opacity,
					itemModifiedDate,
					itemLastAccessDate,
					itemCreatedDate,
					itemSize,
					itemSizeBytes);
			}
			else if (App.LibraryManager.TryGetLibrary(itemPath, out LibraryLocationItem library))
			{
				return new LibraryItem(library)
				{
					Opacity = opacity,
					ItemDateModifiedReal = itemModifiedDate,
					ItemDateCreatedReal = itemCreatedDate,
				};
			}
			else
			{
				ListedItem item = isGitRepo ? new GitItem() : new ListedItem(null);
				item.PrimaryItemAttribute = StorageItemTypes.File;
				item.FileExtension = itemFileExtension;
				item.FileImage = null;
				item.LoadFileIcon = itemThumbnailImgVis;
				item.ItemNameRaw = itemName;
				item.IsHiddenItem = isHidden;
				item.Opacity = opacity;
				item.ItemDateModifiedReal = itemModifiedDate;
				item.ItemDateAccessedReal = itemLastAccessDate;
				item.ItemDateCreatedReal = itemCreatedDate;
				item.ItemType = itemType;
				item.ItemPath = itemPath;
				item.FileSize = itemSize;
				item.FileSizeBytes = itemSizeBytes;
				item.NeedsArchiveAssociationCheck = FileExtensionHelpers.IsBrowsableZipFile(itemPath, out _);
				return item;
			}
		}

		private static ListedItem CreateUnresolvedShortcutItem(
			bool isGitRepo,
			bool isUrl,
			bool isSymlink,
			string itemPath,
			string itemName,
			string? itemFileExtension,
			bool isHidden,
			double opacity,
			DateTime itemModifiedDate,
			DateTime itemLastAccessDate,
			DateTime itemCreatedDate,
			string itemSize,
			long itemSizeBytes)
		{
			ListedItem item = isGitRepo ? new GitShortcutItem() : new ShortcutItem(null);
			item.PrimaryItemAttribute = StorageItemTypes.File;
			item.FileExtension = itemFileExtension;
			item.IsHiddenItem = isHidden;
			item.Opacity = opacity;
			item.FileImage = null;
			item.LoadFileIcon = false;
			item.ItemNameRaw = itemName;
			item.ItemDateModifiedReal = itemModifiedDate;
			item.ItemDateAccessedReal = itemLastAccessDate;
			item.ItemDateCreatedReal = itemCreatedDate;
			item.ItemType = isUrl
				? Strings.ShortcutWebLinkFileType.GetLocalizedResource()
				: Strings.Shortcut.GetLocalizedResource();
			item.ItemPath = itemPath;
			item.FileSize = itemSize;
			item.FileSizeBytes = itemSizeBytes;

			var shortcut = (IShortcutItem)item;
			shortcut.TargetPath = itemPath;
			shortcut.IsUrl = isUrl;
			shortcut.IsSymLink = isSymlink;

			return item;
		}
	}
}
