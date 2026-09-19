using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using System.Collections.Concurrent;

namespace PeterJuhasz.Repositories.Caching;

public class CachingBinaryRepository(
	IBinaryRepository inner,
	CacheOptions cacheOptions,
	TimeProvider timeProvider
) : IBinaryRepository
{
	private CacheEntry? _cacheEntry;

	public IBinaryRepository Inner => inner;

	public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (_cacheEntry is { Data: not null } cached && cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				return true;
			}
		}

		var exists = await inner.ExistsAsync(cancellationToken);
		if (!exists)
		{
			_cacheEntry = null;
		}
		return exists;
	}

	public async Task<BinaryData?> GetAsync(CancellationToken cancellationToken)
	{
		if (_cacheEntry is { Data: not null } cached && cached.ExpiresAt > timeProvider.GetUtcNow())
		{
			if (cacheOptions.MustRevalidate)
			{
				var currentVersion = await inner.GetVersionAsync(cancellationToken);
				if (currentVersion == cached.Version)
				{
					return cached.Data;
				}

				_cacheEntry = null;
			}
			else
			{
				return cached.Data;
			}
		}

		var data = await inner.GetAsync(cancellationToken);
		if (data == null)
		{
			_cacheEntry = null;
			return null;
		}

		var version = await inner.GetVersionAsync(cancellationToken);
		_cacheEntry = new(data, version, GetExpiresAt());
		return data;
	}

	public async Task<Stream?> GetStreamAsync(CancellationToken cancellationToken)
	{
		if (_cacheEntry is { Data: not null } cached && cached.ExpiresAt > timeProvider.GetUtcNow())
		{
			if (cacheOptions.MustRevalidate)
			{
				var currentVersion = await inner.GetVersionAsync(cancellationToken);
				if (currentVersion == cached.Version)
				{
					return cached.Data.ToStream();
				}

				_cacheEntry = null;
			}
			else
			{
				return cached.Data.ToStream();
			}
		}

		await using var stream = await inner.GetStreamAsync(cancellationToken);
		if (stream == null)
		{
			_cacheEntry = null;
			return null;
		}

		var data = await BinaryData.FromStreamAsync(stream, cancellationToken);
		var version = await inner.GetVersionAsync(cancellationToken);
		_cacheEntry = new(data, version, GetExpiresAt());
		return data.ToStream();
	}

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (_cacheEntry is { Version: not null } cached && cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				return cached.Version;
			}
		}

		var newVersion = await inner.GetVersionAsync(cancellationToken);
		if (newVersion == null)
		{
			_cacheEntry = null;
			return null;
		}

		if (_cacheEntry is { Data: not null } existing && existing.ExpiresAt > timeProvider.GetUtcNow())
		{
			_cacheEntry = new(existing.Data, newVersion, GetExpiresAt());
		}
		else
		{
			_cacheEntry = new(null, newVersion, GetExpiresAt());
		}

		return newVersion;
	}

	public async Task StoreAsync(BinaryData value, string? concurrencyToken, CancellationToken cancellationToken)
	{
		await inner.StoreAsync(value, concurrencyToken, cancellationToken);
		_cacheEntry = null;
	}

	public async Task StoreAsync(Stream stream, string? mediaType, string? concurrencyToken, CancellationToken cancellationToken)
	{
		await inner.StoreAsync(stream, mediaType, concurrencyToken, cancellationToken);
		_cacheEntry = null;
	}

	public async Task DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		await inner.DeleteIfExistsAsync(cancellationToken);
		_cacheEntry = null;
	}

	public async Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		await inner.DeleteAsync(concurrencyToken, cancellationToken);
		_cacheEntry = null;
	}

	private DateTimeOffset GetExpiresAt() => cacheOptions.SlidingExpiration switch
	{
		TimeSpan slidingExpiration => timeProvider.GetUtcNow().Add(slidingExpiration),
		_ => DateTimeOffset.MaxValue,
	};

	private sealed record class CacheEntry(BinaryData? Data, string? Version, DateTimeOffset ExpiresAt);
}

