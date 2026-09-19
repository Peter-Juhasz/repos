using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.Abstractions;

public interface IObjectRepository<T>
{
	Task<Versioned<T>> CreateAsync(T value, CancellationToken cancellationToken) =>
		StoreAsync(value, concurrencyToken: null, cancellationToken);

	Task<Versioned<T>> UpdateAsync(T value, string concurrencyToken, CancellationToken cancellationToken) =>
		StoreAsync(value, concurrencyToken, cancellationToken);

	async Task<bool> CreateIfNotExistsAsync(T value, CancellationToken cancellationToken)
	{
		try
		{
			await CreateAsync(value, cancellationToken);
			return true;
		}
		catch (ConflictException)
		{
			return false;
		}
	}

	Task<Versioned<T>> StoreAsync(T value, string? concurrencyToken, CancellationToken cancellationToken);

	async Task<bool> ExistsAsync(CancellationToken cancellationToken)
	{
		return await GetOrDefaultAsync(cancellationToken) is not null;
	}

	async ValueTask<T> GetAsync(CancellationToken cancellationToken)
	{
		if (await GetOrDefaultAsync(cancellationToken) is { } value)
		{
			return value;
		}

		throw new NotFoundException("Object not found.");
	}

	Task<string?> GetVersionAsync(CancellationToken cancellationToken);

	async ValueTask<T?> GetOrDefaultAsync(CancellationToken cancellationToken)
	{
		if (await GetOrDefaultWithVersionAsync(cancellationToken) is { Value: T value })
		{
			return value;
		}

		return default;
	}

	ValueTask<Versioned<T>?> GetOrDefaultWithVersionAsync(CancellationToken cancellationToken);

	Task DeleteAsync(CancellationToken cancellationToken) => DeleteWithVersionAsync(IBlob.AnyConcurrencyToken, cancellationToken);

	Task DeleteWithVersionAsync(string concurrencyToken, CancellationToken cancellationToken);

	async Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		try
		{
			await DeleteAsync(cancellationToken);
			return true;
		}
		catch (NotFoundException)
		{
			return false;
		}
	}

	Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken);

	Task<T?> ApplyAsync(Func<T?, CancellationToken, ValueTask<T?>> factory, CancellationToken cancellationToken) => OptimisticConcurrency.RetryAsync(async c =>
	{
		var versioned = await GetOrDefaultWithVersionAsync(cancellationToken);
		var transformed = await factory(versioned.HasValue ? versioned.Value.Value : default, cancellationToken);
		switch ((versioned, transformed))
		{
			// modify
			case ({ ETag: string version }, not null):
				await UpdateAsync(transformed, version, cancellationToken);
				break;

			// create
			case (null, not null):
				await CreateAsync(transformed, cancellationToken);
				break;

			// delete
			case ({ ETag: string version }, null):
				await DeleteWithVersionAsync(version, cancellationToken);
				break;

			// did not exist, nothing to create
			case (null, null):
				// nothing to do
				break;
		}
		return transformed;
	}, cancellationToken);

	Task<T?> ApplyAsync(Func<T?, T?> factory, CancellationToken cancellationToken) => ApplyAsync((value, ct) => new(factory(value)), cancellationToken);
}
