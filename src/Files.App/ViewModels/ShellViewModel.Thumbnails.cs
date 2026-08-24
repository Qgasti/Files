// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.ViewModels
{
	public sealed partial class ShellViewModel
	{
		private const int MaxConcurrentInitialThumbnailLoads = 6;

		private readonly SemaphoreSlim initialThumbnailSemaphore;

		private async Task<byte[]?> GetInitialIconAsync(
			string path,
			uint requestedSize,
			bool isFolder,
			IconOptions options,
			CancellationToken cancellationToken)
		{
			await initialThumbnailSemaphore.WaitAsync(cancellationToken);
			try
			{
				var loadMetrics = Volatile.Read(ref activeFolderLoadMetrics);
				return await FileThumbnailHelper.GetIconAsync(
					path,
					requestedSize,
					isFolder,
					options,
					cancellationToken,
					loadMetrics is null ? null : loadMetrics.RecordShellThumbnailStarted,
					loadMetrics is null ? null : loadMetrics.RecordDetachedShellWork);
			}
			finally
			{
				initialThumbnailSemaphore.Release();
			}
		}

		private async Task<byte[]?> GetInitialIconOverlayAsync(string path, bool isFolder, CancellationToken cancellationToken)
		{
			await initialThumbnailSemaphore.WaitAsync(cancellationToken);
			try
			{
				var loadMetrics = Volatile.Read(ref activeFolderLoadMetrics);
				return await FileThumbnailHelper.GetIconOverlayAsync(
					path,
					isFolder,
					cancellationToken,
					loadMetrics is null ? null : loadMetrics.RecordShellThumbnailStarted,
					loadMetrics is null ? null : loadMetrics.RecordDetachedShellWork);
			}
			finally
			{
				initialThumbnailSemaphore.Release();
			}
		}
	}
}
