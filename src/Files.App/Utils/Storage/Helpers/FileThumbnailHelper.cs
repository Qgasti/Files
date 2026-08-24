// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Shared.Helpers;
using System.IO;
using Windows.Storage.FileProperties;

namespace Files.App.Utils.Storage
{
	public static class FileThumbnailHelper
	{
		private const int MaxConcurrentShellThumbnailWork = 8;

		private static readonly SemaphoreSlim shellThumbnailWorkSemaphore = new(MaxConcurrentShellThumbnailWork, MaxConcurrentShellThumbnailWork);

		/// <summary>
		/// Returns icon or thumbnail for given file or folder
		/// </summary>
		public static async Task<byte[]?> GetIconAsync(
			string path,
			uint requestedSize,
			bool isFolder,
			IconOptions iconOptions,
			CancellationToken cancellationToken = default,
			Action? shellWorkStartedCallback = null,
			Action<Task>? detachedShellWorkCallback = null)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var size = iconOptions.HasFlag(IconOptions.UseCurrentScale) ? requestedSize * App.AppModel.AppWindowDPI : requestedSize;
			// Ensure size is at least 1 to prevent layout errors
			size = Math.Max(1, size);

			if (!isFolder && !iconOptions.HasFlag(IconOptions.ReturnIconOnly) && !iconOptions.HasFlag(IconOptions.ReturnOnlyIfCached))
			{
				var extension = Path.GetExtension(path);

				//Restrict to only %windir%\fonts
				if (FileExtensionHelpers.IsFontFile(extension) && PathHelpers.IsInSystemFontsFolder(path))
				{
					var winrtThumbnail = await FontFileHelper.GetWinRTThumbnailAsync(path, (uint)size, cancellationToken);
					if (winrtThumbnail is not null)
						return winrtThumbnail;

					if (!extension.Equals(".fon", StringComparison.OrdinalIgnoreCase))
					{
						var fontThumbnail = await RunShellWorkAsync(
							() => cancellationToken.IsCancellationRequested ? null : FontFileHelper.GenerateFontThumbnail(path, (int)size),
							cancellationToken,
							shellWorkStartedCallback,
							detachedShellWorkCallback);
						cancellationToken.ThrowIfCancellationRequested();
						if (fontThumbnail is not null)
							return fontThumbnail;
					}
				}
			}

			var resolvedPath = path is not null && path.StartsWith(@"\\?\", StringComparison.Ordinal)
				? MtpHelpers.ResolveMtpShellPath(path) ?? path
				: path;

			var result = await RunShellWorkAsync(
				() => cancellationToken.IsCancellationRequested ? null : Win32Helper.GetIcon(resolvedPath, (int)size, isFolder, iconOptions),
				cancellationToken,
				shellWorkStartedCallback,
				detachedShellWorkCallback);
			cancellationToken.ThrowIfCancellationRequested();
			return result;
		}

		/// <summary>
		/// Returns overlay for given file or folder
		/// /// </summary>
		/// <param name="path"></param>
		/// <param name="isFolder"></param>
		/// <returns></returns>
		public static async Task<byte[]?> GetIconOverlayAsync(
			string path,
			bool isFolder,
			CancellationToken cancellationToken = default,
			Action? shellWorkStartedCallback = null,
			Action<Task>? detachedShellWorkCallback = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var result = await RunShellWorkAsync(
				() => cancellationToken.IsCancellationRequested ? null : Win32Helper.GetIconOverlay(path, isFolder),
				cancellationToken,
				shellWorkStartedCallback,
				detachedShellWorkCallback);
			cancellationToken.ThrowIfCancellationRequested();
			return result;
		}

		private static async Task<T> RunShellWorkAsync<T>(
			Func<T> shellWorkFactory,
			CancellationToken cancellationToken,
			Action? shellWorkStartedCallback,
			Action<Task>? detachedShellWorkCallback)
		{
			await shellThumbnailWorkSemaphore.WaitAsync(cancellationToken);
			var releaseWhenCompleted = false;
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				var shellWork = STATask.Run(shellWorkFactory, App.Logger);
				shellWorkStartedCallback?.Invoke();
				try
				{
					return await shellWork.WaitAsync(cancellationToken);
				}
				catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
				{
					if (!shellWork.IsCompleted)
					{
						releaseWhenCompleted = true;
						_ = shellWork.ContinueWith(
							static (_, state) => ((SemaphoreSlim)state!).Release(),
							shellThumbnailWorkSemaphore,
							CancellationToken.None,
							TaskContinuationOptions.ExecuteSynchronously,
							TaskScheduler.Default);
						detachedShellWorkCallback?.Invoke(shellWork);
					}

					throw;
				}
			}
			finally
			{
				if (!releaseWhenCompleted)
					shellThumbnailWorkSemaphore.Release();
			}
		}

		[Obsolete]
		public static async Task<byte[]?> LoadIconFromPathAsync(string filePath, uint thumbnailSize, ThumbnailMode thumbnailMode, ThumbnailOptions thumbnailOptions, bool isFolder = false)
		{
			var result = await GetIconAsync(filePath, thumbnailSize, isFolder, IconOptions.None);
			return result;
		}
	}
}
