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

	public Task DeleteWithVersionAsync(string etag, CancellationToken cancellationToken)
	{
		lock (_lock)
		{
			if (_stored is not { } current)
			{
				throw new NotFoundException("Object not found.");
			}

			if (etag != IBlob.AnyConcurrencyToken && current.ETag != etag)
			{
				throw new ConflictException(etag);
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

	public Task<Versioned<T>> StoreAsync(T value, string? etag, CancellationToken cancellationToken)
	{
		lock (_lock)
		{
			var isMet = etag switch
			{
				null => _stored is null,
				IBlob.AnyOrNoneConcurrencyToken => true,
				IBlob.AnyConcurrencyToken => _stored is not null,
				_ => _stored?.ETag == etag,
			};
			if (!isMet)
			{
				throw new ConflictException(etag ?? "Object already exists.");
			}

			var result = new Versioned<T>(value, Guid.NewGuid().ToString());
			_stored = result;
			return Task.FromResult(result);
		}
	}
}
