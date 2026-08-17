// Copyright (c) Files Community
// Licensed under the MIT License.

namespace Files.App.Data.Contracts
{
	internal interface IThumbnailCacheService
	{
		Task<byte[]?> GetAsync(string path, uint pixelSize, DateTimeOffset modified, long fileSize, CancellationToken cancellationToken);

		Task StoreAsync(string path, uint pixelSize, DateTimeOffset modified, long fileSize, byte[] data, CancellationToken cancellationToken = default);

		Task<long> GetSizeAsync(CancellationToken cancellationToken = default);

		Task TrimAsync(CancellationToken cancellationToken = default);

		Task ClearAsync(CancellationToken cancellationToken = default);
	}
}
