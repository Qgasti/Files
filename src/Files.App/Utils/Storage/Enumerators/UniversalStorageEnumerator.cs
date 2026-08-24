// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Services.SizeProvider;
using Files.Shared.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Storage;

namespace Files.App.Utils.Storage
{
	public static class UniversalStorageEnumerator
	{
		public readonly record struct PerformanceTimings(
			int ItemCount,
			long TotalElapsedTicks,
			long ProviderFetchElapsedTicks,
			long ProviderFetchWaitTicks,
			long ItemInitializationWaitTicks,
			long PostProcessingTicks,
			long IntermediateUpdateWaitTicks);

		private const int MaxConcurrentItemInitializations = 8;
		private const int MaxIntermediateBatchSize = 300;
		private const int MaxZipIntermediateBatchSize = 1000;

		private static readonly ISizeProvider folderSizeProvider = Ioc.Default.GetService<ISizeProvider>();

		public static async Task<List<ListedItem>> ListEntries(
			BaseStorageFolder rootFolder,
			StorageFolderWithPath currentStorageFolder,
			CancellationToken cancellationToken,
			int countLimit,
			Func<List<ListedItem>, Task> intermediateAction,
			Dictionary<string, BitmapImage> defaultIconPairs = null,
			Action<PerformanceTimings>? performanceCallback = null)
		{
			var startedTimestamp = Stopwatch.GetTimestamp();
			long providerFetchElapsedTicks = 0;
			long providerFetchWaitTicks = 0;
			long itemInitializationWaitTicks = 0;
			long postProcessingTicks = 0;
			long intermediateUpdateWaitTicks = 0;
			var sampler = new IntervalSampler(500);
			var tempList = new List<ListedItem>();
			uint sourceOffset = 0;
			var listedItemCount = 0;
			var firstRound = true;
			var firstBatchPublished = false;
			Task<IReadOnlyList<IStorageItem>>? prefetchedPageTask = null;
			IReadOnlyList<IStorageItem>? fullProviderItemList = null;
			var providerRequiresFullList = rootFolder is ZipStorageFolder or FtpStorageFolder;
			var followupPageSize = rootFolder is ZipStorageFolder ? MaxZipIntermediateBatchSize : MaxIntermediateBatchSize;

			IUserSettingsService userSettingsService = Ioc.Default.GetRequiredService<IUserSettingsService>();
			bool calculateFolderSizes = userSettingsService.FoldersSettingsService.CalculateFolderSizes;

			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				IReadOnlyList<IStorageItem> items;

				uint maxItemsToRetrieve = (uint)followupPageSize;

				if (intermediateAction is null)
				{
					// without intermediate action increase batches significantly
					maxItemsToRetrieve = 1000;
				}
				else if (firstRound)
				{
					maxItemsToRetrieve = 32;
					firstRound = false;
				}

				try
				{
					var pageTask = prefetchedPageTask;
					prefetchedPageTask = null;
					var providerWaitStartedTimestamp = Stopwatch.GetTimestamp();
					try
					{
						items = await (pageTask ?? FetchPageAsync(sourceOffset, maxItemsToRetrieve));
					}
					finally
					{
						providerFetchWaitTicks += Stopwatch.GetTimestamp() - providerWaitStartedTimestamp;
					}

					if (items is null || items.Count == 0)
					{
						break;
					}
				}
				catch (NotImplementedException)
				{
					break;
				}
				catch (OperationCanceledException) // Password dialog dismissed - let the caller handle it
				{
					throw;
				}
				catch (Exception ex) when (
					ex is UnauthorizedAccessException ||
					ex is FileNotFoundException ||
					(uint)ex.HResult == 0x80070490) // ERROR_NOT_FOUND
				{
					// If some unexpected exception is thrown - enumerate this folder file by file - just to be sure
					items = await EnumerateFileByFile(rootFolder, sourceOffset, maxItemsToRetrieve, cancellationToken);
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "Error enumerating directory contents.");

					break;
				}

				var nextSourceOffset = sourceOffset + maxItemsToRetrieve;
				if (countLimit < 0 || nextSourceOffset < countLimit)
				{
					var nextPageSize = intermediateAction is null ? 1000u : (uint)followupPageSize;
					prefetchedPageTask = FetchPageAsync(nextSourceOffset, nextPageSize);
					_ = prefetchedPageTask.ContinueWith(
						static completedTask => _ = completedTask.Exception,
						CancellationToken.None,
						TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
						TaskScheduler.Default);
				}

