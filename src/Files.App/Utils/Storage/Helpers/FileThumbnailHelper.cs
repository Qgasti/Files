// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.Shared.Helpers;
using System.IO;
using Windows.Storage.FileProperties;

namespace Files.App.Utils.Storage
{
	public static class FileThumbnailHelper
	{
		/// <summary>
		/// Returns icon or thumbnail for given file or folder
		/// </summary>
		public static async Task<byte[]?> GetIconAsync(
			string path,
			uint requestedSize,
			bool isFolder,
			IconOptions iconOptions,
			CancellationToken cancellationToken = default,
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
						var fontThumbnailTask = STATask.Run(
							() => cancellationToken.IsCancellationRequested ? null : FontFileHelper.GenerateFontThumbnail(path, (int)size),
							App.Logger);
						var fontThumbnail = await WaitForStaResultAsync(fontThumbnailTask, cancellationToken, detachedShellWorkCallback);
						cancellationToken.ThrowIfCancellationRequested();
						if (fontThumbnail is not null)
							return fontThumbnail;
					}
				}
			}

			var resolvedPath = path is not null && path.StartsWith(@"\\?\", StringComparison.Ordinal)
				? MtpHelpers.ResolveMtpShellPath(path) ?? path
				: path;

			var shellWork = STATask.Run(
				() => cancellationToken.IsCancellationRequested ? null : Win32Helper.GetIcon(resolvedPath, (int)size, isFolder, iconOptions),
				App.Logger);
			var result = await WaitForStaResultAsync(shellWork, cancellationToken, detachedShellWorkCallback);
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
			Action<Task>? detachedShellWorkCallback = null)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var shellWork = STATask.Run(
				() => cancellationToken.IsCancellationRequested ? null : Win32Helper.GetIconOverlay(path, isFolder),
				App.Logger);
			var result = await WaitForStaResultAsync(shellWork, cancellationToken, detachedShellWorkCallback);
			cancellationToken.ThrowIfCancellationRequested();
			return result;
		}

		private static async Task<T> WaitForStaResultAsync<T>(
			Task<T> shellWork,
			CancellationToken cancellationToken,
			Action<Task>? detachedShellWorkCallback)
		{
			try
			{
				return await shellWork.WaitAsync(cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				if (!shellWork.IsCompleted)
					detachedShellWorkCallback?.Invoke(shellWork);

				throw;
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
