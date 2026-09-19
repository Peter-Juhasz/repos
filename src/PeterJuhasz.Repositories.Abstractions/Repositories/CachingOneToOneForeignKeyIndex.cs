using PeterJuhasz.Repositories.Abstractions;
using System.Collections.Concurrent;

namespace PeterJuhasz.Repositories.Caching;

public sealed class CachingOneToOneForeignKeyIndex(
	IOneToOneForeignKeyIndex inner,
	CacheOptions cacheOptions,
	TimeProvider timeProvider
) : IOneToOneForeignKeyIndex
{
	private readonly ConcurrentDictionary<string, CacheEntry> _cache = [];

	public IOneToOneForeignKeyIndex Inner => inner;

	public async Task AddAsync(string foreignKey, string principalKey, CancellationToken cancellationToken)
	{
		await inner.AddAsync(foreignKey, principalKey, cancellationToken);
		_cache[foreignKey] = CreateEntry(principalKey);
	}

	public async Task<bool> AddOrUpdateAsync(string foreignKey, string principalKey, CancellationToken cancellationToken)
	{
		var added = await inner.AddOrUpdateAsync(foreignKey, principalKey, cancellationToken);
		_cache[foreignKey] = CreateEntry(principalKey);
		return added;
	}

	public async Task<string?> GetOrDefaultAsync(string foreignKey, CancellationToken cancellationToken)
	{
		if (!cacheOptions.MustRevalidate && _cache.TryGetValue(foreignKey, out var cached) && IsValid(cached))
		{
			return cached.PrincipalKey;
		}

		var principalKey = await inner.GetOrDefaultAsync(foreignKey, cancellationToken);
		_cache[foreignKey] = CreateEntry(principalKey);
		return principalKey;
	}

	public async Task DeleteAsync(string foreignKey, CancellationToken cancellationToken)
	{
		await inner.DeleteAsync(foreignKey, cancellationToken);
		_cache.TryRemove(foreignKey, out _);
	}

	public async Task<bool> DeleteIfExistsAsync(string foreignKey, CancellationToken cancellationToken)
	{
		var deleted = await inner.DeleteIfExistsAsync(foreignKey, cancellationToken);
		if (deleted)
		{
			_cache.TryRemove(foreignKey, out _);
		}

		return deleted;
	}


	private bool IsValid(CacheEntry entry) => entry.ExpiresAt > timeProvider.GetUtcNow();

	private CacheEntry CreateEntry(string? principalKey)
	{
		var expiresAt = DateTimeOffset.MaxValue;
		if (cacheOptions.SlidingExpiration is { } slidingExpiration)
		{
			expiresAt = timeProvider.GetUtcNow().Add(slidingExpiration);
		}

		return new(principalKey, expiresAt);
	}

	private sealed class CacheEntry(string? principalKey, DateTimeOffset expiresAt)
	{
		public string? PrincipalKey { get; } = principalKey;

		public DateTimeOffset ExpiresAt { get; } = expiresAt;
	}
}

public static partial class Extensions
{
	extension(IOneToOneForeignKeyIndex index)
	{
		public IOneToOneForeignKeyIndex WithCaching(CacheOptions cacheOptions) =>
			new CachingOneToOneForeignKeyIndex(index, cacheOptions, TimeProvider.System);
	}
}
