using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Caching;
using System.Diagnostics.CodeAnalysis;

namespace PeterJuhasz.Repositories.Blobs;

public class BlobBinaryRepository(IBlob blob) : IBinaryRepository
{
	public IBlob Blob => blob;

	public Task<bool> ExistsAsync(CancellationToken cancellationToken) => blob.ExistsAsync(cancellationToken);

	public async Task<BinaryData?> GetAsync(CancellationToken cancellationToken)
	{
		var result = await blob.ReadAsync(cancellationToken);
		if (result is null)
		{
			return null;
		}

		return result.Value;
	}

	public async Task<Stream?> GetStreamAsync(CancellationToken cancellationToken)
	{
		var result = await blob.OpenReadAsync(cancellationToken);
		return result?.Value;
	}

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		var info = await blob.GetInfoAsync(cancellationToken);
		return info?.ConcurrencyToken;
	}

	public Task StoreAsync(BinaryData value, string? etag, CancellationToken cancellationToken) =>
		blob.WriteAsync(value.ToMemory(), etag, new(MediaType: value.MediaType), cancellationToken);

	public async Task StoreAsync(Stream value, string? mediaType, string? etag, CancellationToken cancellationToken)
	{
		await using var stream = await blob.OpenWriteAsync(etag, new(MediaType: mediaType), cancellationToken);
		await value.CopyToAsync(stream, cancellationToken);
		await stream.FlushAsync(cancellationToken);
	}

	public Task DeleteIfExistsAsync(CancellationToken cancellationToken) => blob.DeleteIfExistsAsync(cancellationToken);

	public Task DeleteAsync(string etag, CancellationToken cancellationToken) => blob.DeleteAsync(etag, cancellationToken);
}

public static partial class Extensions
{
	extension(IBlob blob)
	{
		public IBinaryRepository AsBinaryRepository() => new BlobBinaryRepository(blob);
	}

	extension(IBinaryRepository repository)
	{
		public bool TryGetBlob([NotNullWhen(true)] out IBlob? blob)
		{
			switch (repository)
			{
				case BlobBinaryRepository blobRepo:
					blob = blobRepo.Blob;
					return true;

				case CachingBinaryRepository cachingRepo:
					return cachingRepo.Inner.TryGetBlob(out blob);

				case GlobalCachingBlobBinaryRepository globalCachingBlobSingleObjectRepository:
					return globalCachingBlobSingleObjectRepository.Inner.TryGetBlob(out blob);

				default:
					blob = null;
					return false;
			}
		}
	}
}