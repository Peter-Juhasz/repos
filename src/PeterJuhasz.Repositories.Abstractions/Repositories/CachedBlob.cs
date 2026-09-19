using PeterJuhasz.Repositories.Caching;
using System.Collections.Concurrent;

namespace PeterJuhasz.Repositories.Blobs;

public sealed class CachedBlob(
	IBlob inner,
	CacheOptions cacheOptions,
	TimeProvider timeProvider
) : IBlob
{
	private CacheEntry? _cacheEntry;


	public string Name => inner.Name;

	public IBlob Blob => inner;

	public async Task<string> SetMetadataAsync(string concurrencyToken, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
	{
		var result = await inner.SetMetadataAsync(concurrencyToken, metadata, cancellationToken);
		_cacheEntry = null;
		return result;
	}

	public async Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		await inner.DeleteAsync(concurrencyToken, cancellationToken);
		_cacheEntry = null;
	}

	public async Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		var deleted = await inner.DeleteIfExistsAsync(cancellationToken);
		_cacheEntry = null;
		return deleted;
	}

	public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (_cacheEntry is { Data: not null } cached)
			{
				if (cached.ExpiresAt > timeProvider.GetUtcNow())
				{
					return true;
				}
			}
		}

		var exists = await inner.ExistsAsync(cancellationToken);
		if (!exists)
		{
			_cacheEntry = null;
		}
		return exists;
	}

	public async Task<IBlob.ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (_cacheEntry is { Data: not null } cached)
			{
				if (cached.ExpiresAt > timeProvider.GetUtcNow())
				{
					return cached.Info;
				}
			}
		}

		var info = await inner.GetInfoAsync(cancellationToken);
		if (info == null)
		{
			_cacheEntry = null;
		}
		else if (_cacheEntry?.Info.ConcurrencyToken != info.ConcurrencyToken)
		{
			var expiresAt = DateTimeOffset.MaxValue;
			if (cacheOptions.SlidingExpiration != null)
			{
				expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
			}

			_cacheEntry = new CacheEntry(info, expiresAt);
		}
		return info;
	}

	public async Task<IBlob.BlobReadResult?> ReadAsync(CancellationToken cancellationToken)
	{
		if (_cacheEntry is { Data: not null } cached)
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentInfo = await inner.GetInfoAsync(cancellationToken);
					if (currentInfo?.ConcurrencyToken == cached.Info.ConcurrencyToken)
					{
						return new(cached.Data, cached.Info);
					}
					else
					{
						_cacheEntry = null;
					}
				}
				else
				{
					return new(cached.Data, cached.Info);
				}
			}
		}

		var result = await inner.ReadAsync(cancellationToken);
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

		_cacheEntry = new CacheEntry(result.Info, expiresAt)
		{
			Data = result.Value,
		};

		return result;
	}

	public async Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken)
	{
		if (_cacheEntry is { Data: not null } cached)
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentInfo = await inner.GetInfoAsync(cancellationToken);
					if (currentInfo?.ConcurrencyToken == cached.Info.ConcurrencyToken)
					{
						return new(cached.Data.ToStream(), cached.Info);
					}
					else
					{
						_cacheEntry = null;
					}
				}
				else
				{
					return new(cached.Data.ToStream(), cached.Info);
				}
			}
		}

		var result = await inner.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			_cacheEntry = null;
			return null;
		}

		await using (result.Value)
		{
			var data = await BinaryData.FromStreamAsync(result.Value, cancellationToken);

			var expiresAt = DateTimeOffset.MaxValue;
			if (cacheOptions.SlidingExpiration != null)
			{
				expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
			}

			_cacheEntry = new CacheEntry(result.Info, expiresAt)
			{
				Data = data,
			};

			return new(data.ToStream(), result.Info);
		}
	}

	public async Task<Stream> OpenWriteAsync(string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		var stream = await inner.OpenWriteAsync(concurrencyToken, options, cancellationToken);
		_cacheEntry = null;
		return stream;
	}

	public async Task<string> WriteAsync(ReadOnlyMemory<byte> data, string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		var newConcurrencyToken = await inner.WriteAsync(data, concurrencyToken, options, cancellationToken);
		_cacheEntry = null;
		return newConcurrencyToken;
	}


	private sealed class CacheEntry(IBlob.ReadBlobInfo info, DateTimeOffset expiresAt)
	{
		public BinaryData? Data { get; set; }
		public IBlob.ReadBlobInfo Info { get; } = info;
		public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
	}
}

