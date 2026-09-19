using PeterJuhasz.Repositories.Abstractions;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace PeterJuhasz.Repositories.Caching;

public sealed class CachingOneToManyForeignKeyIndex(
	IOneToManyForeignKeyIndex inner,
	CacheOptions cacheOptions,
	TimeProvider timeProvider
) : IOneToManyForeignKeyIndex
{
	private readonly ConcurrentDictionary<string, CacheEntry> _cache = [];

	public IOneToManyForeignKeyIndex Inner => inner;

	public async Task AddAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		await inner.AddAsync(principalKey, foreignKey, cancellationToken);
		if (_cache.TryGetValue(principalKey, out var cached) && IsValid(cached))
		{
			_cache[principalKey] = CreateEntry([.. cached.ForeignKeys.Append(foreignKey).Distinct()]);
		}
	}

	public async Task<bool> AddOrUpdateAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		var added = await inner.AddOrUpdateAsync(principalKey, foreignKey, cancellationToken);
		if (!added)
		{
			return false;
		}

		if (_cache.TryGetValue(principalKey, out var cached) && IsValid(cached))
		{
			_cache[principalKey] = CreateEntry([.. cached.ForeignKeys.Append(foreignKey).Distinct()]);
		}

		return true;
	}

	public async Task<bool> ContainsAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate && _cache.TryGetValue(principalKey, out var cached) && IsValid(cached))
		{
			return cached.ForeignKeys.Contains(foreignKey);
		}

		return await inner.ContainsAsync(principalKey, foreignKey, cancellationToken);
	}

	public async IAsyncEnumerable<string> ListAsync(string principalKey, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate && _cache.TryGetValue(principalKey, out var cached) && IsValid(cached))
		{
			foreach (var foreignKey in cached.ForeignKeys)
			{
				yield return foreignKey;
			}

			yield break;
		}

		var foreignKeys = new List<string>();
		await foreach (var foreignKey in inner.ListAsync(principalKey, cancellationToken))
		{
			foreignKeys.Add(foreignKey);
			yield return foreignKey;
		}

		_cache[principalKey] = CreateEntry(foreignKeys);
	}

	public async Task DeleteAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		await inner.DeleteAsync(principalKey, foreignKey, cancellationToken);
		if (_cache.TryGetValue(principalKey, out var cached) && IsValid(cached))
		{
			_cache[principalKey] = CreateEntry([.. cached.ForeignKeys.Where(e => e != foreignKey)]);
		}
	}

	public async Task DeleteAsync(string principalKey, CancellationToken cancellationToken)
	{
		await inner.DeleteAsync(principalKey, cancellationToken);
		_cache.TryRemove(principalKey, out _);
	}

	public async Task<bool> DeleteIfExistsAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		var deleted = await inner.DeleteIfExistsAsync(principalKey, foreignKey, cancellationToken);
		if (!deleted)
		{
			return false;
		}

		if (_cache.TryGetValue(principalKey, out var cached) && IsValid(cached))
		{
			_cache[principalKey] = CreateEntry([.. cached.ForeignKeys.Where(e => e != foreignKey)]);
		}

		return true;
	}


	private bool IsValid(CacheEntry entry) => entry.ExpiresAt > timeProvider.GetUtcNow();

	private CacheEntry CreateEntry(IReadOnlyCollection<string> foreignKeys)
	{
		var expiresAt = DateTimeOffset.MaxValue;
		if (cacheOptions.SlidingExpiration is { } slidingExpiration)
		{
			expiresAt = timeProvider.GetUtcNow().Add(slidingExpiration);
		}

		return new(foreignKeys, expiresAt);
	}

	private sealed class CacheEntry(IReadOnlyCollection<string> foreignKeys, DateTimeOffset expiresAt)
	{
		public IReadOnlyCollection<string> ForeignKeys { get; } = foreignKeys;

		public DateTimeOffset ExpiresAt { get; } = expiresAt;
	}
}

public static partial class Extensions
{
	extension(IOneToManyForeignKeyIndex index)
	{
		public IOneToManyForeignKeyIndex WithCaching(CacheOptions cacheOptions) =>
			new CachingOneToManyForeignKeyIndex(index, cacheOptions, TimeProvider.System);
	}
}
