using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using System.Collections.Concurrent;

namespace PeterJuhasz.Repositories.Caching;

public class CachingObjectRepository<T>(
	IObjectRepository<T> inner,
	CacheOptions cacheOptions,
	TimeProvider timeProvider
)
	: IObjectRepository<T>
{
	private CacheEntry? _cacheEntry;

	public IObjectRepository<T> Inner => inner;

	public async Task<bool> ExistsAsync(CancellationToken cancellationToken) =>
		await GetVersionAsync(cancellationToken) != null;

	public async ValueTask<Versioned<T>?> GetOrDefaultWithVersionAsync(CancellationToken cancellationToken)
	{
		if (_cacheEntry is { Value: not null } cached)
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentInfo = await inner.GetVersionAsync(cancellationToken);
					if (currentInfo == cached.Version)
					{
						return new(cached.Value, cached.Version);
					}
					else
					{
						_cacheEntry = null;
					}
				}
				else
				{
					return new(cached.Value, cached.Version);
				}
			}
		}

		var result = await inner.GetOrDefaultWithVersionAsync(cancellationToken);
		if (result == null)
		{
			_cacheEntry = null;
			return null;
		}

		_cacheEntry = GetEntry(result.Value.ETag) with
		{
			Value = result.Value.Value,
		};

		return result;
	}

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (_cacheEntry is { } cached)
			{
				if (cached.ExpiresAt > timeProvider.GetUtcNow())
				{
					return cached.Version;
				}
			}
		}

		var newVersion = await inner.GetVersionAsync(cancellationToken);
		if (newVersion == null)
		{
			_cacheEntry = null;
			return null;
		}

		_cacheEntry = GetEntry(newVersion);

		return newVersion;
	}

	public async Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken)
	{
		if (_cacheEntry is { BinaryData: not null } cached)
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentInfo = await inner.GetVersionAsync(cancellationToken);
					if (currentInfo == cached.Version)
					{
						return new(cached.BinaryData.ToStream(), cached.LastModified!.Value, cached.Version, cached.ContentEncoding);
					}
					else
					{
						_cacheEntry = null;
					}
				}
				else
				{
					return new(cached.BinaryData.ToStream(), cached.LastModified!.Value, cached.Version, cached.ContentEncoding);
				}
			}
		}

		await using var result = await inner.RawStreamAsync(cancellationToken);
		if (result == null)
		{
			_cacheEntry = null;
			return null;
		}

		var data = await BinaryData.FromStreamAsync(result.Value.Stream, cancellationToken);

		_cacheEntry = GetEntry(result.Value.ETag) with
		{
			BinaryData = data,
			LastModified = result.Value.LastModified,
			ContentEncoding = result.Value.Encoding
		};

		return new(data.ToStream(), result.Value.LastModified, result.Value.ETag, result.Value.Encoding);
	}

	/// <summary>
	/// Returns the current entry if it is still fresh and has the same version, so the value and the raw content of a version are cached together.
	/// </summary>
	private CacheEntry GetEntry(string version)
	{
		if (_cacheEntry is { } current && current.Version == version && current.ExpiresAt > timeProvider.GetUtcNow())
		{
			return current;
		}

		var expiresAt = DateTimeOffset.MaxValue;
		if (cacheOptions.SlidingExpiration != null)
		{
			expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
		}

		return new(version, expiresAt);
	}


	// immutable, so concurrent readers never observe a partially updated entry
	private sealed record class CacheEntry(string Version, DateTimeOffset ExpiresAt)
	{
		public T? Value { get; init; }

		public BinaryData? BinaryData { get; init; }

		public DateTimeOffset? LastModified { get; init; }

		public string? ContentEncoding { get; init; }
	}


	#region Forwarded writes

	// writes invalidate even when they fail, e.g. a conflict means the cached version is stale

	public async Task<Versioned<T>> StoreAsync(T value, string? etag, CancellationToken cancellationToken)
	{
		try
		{
			return await inner.StoreAsync(value, etag, cancellationToken);
		}
		finally
		{
			_cacheEntry = null;
		}
	}

	// forwarded instead of the default implementation, so it reads the current version instead of the cache, and the inner implementation is used (e.g. comparer, metadata)
	public async Task<T?> ApplyAsync(Func<T?, CancellationToken, ValueTask<T?>> factory, CancellationToken cancellationToken)
	{
		try
		{
			return await inner.ApplyAsync(factory, cancellationToken);
		}
		finally
		{
			_cacheEntry = null;
		}
	}

	public async Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		try
		{
			return await inner.DeleteIfExistsAsync(cancellationToken);
		}
		finally
		{
			_cacheEntry = null;
		}
	}

	public async Task DeleteWithVersionAsync(string etag, CancellationToken cancellationToken)
	{
		try
		{
			await inner.DeleteWithVersionAsync(etag, cancellationToken);
		}
		finally
		{
			_cacheEntry = null;
		}
	}

	#endregion
}