				for (var batchStart = 0; batchStart < items.Count; batchStart += MaxConcurrentItemInitializations)
				{
					cancellationToken.ThrowIfCancellationRequested();
					var pendingItems = new List<Task<ListedItem>>(MaxConcurrentItemInitializations);
					var batchEnd = Math.Min(batchStart + MaxConcurrentItemInitializations, items.Count);
					for (var itemIndex = batchStart; itemIndex < batchEnd; itemIndex++)
					{
						var item = items[itemIndex];
						if (!item.Name.StartsWith('.') || userSettingsService.FoldersSettingsService.ShowDotFiles)
							pendingItems.Add(CreateListedItemAsync(item));
					}

					if (pendingItems.Count > 0)
					{
						var initializationStartedTimestamp = Stopwatch.GetTimestamp();
						var resolvedItems = await Task.WhenAll(pendingItems);
						itemInitializationWaitTicks += Stopwatch.GetTimestamp() - initializationStartedTimestamp;
						cancellationToken.ThrowIfCancellationRequested();

						var postProcessingStartedTimestamp = Stopwatch.GetTimestamp();
						foreach (var listedItem in resolvedItems)
						{
							if (listedItem is null)
								continue;

							ApplyInitialIcon(listedItem);
							ApplyFolderSize(listedItem);
							tempList.Add(listedItem);
							++listedItemCount;
						}
						postProcessingTicks += Stopwatch.GetTimestamp() - postProcessingStartedTimestamp;
					}

					await PublishIntermediateItemsAsync();
				}

				sourceOffset += maxItemsToRetrieve;

				if (countLimit > -1 && sourceOffset >= countLimit)
					break;
			}

			performanceCallback?.Invoke(new(
				listedItemCount,
				Stopwatch.GetTimestamp() - startedTimestamp,
				providerFetchElapsedTicks,
				providerFetchWaitTicks,
				itemInitializationWaitTicks,
				postProcessingTicks,
				intermediateUpdateWaitTicks));
			return tempList;

			async Task<IReadOnlyList<IStorageItem>> FetchPageAsync(uint offset, uint pageSize)
			{
				var providerFetchStartedTimestamp = Stopwatch.GetTimestamp();
				try
				{
					if (providerRequiresFullList)
					{
						fullProviderItemList ??= await AwaitProviderOperationAsync(rootFolder.GetItemsAsync(), cancellationToken);
						return fullProviderItemList
							.Skip((int)offset)
							.Take((int)pageSize)
							.ToList();
					}

					return await AwaitProviderOperationAsync(rootFolder.GetItemsAsync(offset, pageSize), cancellationToken);
				}
				finally
				{
					Interlocked.Add(ref providerFetchElapsedTicks, Stopwatch.GetTimestamp() - providerFetchStartedTimestamp);
				}
			}

			Task<ListedItem> CreateListedItemAsync(IStorageItem item)
				=> item.IsOfType(StorageItemTypes.Folder)
					? AddFolderAsync(item.AsBaseStorageFolder(), currentStorageFolder, cancellationToken)
					: AddFileAsync(item.AsBaseStorageFile(), currentStorageFolder, cancellationToken);

			void ApplyInitialIcon(ListedItem listedItem)
			{
				if (listedItem.IsFolder)
				{
					if (defaultIconPairs?.TryGetValue(string.Empty, out var folderIcon) ?? false)
						listedItem.FileImage = folderIcon;
				}
				else if (defaultIconPairs is not null && !string.IsNullOrEmpty(listedItem.FileExtension) &&
					defaultIconPairs.TryGetValue(listedItem.FileExtension.ToLowerInvariant(), out var fileIcon))
				{
					listedItem.FileImage = fileIcon;
				}
			}

			void ApplyFolderSize(ListedItem listedItem)
			{
				if (!calculateFolderSizes || !listedItem.IsFolder || !FolderHelpers.CheckFolderAccessWithWin32(listedItem.ItemPath))
					return;

				if (folderSizeProvider.TryGetSize(listedItem.ItemPath, out var size))
				{
					listedItem.FileSizeBytes = (long)size;
					listedItem.FileSize = size.ToSizeString();
				}

				_ = folderSizeProvider.UpdateAsync(listedItem.ItemPath, cancellationToken);
			}

			async Task PublishIntermediateItemsAsync()
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (intermediateAction is null || tempList.Count == 0 ||
					(firstBatchPublished || listedItemCount < 32) && tempList.Count < followupPageSize && !sampler.CheckNow())
				{
					return;
				}

