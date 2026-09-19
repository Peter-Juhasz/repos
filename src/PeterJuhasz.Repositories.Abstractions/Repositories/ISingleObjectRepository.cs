using PeterJuhasz.Repositories.Compression;

namespace PeterJuhasz.Repositories.Abstractions;

public interface ISingleObjectRepository<T>
{
	Task<Versioned<T>> CreateAsync(string id, T value, CancellationToken cancellationToken = default) =>
		StoreAsync(id, value, etag: null, overwrite: false, cancellationToken);

	Task<Versioned<T>> StoreAsync(string id, T value, string? etag = null, bool overwrite = true, CancellationToken cancellationToken = default);

	Task<Versioned<T>> CreateOrUpdateAsync(string id, T value, string? etag = null, CancellationToken cancellationToken = default) =>
		StoreAsync(id, value, etag, overwrite: true, cancellationToken);

	Task<Versioned<T>> UpdateAsync(string id, T value, string etag, CancellationToken cancellationToken = default) =>
		StoreAsync(id, value, etag, overwrite: true, cancellationToken);

	Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default);

	ValueTask<T> GetAsync(string id, CancellationToken cancellationToken = default);

	Task<string?> GetVersionAsync(string id, CancellationToken cancellationToken = default);

	async ValueTask<T?> GetOrDefaultAsync(string id, CancellationToken cancellationToken = default)
	{
		if (await GetOrDefaultWithVersionAsync(id, cancellationToken) is { Value: T value })
		{
			return value;
		}

		return default;
	}

	ValueTask<Versioned<T>?> GetOrDefaultWithVersionAsync(string id, CancellationToken cancellationToken = default);

	Task DeleteAsync(string id, CancellationToken cancellationToken = default);

	Task DeleteWithVersionAsync(string id, string etag, CancellationToken cancellationToken = default);

	async Task<bool> DeleteIfExistsAsync(string id, CancellationToken cancellationToken = default)
	{
		try
		{
			await DeleteAsync(id, cancellationToken);
			return true;
		}
		catch (NotFoundException)
		{
			return false;
		}
	}

	async Task<bool> DeleteWithVersionIfExistsAsync(string id, string version, CancellationToken cancellationToken = default)
	{
		try
		{
			await DeleteWithVersionAsync(id, version, cancellationToken);
			return true;
		}
		catch (NotFoundException)
		{
			return false;
		}
	}

	Task<RawStreamResult?> RawStreamAsync(string id, CancellationToken cancellationToken = default);

	IAsyncEnumerable<string> ListAsync(CancellationToken cancellationToken = default);

	Task<(IReadOnlyList<string> Items, string? ContinuationToken)> ListPageAsync(string? continuationToken = null, int? pageSize = null, CancellationToken cancellationToken = default);

	Task ClearAsync(CancellationToken cancellationToken = default);

	Task<T?> ApplyAsync(string id, Func<T?, CancellationToken, ValueTask<T?>> factory, CancellationToken cancellationToken = default) => OptimisticConcurrency.Retry(async c =>
	{
		var versioned = await GetOrDefaultWithVersionAsync(id, cancellationToken);
		var transformed = await factory(versioned.HasValue ? versioned.Value.Value : default, cancellationToken);
		switch ((versioned, transformed))
		{
			// modify
			case ({ ETag: string version }, not null):
				await UpdateAsync(id, transformed, version, cancellationToken);
				break;

			// create
			case (null, not null):
				await CreateAsync(id, transformed, cancellationToken);
				break;

			// delete
			case ({ ETag: string version }, null):
				await DeleteWithVersionIfExistsAsync(id, version, cancellationToken);
				break;

			// did not exist, nothing to create
			case (null, null):
				// nothing to do
				break;
		}
		return transformed;
	}, cancellationToken);

	Task<T?> ApplyAsync(string id, Func<T?, T?> factory, CancellationToken cancellationToken = default) => OptimisticConcurrency.Retry(async c =>
	{
		var versioned = await GetOrDefaultWithVersionAsync(id, cancellationToken);
		var transformed = factory(versioned.HasValue ? versioned.Value.Value : default);
		switch ((versioned, transformed))
		{
			// modify
			case ({ ETag: string version }, not null):
				await UpdateAsync(id, transformed, version, cancellationToken);
				break;

			// create
			case (null, not null):
				await CreateAsync(id, transformed, cancellationToken);
				break;

			// delete
			case ({ ETag: string version }, null):
				await DeleteWithVersionIfExistsAsync(id, version, cancellationToken);
				break;

			// did not exist, nothing to create
			case (null, null):
				// nothing to do
				break;
		}
		return transformed;
	}, cancellationToken);
}

public readonly record struct RawStreamResult(Stream Stream, DateTimeOffset LastModified, string ETag, string? Encoding = null) : IAsyncDisposable
{
	public Stream GetDecodedStream()
	{
		if (CompressionOptions.FromContentEncoding(Encoding) is CompressionOptions compression &&
			CompressionOptions.CreateStreamCompressor(compression) is IStreamCompressor compressor)
		{
			return compressor.Decompress(Stream);
		}

		return Stream;
	}

	public ValueTask DisposeAsync()
	{
		return ((IAsyncDisposable)Stream).DisposeAsync();
	}
}
