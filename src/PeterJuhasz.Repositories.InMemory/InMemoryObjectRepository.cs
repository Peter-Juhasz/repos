using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.InMemory;

public sealed class InMemoryObjectRepository<T> : IObjectRepository<T>
	where T : class
{
	private readonly Lock _lock = new();
	private Versioned<T>? _stored;

	public Task<bool> ExistsAsync(CancellationToken cancellationToken)
	{
		if (_stored.HasValue)
		{
			return SpecializedTasks.True;
		}
		else
		{
			return SpecializedTasks.False;
		}
	}

	public Task DeleteWithVersionAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		lock (_lock)
		{
			if (_stored is not { } current)
			{
				// a specific version of a missing object is a conflict, so optimistic retries can recover
				if (concurrencyToken != IBlob.AnyConcurrencyToken)
				{
					throw new ConflictException(concurrencyToken);
				}

				throw new NotFoundException("Object not found.");
			}

			if (concurrencyToken != IBlob.AnyConcurrencyToken && current.ETag != concurrencyToken)
			{
				throw new ConflictException(concurrencyToken);
			}

			_stored = null;
			return Task.CompletedTask;
		}
	}

	public ValueTask<Versioned<T>?> GetOrDefaultWithVersionAsync(CancellationToken cancellationToken)
	{
		lock (_lock)
		{
			return new(_stored);
		}
	}

	public Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		lock (_lock)
		{
			if (_stored.HasValue)
			{
				return Task.FromResult<string?>(_stored.Value.ETag);
			}
			else
			{
				return SpecializedTasks.Null<string>();
			}
		}
	}

	public Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken)
	{
		throw new NotImplementedException();
	}

	public Task<Versioned<T>> StoreAsync(T value, string? concurrencyToken, CancellationToken cancellationToken)
	{
		lock (_lock)
		{
			var isMet = concurrencyToken switch
			{
				null => _stored is null,
				IBlob.AnyOrNoneConcurrencyToken => true,
				IBlob.AnyConcurrencyToken => _stored is not null,
				_ => _stored?.ETag == concurrencyToken,
			};
			if (!isMet)
			{
				throw new ConflictException(concurrencyToken ?? "Object already exists.");
			}

			var result = new Versioned<T>(value, Guid.NewGuid().ToString());
			_stored = result;
			return Task.FromResult(result);
		}
	}
}