				var updateStartedTimestamp = Stopwatch.GetTimestamp();
				await intermediateAction(tempList);
				intermediateUpdateWaitTicks += Stopwatch.GetTimestamp() - updateStartedTimestamp;
				cancellationToken.ThrowIfCancellationRequested();
				_ = sampler.CheckNow();
				tempList.Clear();
				firstBatchPublished = true;
			}
		}

		private static async Task<IReadOnlyList<IStorageItem>> EnumerateFileByFile(BaseStorageFolder rootFolder, uint startFrom, uint itemsToIterate, CancellationToken cancellationToken)
		{
			var tempList = new List<IStorageItem>();

			for (var i = startFrom; i < startFrom + itemsToIterate; i++)
			{
				cancellationToken.ThrowIfCancellationRequested();
				IStorageItem item;
				try
				{
					var results = await AwaitProviderOperationAsync(rootFolder.GetItemsAsync(i, 1), cancellationToken);

					item = results?.FirstOrDefault();
					if (item is null)
						break;
				}
				catch (NotImplementedException)
				{
					break;
				}
				catch (Exception ex) when (
					ex is UnauthorizedAccessException ||
					ex is FileNotFoundException ||
					(uint)ex.HResult == 0x80070490) // ERROR_NOT_FOUND
				{
					continue;
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, "Error enumerating directory contents.");
					break;
				}

				tempList.Add(item);
			}

			return tempList;
		}

		private static async Task<T> AwaitProviderOperationAsync<T>(IAsyncOperation<T> operation, CancellationToken cancellationToken)
		{
			var providerTask = operation.AsTask();
			try
			{
				return await providerTask.WaitAsync(cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				operation.Cancel();
				_ = providerTask.ContinueWith(
					static completedTask => _ = completedTask.Exception,
					CancellationToken.None,
					TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
				throw;
			}
		}

		public static async Task<ListedItem> AddFolderAsync(
			BaseStorageFolder folder,
			StorageFolderWithPath currentStorageFolder,
			CancellationToken cancellationToken)
		{
			var basicProperties = folder switch
			{
				ShellStorageFolder shellFolder => shellFolder.InitialBasicProperties,
				ZipStorageFolder { InitialBasicProperties: not null } zipFolder => zipFolder.InitialBasicProperties,
				FtpStorageFolder { InitialBasicProperties: not null } ftpFolder => ftpFolder.InitialBasicProperties,
				_ => await AwaitProviderOperationAsync(folder.GetBasicPropertiesAsync(), cancellationToken)
			};
			cancellationToken.ThrowIfCancellationRequested();
			if (!cancellationToken.IsCancellationRequested)
			{
				if (folder is ShortcutStorageFolder linkFolder)
				{
					return new ShortcutItem(folder.FolderRelativeId)
					{
						PrimaryItemAttribute = StorageItemTypes.Folder,
						IsHiddenItem = false,
						Opacity = 1,
						FileImage = null,
						LoadFileIcon = false,
						ItemNameRaw = folder.DisplayName,
						ItemDateModifiedReal = basicProperties.DateModified,
						ItemDateCreatedReal = folder.DateCreated,
						ItemType = folder.DisplayType,
						ItemPath = folder.Path,
						FileSize = null,
						FileSizeBytes = 0,
						TargetPath = linkFolder.TargetPath,
						Arguments = linkFolder.Arguments,
						WorkingDirectory = linkFolder.WorkingDirectory,
						RunAsAdmin = linkFolder.RunAsAdmin,
						ShowWindowCommand = linkFolder.ShowWindowCommand
					};
				}
				else if (folder is BinStorageFolder binFolder)
				{
					return new RecycleBinItem(folder.FolderRelativeId)
					{
						PrimaryItemAttribute = StorageItemTypes.Folder,
						ItemNameRaw = folder.DisplayName,
						ItemDateModifiedReal = basicProperties.DateModified,
						ItemDateCreatedReal = folder.DateCreated,
						ItemType = folder.DisplayType,
						IsHiddenItem = false,
						Opacity = 1,
						FileImage = null,
						LoadFileIcon = false,
						ItemPath = string.IsNullOrEmpty(folder.Path) ? PathNormalization.Combine(currentStorageFolder.Path, folder.Name) : folder.Path,
						FileSize = basicProperties.Size.ToSizeString(),
						FileSizeBytes = (long)basicProperties.Size,
						ItemDateDeletedReal = binFolder.DateDeleted,
						ItemOriginalPath = binFolder.OriginalPath,
					};
				}
				else
				{
					return new ListedItem(folder.FolderRelativeId)
					{
						PrimaryItemAttribute = StorageItemTypes.Folder,
						ItemNameRaw = folder.DisplayName,
						ItemDateModifiedReal = basicProperties.DateModified,
						ItemDateCreatedReal = folder.DateCreated,
						ItemType = folder.DisplayType,
						IsHiddenItem = false,
						Opacity = 1,
						FileImage = null,
						LoadFileIcon = false,
						ItemPath = string.IsNullOrEmpty(folder.Path) ? PathNormalization.Combine(currentStorageFolder.Path, folder.Name) : folder.Path,
						FileSize = null,
						FileSizeBytes = 0
					};
				}
			}

			return null;
		}

		public static async Task<ListedItem> AddFileAsync(
			BaseStorageFile file,
			StorageFolderWithPath currentStorageFolder,
			CancellationToken cancellationToken)
		{
			var basicProperties = file switch
			{
				ShellStorageFile shellFile => shellFile.InitialBasicProperties,
				ZipStorageFile { InitialBasicProperties: not null } zipFile => zipFile.InitialBasicProperties,
				FtpStorageFile { InitialBasicProperties: not null } ftpFile => ftpFile.InitialBasicProperties,
				_ => await AwaitProviderOperationAsync(file.GetBasicPropertiesAsync(), cancellationToken)
			};
			cancellationToken.ThrowIfCancellationRequested();
			// Display name does not include extension
			var itemName = file.Name;
			var itemModifiedDate = basicProperties.DateModified;
			var itemCreatedDate = file.DateCreated;
			var itemPath = string.IsNullOrEmpty(file.Path) ? PathNormalization.Combine(currentStorageFolder.Path, file.Name) : file.Path;
			var itemSize = basicProperties.Size.ToSizeString();
			var itemSizeBytes = basicProperties.Size;
			var itemType = file.DisplayType;
			var itemFileExtension = file.FileType;
			var itemThumbnailImgVis = false;

			if (cancellationToken.IsCancellationRequested)
				return null;

			// TODO: is this needed to be handled here?
			if (App.LibraryManager.TryGetLibrary(file.Path, out LibraryLocationItem library))
			{
				return new LibraryItem(library)
				{
					Opacity = 1,
					ItemDateModifiedReal = itemModifiedDate,
					ItemDateCreatedReal = itemCreatedDate,
				};
			}
			else
			{
				if (file is ShortcutStorageFile linkFile)
				{
					var isUrl = FileExtensionHelpers.IsWebLinkFile(linkFile.Name);
					return new ShortcutItem(file.FolderRelativeId)
					{
						PrimaryItemAttribute = StorageItemTypes.File,
						FileExtension = itemFileExtension,
						IsHiddenItem = false,
						Opacity = 1,
						FileImage = null,
						LoadFileIcon = itemThumbnailImgVis,
						ItemNameRaw = itemName,
						ItemDateModifiedReal = itemModifiedDate,
						ItemDateCreatedReal = itemCreatedDate,
						ItemType = itemType,
						ItemPath = itemPath,
						FileSize = itemSize,
						FileSizeBytes = (long)itemSizeBytes,
						TargetPath = linkFile.TargetPath,
						Arguments = linkFile.Arguments,
						WorkingDirectory = linkFile.WorkingDirectory,
						RunAsAdmin = linkFile.RunAsAdmin,
						ShowWindowCommand = linkFile.ShowWindowCommand,
						IsUrl = isUrl,
					};
				}
				else if (file is BinStorageFile binFile)
				{
					return new RecycleBinItem(file.FolderRelativeId)
					{
						PrimaryItemAttribute = StorageItemTypes.File,
						FileExtension = itemFileExtension,
						IsHiddenItem = false,
						Opacity = 1,
						FileImage = null,
						LoadFileIcon = itemThumbnailImgVis,
						ItemNameRaw = itemName,
						ItemDateModifiedReal = itemModifiedDate,
						ItemDateCreatedReal = itemCreatedDate,
						ItemType = itemType,
						ItemPath = itemPath,
						FileSize = itemSize,
						FileSizeBytes = (long)itemSizeBytes,
						ItemDateDeletedReal = binFile.DateDeleted,
						ItemOriginalPath = binFile.OriginalPath
					};
				}
				else
				{
					return new ListedItem(file.FolderRelativeId)
					{
						PrimaryItemAttribute = StorageItemTypes.File,
						FileExtension = itemFileExtension,
						IsHiddenItem = false,
						Opacity = 1,
						FileImage = null,
						LoadFileIcon = itemThumbnailImgVis,
						ItemNameRaw = itemName,
						ItemDateModifiedReal = itemModifiedDate,
						ItemDateCreatedReal = itemCreatedDate,
						ItemType = itemType,
						ItemPath = itemPath,
						FileSize = itemSize,
						FileSizeBytes = (long)itemSizeBytes,
					};
				}
			}
		}
	}
}
