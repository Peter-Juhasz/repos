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

		var expiresAt = DateTimeOffset.MaxValue;
		if (cacheOptions.SlidingExpiration != null)
		{
			expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
		}

		_cacheEntry = new CacheEntry(result.Value.ETag, expiresAt)
		{
			Value = result.Value,
		};

		return new(result.Value, result.Value.ETag);
	}

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (_cacheEntry is { Value: not null } cached)
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

		var expiresAt = DateTimeOffset.MaxValue;
		if (cacheOptions.SlidingExpiration != null)
		{
			expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
		}

		_cacheEntry = new CacheEntry(newVersion, expiresAt);

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

		var expiresAt = DateTimeOffset.MaxValue;
		if (cacheOptions.SlidingExpiration != null)
		{
			expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
		}

		_cacheEntry = new CacheEntry(result.Value.ETag, expiresAt)
		{
			BinaryData = data,
			LastModified = result.Value.LastModified,
			ContentEncoding = result.Value.Encoding
		};

		return new(data.ToStream(), result.Value.LastModified, result.Value.ETag, result.Value.Encoding);
	}

	public async Task<Versioned<T>> StoreAsync(T value, string? etag, CancellationToken cancellationToken)
	{
		var result = await inner.StoreAsync(value, etag, cancellationToken);
		_cacheEntry = null;
		return result;
	}

	public async Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		var deleted = await inner.DeleteIfExistsAsync(cancellationToken);
		_cacheEntry = null;
		return deleted;
	}

	public async Task DeleteWithVersionAsync(string etag, CancellationToken cancellationToken)
	{
		await inner.DeleteWithVersionAsync(etag, cancellationToken);
		_cacheEntry = null;
	}


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
			if (cache.TryGetValue(cacheKey, out var cached) && cached is { Value: not null })
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

	public async Task<Versioned<T>> StoreAsync(T value, string? etag, CancellationToken cancellationToken)
	{
		var result = await inner.StoreAsync(value, etag, cancellationToken);
		cache.TryRemove(cacheKey, out _);
		return result;
	}

	public async Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		var deleted = await inner.DeleteIfExistsAsync(cancellationToken);
		cache.TryRemove(cacheKey, out _);
		return deleted;
	}

	public async Task DeleteWithVersionAsync(string etag, CancellationToken cancellationToken)
	{
		await inner.DeleteWithVersionAsync(etag, cancellationToken);
		cache.TryRemove(cacheKey, out _);
	}


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