public class GlobalCachingBlobBinaryRepository(
	IBinaryRepository inner,
	string cacheKey,
	CacheOptions cacheOptions,
	TimeProvider timeProvider
) : IBinaryRepository
{
	private static readonly ConcurrentDictionary<string, CacheEntry> cache = new();

	public IBinaryRepository Inner => inner;

	public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (cache.TryGetValue(cacheKey, out var cached) && cached is { Data: not null } && cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				return true;
			}
		}

		var exists = await inner.ExistsAsync(cancellationToken);
		if (!exists)
		{
			cache.TryRemove(cacheKey, out _);
		}
		return exists;
	}

	public async Task<BinaryData?> GetAsync(CancellationToken cancellationToken)
	{
		if (cache.TryGetValue(cacheKey, out var cached) && cached is { Data: not null } && cached.ExpiresAt > timeProvider.GetUtcNow())
		{
			if (cacheOptions.MustRevalidate)
			{
				var currentVersion = await inner.GetVersionAsync(cancellationToken);
				if (currentVersion == cached.Version)
				{
					return cached.Data;
				}

				cache.TryRemove(cacheKey, out _);
			}
			else
			{
				return cached.Data;
			}
		}

		var data = await inner.GetAsync(cancellationToken);
		if (data == null)
		{
			cache.TryRemove(cacheKey, out _);
			return null;
		}

		var version = await inner.GetVersionAsync(cancellationToken);
		cache[cacheKey] = new(data, version, GetExpiresAt());
		return data;
	}

	public async Task<Stream?> GetStreamAsync(CancellationToken cancellationToken)
	{
		if (cache.TryGetValue(cacheKey, out var cached) && cached is { Data: not null } && cached.ExpiresAt > timeProvider.GetUtcNow())
		{
			if (cacheOptions.MustRevalidate)
			{
				var currentVersion = await inner.GetVersionAsync(cancellationToken);
				if (currentVersion == cached.Version)
				{
					return cached.Data.ToStream();
				}

				cache.TryRemove(cacheKey, out _);
			}
			else
			{
				return cached.Data.ToStream();
			}
		}

		await using var stream = await inner.GetStreamAsync(cancellationToken);
		if (stream == null)
		{
			cache.TryRemove(cacheKey, out _);
			return null;
		}

		var data = await BinaryData.FromStreamAsync(stream, cancellationToken);
		var version = await inner.GetVersionAsync(cancellationToken);
		cache[cacheKey] = new(data, version, GetExpiresAt());
		return data.ToStream();
	}

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (cache.TryGetValue(cacheKey, out var cached) && cached is { Version: not null } && cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				return cached.Version;
			}
		}

		var newVersion = await inner.GetVersionAsync(cancellationToken);
		if (newVersion == null)
		{
			cache.TryRemove(cacheKey, out _);
			return null;
		}

		cache.AddOrUpdate(cacheKey,
		_ => new(null, newVersion, GetExpiresAt()),
		(_, existing) => new(existing.Data, newVersion, GetExpiresAt())
		);

		return newVersion;
	}

	public async Task StoreAsync(BinaryData value, string? concurrencyToken, CancellationToken cancellationToken)
	{
		await inner.StoreAsync(value, concurrencyToken, cancellationToken);
		cache.TryRemove(cacheKey, out _);
	}

	public async Task StoreAsync(Stream stream, string? mediaType, string? concurrencyToken, CancellationToken cancellationToken)
	{
		await inner.StoreAsync(stream, mediaType, concurrencyToken, cancellationToken);
		cache.TryRemove(cacheKey, out _);
	}

	public async Task DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		await inner.DeleteIfExistsAsync(cancellationToken);
		cache.TryRemove(cacheKey, out _);
	}

	public async Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		await inner.DeleteAsync(concurrencyToken, cancellationToken);
		cache.TryRemove(cacheKey, out _);
	}

	private DateTimeOffset GetExpiresAt() => cacheOptions.SlidingExpiration switch
	{
		TimeSpan slidingExpiration => timeProvider.GetUtcNow().Add(slidingExpiration),
		_ => DateTimeOffset.MaxValue,
	};

	private sealed record class CacheEntry(BinaryData? Data, string? Version, DateTimeOffset ExpiresAt);
}

public static partial class Extensions
{
	extension(IBinaryRepository repository)
	{
		public IBinaryRepository WithCaching(CacheOptions cacheOptions)
		{
			if (repository is BlobBinaryRepository blobRepository)
			{
				return new GlobalCachingBlobBinaryRepository(blobRepository, blobRepository.Blob.Name, cacheOptions, TimeProvider.System);
			}

			return new CachingBinaryRepository(repository, cacheOptions, TimeProvider.System);
		}
	}
}
