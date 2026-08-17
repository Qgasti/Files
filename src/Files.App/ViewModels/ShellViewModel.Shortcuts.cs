// Copyright (c) Files Community
// Licensed under the MIT License.

using Windows.Storage;

namespace Files.App.ViewModels
{
	public sealed partial class ShellViewModel
	{
		private const int MaxConcurrentShortcutEnrichments = 4;
		private static readonly TimeSpan DeferredItemCollectionRefreshDelay = TimeSpan.FromMilliseconds(150);

		private readonly SemaphoreSlim shortcutEnrichmentSemaphore;
		private CancellationTokenSource? deferredItemCollectionRefreshCts;

		private async Task<bool> EnrichShortcutAsync(ListedItem item, CancellationToken cancellationToken)
		{
			if (item is not IShortcutItem shortcut)
				return false;

			await shortcutEnrichmentSemaphore.WaitAsync(cancellationToken);
			try
			{
				ShellLinkItem? shortcutInfo;
				if (shortcut.IsSymLink)
				{
					var targetPath = await Task.Run(() => Win32Helper.ParseSymLink(item.ItemPath), cancellationToken);
					shortcutInfo = new ShellLinkItem { TargetPath = targetPath };
				}
				else
				{
					shortcutInfo = await FileOperationsHelpers.ParseLinkAsync(item.ItemPath);
				}

				cancellationToken.ThrowIfCancellationRequested();
				if (shortcutInfo is null)
					return false;

				var primaryItemAttribute = shortcutInfo.IsFolder ? StorageItemTypes.Folder : StorageItemTypes.File;
				var classificationChanged = item.PrimaryItemAttribute != primaryItemAttribute;

				await dispatcherQueue.EnqueueOrInvokeAsync(() =>
				{
					item.PrimaryItemAttribute = primaryItemAttribute;
					shortcut.TargetPath = string.IsNullOrEmpty(shortcutInfo.TargetPath) ? item.ItemPath : shortcutInfo.TargetPath;
					shortcut.Arguments = shortcutInfo.Arguments;
					shortcut.WorkingDirectory = shortcutInfo.WorkingDirectory;
					shortcut.RunAsAdmin = shortcutInfo.RunAsAdmin;
					shortcut.ShowWindowCommand = shortcutInfo.ShowWindowCommand;
				});

				return classificationChanged;
			}
			finally
			{
				shortcutEnrichmentSemaphore.Release();
			}
		}

		private async Task<bool> EnrichArchiveCandidateAsync(ListedItem item, CancellationToken cancellationToken)
		{
			if (!item.NeedsArchiveAssociationCheck)
				return false;

			await shortcutEnrichmentSemaphore.WaitAsync(cancellationToken);
			try
			{
				var isArchive = await ZipStorageFolder.CheckDefaultZipApp(item.ItemPath);
				cancellationToken.ThrowIfCancellationRequested();

				await dispatcherQueue.EnqueueOrInvokeAsync(() =>
				{
					item.NeedsArchiveAssociationCheck = false;
					item.IsArchive = isArchive;
					if (isArchive)
						item.PrimaryItemAttribute = StorageItemTypes.Folder;
				});

				return isArchive;
			}
			finally
			{
				shortcutEnrichmentSemaphore.Release();
			}
		}

		private void ScheduleDeferredItemCollectionRefresh()
		{
			var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(loadPropsCTS.Token);
			var previousCts = Interlocked.Exchange(ref deferredItemCollectionRefreshCts, refreshCts);
			previousCts?.Cancel();
			previousCts?.Dispose();

			_ = RefreshDeferredItemCollectionAsync(refreshCts);
		}

		private async Task RefreshDeferredItemCollectionAsync(CancellationTokenSource refreshCts)
		{
			try
			{
				await Task.Delay(DeferredItemCollectionRefreshDelay, refreshCts.Token);
				await OrderFilesAndFoldersAsync();
				await ApplyFilesAndFoldersChangesAsync();
			}
			catch (OperationCanceledException)
			{
			}
			finally
			{
				if (ReferenceEquals(Interlocked.CompareExchange(ref deferredItemCollectionRefreshCts, null, refreshCts), refreshCts))
					refreshCts.Dispose();
			}
		}

		private void CancelDeferredItemCollectionRefresh()
		{
			var refreshCts = Interlocked.Exchange(ref deferredItemCollectionRefreshCts, null);
			refreshCts?.Cancel();
			refreshCts?.Dispose();
		}
	}
}
