using PeterJuhasz.Repositories.Abstractions;
using System.Collections.Concurrent;

namespace PeterJuhasz.Repositories.InMemory;

public class InMemorySingleObjectStorage<T> : ISingleObjectRepository<T>
{
	private readonly ConcurrentDictionary<string, Versioned<T>> _store = new();

	public Task ClearAsync(CancellationToken cancellationToken = default)
	{
		_store.Clear();
		return Task.CompletedTask;
	}

	public Task DeleteAsync(string id, CancellationToken cancellationToken = default)
	{
		if (_store.TryRemove(id, out _))
		{
			return Task.CompletedTask;
		}
		throw NotFoundException.Create<T, string>(id);
	}

	public Task DeleteWithVersionAsync(string id, string etag, CancellationToken cancellationToken = default)
	{
		if (_store.TryRemove(id, out var value) && value.ETag == etag)
		{
			return Task.CompletedTask;
		}
		throw new ConflictException(etag);
	}

	public Task<bool> ExistsAsync(string id, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(_store.ContainsKey(id));
	}

	public ValueTask<T> GetAsync(string id, CancellationToken cancellationToken = default)
	{
		if (_store.TryGetValue(id, out var value))
		{
			return new(value.Value);
		}
		throw NotFoundException.Create<T, string>(id);
	}

	public ValueTask<Versioned<T>?> GetOrDefaultWithVersionAsync(string id, CancellationToken cancellationToken = default)
	{
		if (_store.TryGetValue(id, out var value))
		{
			return new(value);
		}
		return new((Versioned<T>?)null);
	}

	public Task<string?> GetVersionAsync(string id, CancellationToken cancellationToken = default)
	{
		if (_store.TryGetValue(id, out var value))
		{
			return Task.FromResult<string?>(value.ETag);
		}

		return Task.FromResult<string?>(null);
	}

	public IAsyncEnumerable<string> ListAsync(CancellationToken cancellationToken = default)
	{
		return _store.Keys.ToAsyncEnumerable();
	}

	public Task<(IReadOnlyList<string> Items, string? ContinuationToken)> ListPageAsync(string? continuationToken = null, int? pageSize = null, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<(IReadOnlyList<string> Items, string? ContinuationToken)>((Items: _store.Keys.ToArray(), ContinuationToken: null));
	}

	public Task<RawStreamResult?> RawStreamAsync(string id, CancellationToken cancellationToken = default)
	{
		throw new NotImplementedException();
	}

	public Task<Versioned<T>> StoreAsync(string id, T value, string? etag = null, bool overwrite = true, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(_store.AddOrUpdate(id, _ => new Versioned<T>(value, Guid.NewGuid().ToString()), (_, old) =>
		{
			if (etag != null)
			{
				if (old.ETag != etag)
				{
					throw new ConflictException(etag);
				}
			}
			else if (!overwrite)
			{
				throw new ConflictException(id);
			}

			return new Versioned<T>(value, Guid.NewGuid().ToString());
		}));
	}
}