public class GlobalCachingBlobSingleObjectRepository<T>(
	IObjectRepository<T> inner,
	string cacheKey,
	CacheOptions cacheOptions,
	TimeProvider timeProvider
)
	: IObjectRepository<T>
{
	private static readonly ConcurrentDictionary<string, CacheEntry> cache = new();

	public IObjectRepository<T> Inner => inner;

	public async Task<bool> ExistsAsync(CancellationToken cancellationToken) =>
		await GetVersionAsync(cancellationToken) != null;

	public async ValueTask<Versioned<T>?> GetOrDefaultWithVersionAsync(CancellationToken cancellationToken)
	{
		if (cache.TryGetValue(cacheKey, out var cached) && cached is { Value: not null })
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentInfo = await inner.GetVersionAsync(cancellationToken);
					if (currentInfo == cached.Version)
					{
						return new(cached.Value, cached.Version);
					}
					else
					{
						cache.TryRemove(cacheKey, out _);
					}
				}
				else
				{
					return new(cached.Value, cached.Version);
				}
			}
		}

		var result = await inner.GetOrDefaultWithVersionAsync(cancellationToken);
		if (result == null)
		{
			cache.TryRemove(cacheKey, out _);
			return null;
		}

		var expiresAt = DateTimeOffset.MaxValue;
		if (cacheOptions.SlidingExpiration != null)
		{
			expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
		}

		cache.AddOrUpdate(cacheKey,
			addValueFactory: _ => new CacheEntry(result.Value.ETag, expiresAt)
			{
				Value = result.Value,
			},
			updateValueFactory: (_, existing) =>
			{
				if (existing.Version != result.Value.ETag ||
					existing.ExpiresAt < timeProvider.GetUtcNow()
				)
				{
					return new CacheEntry(result.Value.ETag, expiresAt)
					{
						Value = result.Value,
					};
				}
				else
				{
					if (existing.Value == null)
					{
						existing.Value = result.Value;
					}
					return existing;
				}
			}
		);

		return new(result.Value, result.Value.ETag);
	}

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (cache.TryGetValue(cacheKey, out var cached))
			{
				if (cached.ExpiresAt > timeProvider.GetUtcNow())
				{
					return cached.Version;
				}
			}
		}

		var newVersion = await inner.GetVersionAsync(cancellationToken);
		if (newVersion == null)
		{
			cache.TryRemove(cacheKey, out _);
			return null;
		}

		var expiresAt = DateTimeOffset.MaxValue;
		if (cacheOptions.SlidingExpiration != null)
		{
			expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
		}

		cache.AddOrUpdate(cacheKey,
			addValueFactory: _ => new CacheEntry(newVersion, expiresAt),
			updateValueFactory: (_, existing) =>
			{
				if (existing.Version != newVersion || existing.ExpiresAt < timeProvider.GetUtcNow())
				{
					return new CacheEntry(newVersion, expiresAt);
				}
				else
				{
					return existing;
				}
			}
		);

		return newVersion;
	}

	public async Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken)
	{
		if (cache.TryGetValue(cacheKey, out var cached) && cached is { BinaryData: not null })
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentInfo = await inner.GetVersionAsync(cancellationToken);
					if (currentInfo == cached.Version)
					{
						return new(cached.BinaryData.ToStream(), cached.LastModified!.Value, cached.Version, cached.ContentEncoding);
					}
					else
					{
						cache.TryRemove(cacheKey, out _);
					}
				}
				else
				{
					return new(cached.BinaryData.ToStream(), cached.LastModified!.Value, cached.Version, cached.ContentEncoding);
				}
			}
		}

		await using var result = await inner.RawStreamAsync(cancellationToken);
		if (result == null)
		{
			cache.TryRemove(cacheKey, out _);
			return null;
		}

		var data = await BinaryData.FromStreamAsync(result.Value.Stream, cancellationToken);

		var expiresAt = DateTimeOffset.MaxValue;
		if (cacheOptions.SlidingExpiration != null)
		{
			expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
		}

		cache.AddOrUpdate(cacheKey,
			addValueFactory: _ => new CacheEntry(result.Value.ETag, expiresAt)
			{
				BinaryData = data,
				LastModified = result.Value.LastModified,
				ContentEncoding = result.Value.Encoding
			},
			updateValueFactory: (_, existing) =>
			{
				if (existing.Version != result.Value.ETag || existing.ExpiresAt < timeProvider.GetUtcNow())
				{
					return new CacheEntry(result.Value.ETag, expiresAt)
					{
						BinaryData = data,
						LastModified = result.Value.LastModified,
						ContentEncoding = result.Value.Encoding
					};
				}
				else
				{
					if (existing.BinaryData == null)
					{
						existing.BinaryData = data;
						existing.LastModified = result.Value.LastModified;
						existing.ContentEncoding = result.Value.Encoding;
					}
					return existing;
				}
			}
		);

		return new(data.ToStream(), result.Value.LastModified, result.Value.ETag, result.Value.Encoding);
	}

	#region Forwarded writes

	// writes invalidate even when they fail, e.g. a conflict means the cached version is stale

	public async Task<Versioned<T>> StoreAsync(T value, string? etag, CancellationToken cancellationToken)
	{
		try
		{
			return await inner.StoreAsync(value, etag, cancellationToken);
		}
		finally
		{
			cache.TryRemove(cacheKey, out _);
		}
	}

	// forwarded instead of the default implementation, so it reads the current version instead of the cache, and the inner implementation is used (e.g. comparer, metadata)
	public async Task<T?> ApplyAsync(Func<T?, CancellationToken, ValueTask<T?>> factory, CancellationToken cancellationToken)
	{
		try
		{
			return await inner.ApplyAsync(factory, cancellationToken);
		}
		finally
		{
			cache.TryRemove(cacheKey, out _);
		}
	}

	public async Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		try
		{
			return await inner.DeleteIfExistsAsync(cancellationToken);
		}
		finally
		{
			cache.TryRemove(cacheKey, out _);
		}
	}

	public async Task DeleteWithVersionAsync(string etag, CancellationToken cancellationToken)
	{
		try
		{
			await inner.DeleteWithVersionAsync(etag, cancellationToken);
		}
		finally
		{
			cache.TryRemove(cacheKey, out _);
		}
	}

	#endregion


	private sealed class CacheEntry(string version, DateTimeOffset expiresAt)
	{
		public string Version { get; } = version;

		public DateTimeOffset ExpiresAt { get; } = expiresAt;

		public T? Value { get; set; }

		public BinaryData? BinaryData { get; set; }

		public DateTimeOffset? LastModified { get; set; }

		public string? ContentEncoding { get; set; }
	}
}

public static partial class Extensions
{
	extension<T>(IObjectRepository<T> repository) where T : class
	{
		public IObjectRepository<T> WithCaching(CacheOptions cacheOptions)
		{
			if (repository is BlobObjectRepository<T> blobRepository)
			{
				return new GlobalCachingBlobSingleObjectRepository<T>(blobRepository, blobRepository.Blob.Name, cacheOptions, TimeProvider.System);
			}

			return new CachingObjectRepository<T>(repository, cacheOptions, TimeProvider.System);
		}
	}
}