public sealed class GlobalCachedBlob(
	IBlob inner,
	CacheOptions cacheOptions,
	TimeProvider timeProvider
) : IBlob
{
	private static readonly ConcurrentDictionary<string, CacheEntry> cache = new();


	public string Name => inner.Name;

	private string CacheKey => Name;

	public async Task<string> SetMetadataAsync(string concurrencyToken, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
	{
		var result = await inner.SetMetadataAsync(concurrencyToken, metadata, cancellationToken);
		cache.TryRemove(CacheKey, out _);
		return result;
	}

	public async Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		await inner.DeleteAsync(concurrencyToken, cancellationToken);
		cache.TryRemove(CacheKey, out _);
	}

	public async Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		var deleted = await inner.DeleteIfExistsAsync(cancellationToken);
		cache.TryRemove(CacheKey, out _);
		return deleted;
	}

	public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (cache.TryGetValue(CacheKey, out var cached) && cached is { Data: not null })
			{
				if (cached.ExpiresAt > timeProvider.GetUtcNow())
				{
					return true;
				}
			}
		}

		var exists = await inner.ExistsAsync(cancellationToken);
		if (!exists)
		{
			cache.TryRemove(CacheKey, out _);
		}
		return exists;
	}

	public async Task<IBlob.ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate)
		{
			if (cache.TryGetValue(CacheKey, out var cached) && cached is { Data: not null })
			{
				if (cached.ExpiresAt > timeProvider.GetUtcNow())
				{
					return cached.Info;
				}
			}
		}

		var info = await inner.GetInfoAsync(cancellationToken);
		if (info == null)
		{
			cache.TryRemove(CacheKey, out _);
		}
		else if (cache.TryGetValue(CacheKey, out var cached) && cached.Info.ConcurrencyToken != info.ConcurrencyToken)
		{
			var expiresAt = DateTimeOffset.MaxValue;
			if (cacheOptions.SlidingExpiration != null)
			{
				expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
			}

			cache[CacheKey] = new CacheEntry(info, expiresAt);
		}
		return info;
	}

	public async Task<IBlob.BlobReadResult?> ReadAsync(CancellationToken cancellationToken)
	{
		if (cache.TryGetValue(CacheKey, out var cached) && cached is { Data: not null })
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentInfo = await inner.GetInfoAsync(cancellationToken);
					if (currentInfo?.ConcurrencyToken == cached.Info.ConcurrencyToken)
					{
						return new(cached.Data, cached.Info);
					}
					else
					{
						cache.TryRemove(CacheKey, out _);
					}
				}
				else
				{
					return new(cached.Data, cached.Info);
				}
			}
		}

		var result = await inner.ReadAsync(cancellationToken);
		if (result == null)
		{
			cache.TryRemove(CacheKey, out _);
			return null;
		}

		var expiresAt = DateTimeOffset.MaxValue;
		if (cacheOptions.SlidingExpiration != null)
		{
			expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
		}

		cache.AddOrUpdate(CacheKey, _ => new CacheEntry(result.Info, expiresAt)
		{
			Data = result.Value,
		}, (_, existing) =>
		{
			if (existing.Info.ConcurrencyToken != result.Info.ConcurrencyToken ||
				existing.ExpiresAt <= timeProvider.GetUtcNow()
			)
			{
				return new CacheEntry(result.Info, expiresAt)
				{
					Data = result.Value,
				};
			}
			else
			{
				if (existing.Data == null)
				{
					existing.Data = result.Value;
				}
				return existing;
			}
		});

		return result;
	}

	public async Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken)
	{
		if (cache.TryGetValue(CacheKey, out var cached) && cached is { Data: not null })
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentInfo = await inner.GetInfoAsync(cancellationToken);
					if (currentInfo?.ConcurrencyToken == cached.Info.ConcurrencyToken)
					{
						return new(cached.Data.ToStream(), cached.Info);
					}
					else
					{
						cache.TryRemove(CacheKey, out _);
					}
				}
				else
				{
					return new(cached.Data.ToStream(), cached.Info);
				}
			}
		}

		var result = await inner.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			cache.TryRemove(CacheKey, out _);
			return null;
		}

		await using (result.Value)
		{
			var data = await BinaryData.FromStreamAsync(result.Value, cancellationToken);

			var expiresAt = DateTimeOffset.MaxValue;
			if (cacheOptions.SlidingExpiration != null)
			{
				expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
			}

			cache.AddOrUpdate(CacheKey, _ => new CacheEntry(result.Info, expiresAt)
			{
				Data = data,
			}, (_, existing) =>
			{
				if (existing.Info.ConcurrencyToken != result.Info.ConcurrencyToken ||
					existing.ExpiresAt <= timeProvider.GetUtcNow()
				)
				{
					return new CacheEntry(result.Info, expiresAt)
					{
						Data = data,
					};
				}
				else
				{
					if (existing.Data == null)
					{
						existing.Data = data;
					}
					return existing;
				}
			});

			return new(data.ToStream(), result.Info);
		}
	}

	public async Task<Stream> OpenWriteAsync(string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		var stream = await inner.OpenWriteAsync(concurrencyToken, options, cancellationToken);
		cache.TryRemove(CacheKey, out _);
		return stream;
	}

	public async Task<string> WriteAsync(ReadOnlyMemory<byte> data, string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		var newConcurrencyToken = await inner.WriteAsync(data, concurrencyToken, options, cancellationToken);
		cache.TryRemove(CacheKey, out _);
		return newConcurrencyToken;
	}


	private sealed class CacheEntry(IBlob.ReadBlobInfo info, DateTimeOffset expiresAt)
	{
		public BinaryData? Data { get; set; }
		public IBlob.ReadBlobInfo Info { get; } = info;
		public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
	}
}

public static partial class Extensions
{
	extension(IBlob blob)
	{
		public IBlob WithCaching(CacheOptions cacheOptions) => new CachedBlob(blob, cacheOptions, TimeProvider.System);
	}
}