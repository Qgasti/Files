// Copyright (c) Files Community
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage;

namespace Files.App.Services
{
	internal sealed class ThumbnailCacheService : IThumbnailCacheService, IDisposable
	{
		private const int MaxConcurrentReads = 8;
		private const int WritesBetweenTrims = 16;
		private const string CacheFileExtension = ".thumb";

		private readonly IUserSettingsService userSettingsService;
		private readonly SemaphoreSlim cacheReadSemaphore = new(MaxConcurrentReads, MaxConcurrentReads);
		private readonly SemaphoreSlim cacheIoSemaphore = new(1, 1);
		private readonly string cacheDirectory = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "Thumbnails");
		private int writesSinceTrim;

		public ThumbnailCacheService(IUserSettingsService userSettingsService)
		{
			this.userSettingsService = userSettingsService;
		}

		public async Task<byte[]?> GetAsync(
			string path,
			uint pixelSize,
			DateTimeOffset modified,
			long fileSize,
			CancellationToken cancellationToken)
		{
			if (!userSettingsService.GeneralSettingsService.EnableThumbnailCache)
				return null;

			var cachePath = GetCachePath(path, pixelSize, modified, fileSize);
			var lockTaken = false;
			try
			{
				await cacheReadSemaphore.WaitAsync(cancellationToken);
				lockTaken = true;
				var data = await File.ReadAllBytesAsync(cachePath, cancellationToken);
				if (data.Length == 0)
					return null;

				try
				{
					File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow);
				}
				catch (Exception ex)
				{
					App.Logger.LogDebug(ex, "Failed to update a persistent thumbnail cache access time.");
				}

				return data;
			}
			catch (FileNotFoundException)
			{
				return null;
			}
			catch (DirectoryNotFoundException)
			{
				return null;
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "Failed to read a persistent thumbnail cache entry.");
				return null;
			}
			finally
			{
				if (lockTaken)
					cacheReadSemaphore.Release();
			}
		}

		public async Task StoreAsync(
			string path,
			uint pixelSize,
			DateTimeOffset modified,
			long fileSize,
			byte[] data,
			CancellationToken cancellationToken = default)
		{
			if (!userSettingsService.GeneralSettingsService.EnableThumbnailCache || data.Length == 0)
				return;

			var lockTaken = false;
			try
			{
				await cacheIoSemaphore.WaitAsync(cancellationToken);
				lockTaken = true;
				Directory.CreateDirectory(cacheDirectory);
				var cachePath = GetCachePath(path, pixelSize, modified, fileSize);
				var temporaryPath = Path.Combine(cacheDirectory, $"{Guid.NewGuid():N}.tmp");

				try
				{
					await File.WriteAllBytesAsync(temporaryPath, data, cancellationToken);
					File.Move(temporaryPath, cachePath, true);
				}
				finally
				{
					if (File.Exists(temporaryPath))
						File.Delete(temporaryPath);
				}

				if (Interlocked.Increment(ref writesSinceTrim) >= WritesBetweenTrims)
				{
					Interlocked.Exchange(ref writesSinceTrim, 0);
					TrimCore();
				}
			}
			catch (OperationCanceledException)
			{
			}
			catch (Exception ex)
			{
				App.Logger.LogDebug(ex, "Failed to persist a thumbnail cache entry.");
			}
			finally
			{
				if (lockTaken)
					cacheIoSemaphore.Release();
			}
		}

		public async Task<long> GetSizeAsync(CancellationToken cancellationToken = default)
		{
			await cacheIoSemaphore.WaitAsync(cancellationToken);
			try
			{
				return EnumerateCacheFiles().Sum(file => file.Length);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogDebug(ex, "Failed to calculate persistent thumbnail cache size.");
				return 0;
			}
			finally
			{
				cacheIoSemaphore.Release();
			}
		}

		public async Task TrimAsync(CancellationToken cancellationToken = default)
		{
			await cacheIoSemaphore.WaitAsync(cancellationToken);
			try
			{
				TrimCore();
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				App.Logger.LogDebug(ex, "Failed to trim the persistent thumbnail cache.");
			}
			finally
			{
				cacheIoSemaphore.Release();
			}
		}

		public async Task ClearAsync(CancellationToken cancellationToken = default)
		{
			await cacheIoSemaphore.WaitAsync(cancellationToken);
			try
			{
				foreach (var file in EnumerateCacheFiles().ToList())
				{
					cancellationToken.ThrowIfCancellationRequested();
					try
					{
						file.Delete();
					}
					catch (Exception ex)
					{
						App.Logger.LogDebug(ex, "Failed to delete a persistent thumbnail cache entry.");
					}
				}
			}
			finally
			{
				cacheIoSemaphore.Release();
			}
		}

		private void TrimCore()
		{
			var cacheFiles = EnumerateCacheFiles()
				.OrderBy(file => file.LastAccessTimeUtc)
				.ThenBy(file => file.LastWriteTimeUtc)
				.ToList();
			var totalSize = cacheFiles.Sum(file => file.Length);
			var limit = (long)(Math.Clamp(userSettingsService.GeneralSettingsService.ThumbnailCacheSizeLimit, 100d, 5000d) * 1024 * 1024);

			foreach (var file in cacheFiles)
			{
				if (totalSize <= limit)
					break;

				var length = file.Length;
				file.Delete();
				totalSize -= length;
			}
		}

		private IEnumerable<FileInfo> EnumerateCacheFiles()
		{
			if (!Directory.Exists(cacheDirectory))
				return [];

			return new DirectoryInfo(cacheDirectory).EnumerateFiles($"*{CacheFileExtension}", SearchOption.TopDirectoryOnly);
		}

		private string GetCachePath(string path, uint pixelSize, DateTimeOffset modified, long fileSize)
		{
			var normalizedPath = path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
			var identity = $"{normalizedPath}\n{pixelSize}\n{modified.UtcTicks}\n{fileSize}";
			var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
			return Path.Combine(cacheDirectory, hash + CacheFileExtension);
		}

		public void Dispose()
		{
			cacheReadSemaphore.Dispose();
			cacheIoSemaphore.Dispose();
		}
	}
}
