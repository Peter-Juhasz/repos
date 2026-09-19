using PeterJuhasz.Repositories.Abstractions;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace PeterJuhasz.Repositories.Caching;

public class CachingCollectionRepository<T>(
	ICollectionRepository<T> inner,
	CacheOptions cacheOptions,
	TimeProvider timeProvider
) : ICollectionRepository<T>
{
	private CacheEntry? _cacheEntry;

	public ICollectionRepository<T> Inner => inner;

	public Task ApplyAsync(Func<IImmutableList<T>?, CancellationToken, ValueTask<IReadOnlyCollection<T>?>> update, CancellationToken cancellationToken) =>
		inner.ApplyAsync(update, cancellationToken);

	public async IAsyncEnumerable<T> AsAsyncEnumerableAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var result = await ListWithVersionAsync(cancellationToken);
		if (result.Value is not { Count: > 0 })
		{
			yield break;
		}

		foreach (var item in result.Value)
		{
			yield return item;
		}
	}

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		if (_cacheEntry is { } cached)
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (!cacheOptions.MustRevalidate)
				{
					return cached.Version;
				}
			}
		}

		var currentVersion = await inner.GetVersionAsync(cancellationToken);
		if (currentVersion == null)
		{
			_cacheEntry = null;
			return null;
		}

		if (_cacheEntry?.Version != currentVersion)
		{
			var expiresAt = DateTimeOffset.MaxValue;
			if (cacheOptions.SlidingExpiration != null)
			{
				expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
			}

			_cacheEntry = new(currentVersion, expiresAt);
		}

		return currentVersion;
	}

	public async Task<Versioned<IReadOnlyCollection<T>>> ListWithVersionAsync(CancellationToken cancellationToken)
	{
		if (_cacheEntry is { Items: not null } cached)
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentVersion = await inner.GetVersionAsync(cancellationToken);
					if (currentVersion == cached.Version)
					{
						return new(cached.Items, cached.Version);
					}
				}
				else
				{
					return new(cached.Items, cached.Version);
				}
			}
		}

		var newResult = await inner.ListWithVersionAsync(cancellationToken);
		if (newResult.ETag == null)
		{
			_cacheEntry = null;
			return newResult;
		}

		if (_cacheEntry is { } currentEntry && currentEntry.Version == newResult.ETag && currentEntry.ExpiresAt > timeProvider.GetUtcNow())
		{
			if (currentEntry.Items == null)
			{
				currentEntry.Items = newResult.Value;
			}
		}
		else
		{
			var expiresAt = DateTimeOffset.MaxValue;
			if (cacheOptions.SlidingExpiration != null)
			{
				expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
			}

			_cacheEntry = new CacheEntry(newResult.ETag, expiresAt)
			{
				Items = newResult.Value,
			};
		}

		return newResult;
	}

	public async Task<int> CountAsync(CancellationToken cancellationToken)
	{
		if (_cacheEntry is { Items: not null } cached)
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentVersion = await inner.GetVersionAsync(cancellationToken);
					if (currentVersion == cached.Version)
					{
						return cached.Items.Count;
					}
				}
				else
				{
					return cached.Items.Count;
				}
			}
		}

		var list = await ListWithVersionAsync(cancellationToken);
		return list.Value.Count;
	}

	public async Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken)
	{
		if (_cacheEntry is { Data: not null } cached)
		{
			if (cached.ExpiresAt > timeProvider.GetUtcNow())
			{
				if (cacheOptions.MustRevalidate)
				{
					var currentVersion = await inner.GetVersionAsync(cancellationToken);
					if (currentVersion == cached.Version)
					{
						return new(cached.Data.ToStream(), cached.LastModified!.Value, cached.Version, cached.ContentEncoding);
					}
				}
				else
				{
					return new(cached.Data.ToStream(), cached.LastModified!.Value, cached.Version, cached.ContentEncoding);
				}
			}
		}

		var newResult = await inner.RawStreamAsync(cancellationToken);
		if (newResult == null)
		{
			_cacheEntry = null;
			return null;
		}

		var binaryData = await BinaryData.FromStreamAsync(newResult.Value.Stream, cancellationToken);
		if (_cacheEntry is { } currentEntry && currentEntry.Version == newResult.Value.ETag && currentEntry.ExpiresAt > timeProvider.GetUtcNow())
		{
			currentEntry.Data = binaryData;
			currentEntry.LastModified = newResult.Value.LastModified;
			currentEntry.ContentEncoding = newResult.Value.Encoding;
		}
		else
		{
			var expiresAt = DateTimeOffset.MaxValue;
			if (cacheOptions.SlidingExpiration != null)
			{
				expiresAt = timeProvider.GetUtcNow().Add(cacheOptions.SlidingExpiration.Value);
			}

			_cacheEntry = new CacheEntry(newResult.Value.ETag, expiresAt)
			{
				Data = binaryData,
				LastModified = newResult.Value.LastModified,
				ContentEncoding = newResult.Value.Encoding,
			};
		}

		return newResult;
	}

	public async Task ClearAsync(CancellationToken cancellationToken)
	{
		await inner.ClearAsync(cancellationToken);
		_cacheEntry = null;
	}


	private sealed class CacheEntry(string version, DateTimeOffset expiresAt)
	{
		public string Version { get; } = version;

		public DateTimeOffset ExpiresAt { get; } = expiresAt;

		public IReadOnlyCollection<T>? Items { get; set; }

		public BinaryData? Data { get; set; }

		public DateTimeOffset? LastModified { get; set; }

		public string? ContentEncoding { get; set; }
	}
}

public static partial class Extensions
{
	extension<T>(ICollectionRepository<T> repository)
	{
		public ICollectionRepository<T> WithCaching(CacheOptions cacheOptions) => new CachingCollectionRepository<T>(repository, cacheOptions, TimeProvider.System);
	}
}