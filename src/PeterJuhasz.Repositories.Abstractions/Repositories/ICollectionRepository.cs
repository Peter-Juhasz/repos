using System.Collections.Immutable;

namespace PeterJuhasz.Repositories.Abstractions;

public interface ICollectionRepository<T>
{
	async Task<bool> AddAsync(T value, CancellationToken cancellationToken = default)
	{
		var added = false;

		await ApplyAsync(oldItems =>
		{
			added = false;

			// empty
			if (oldItems is null or { Count: 0 })
			{
				added = true;
				return [value];
			}

			// add
			var newItems = oldItems.Add(value);
			added = true;

			return newItems;
		}, cancellationToken);

		return added;
	}

	Task AddRangeAsync(IReadOnlyCollection<T> value, CancellationToken cancellationToken = default)
	{
		if (value.Count == 0)
		{
			return Task.CompletedTask;
		}

		return ApplyAsync(collection =>
		{
			if (collection is null or { Count: 0 })
			{
				return value;
			}

			return collection.AddRange(value);
		}, cancellationToken);
	}

	async Task AddOrUpdateAsync(T value, Func<T, T> update, CancellationToken cancellationToken = default)
	{
		await ApplyAsync(items =>
		{
			if (items is null or { Count: 0 })
			{
				return items;
			}

			var newItems = items;

			for (int i = 0; i < items.Count; i++)
			{
				var item = items[i];
				if (EqualityComparer<T>.Default.Equals(item, value))
				{
					newItems = newItems.SetItem(i, update(item));
				}
			}

			return newItems;
		}, cancellationToken);
	}

	Task ApplyAsync(Func<IImmutableList<T>?, IReadOnlyCollection<T>?> update, CancellationToken cancellationToken = default) => ApplyAsync((items, ct) => new(update(items)), cancellationToken);

	Task ApplyAsync(Func<IImmutableList<T>?, CancellationToken, ValueTask<IReadOnlyCollection<T>?>> update, CancellationToken cancellationToken = default);

	async Task<IReadOnlyCollection<T>> ListAsync(CancellationToken cancellationToken = default)
	{
		var versioned = await ListWithVersionAsync(cancellationToken);
		return versioned.Value;
	}

	Task<Versioned<IReadOnlyCollection<T>>> ListWithVersionAsync(CancellationToken cancellationToken = default);

	async Task<T?> GetOrDefaultAsync(Func<T, bool> predicate, CancellationToken cancellationToken)
	{
		await foreach (var item in AsAsyncEnumerableAsync(cancellationToken))
		{
			if (predicate(item))
			{
				return item;
			}
		}

		return default;
	}

	async Task<bool> AnyAsync(CancellationToken cancellationToken) => await AsAsyncEnumerableAsync(cancellationToken).AnyAsync(cancellationToken);

	async Task<bool> AnyAsync(Func<T, bool> predicate, CancellationToken cancellationToken) => await AsAsyncEnumerableAsync(cancellationToken).AnyAsync(predicate, cancellationToken);

	async Task<int> CountAsync(CancellationToken cancellationToken) => await AsAsyncEnumerableAsync(cancellationToken).CountAsync(cancellationToken);

	async Task<int> CountAsync(Func<T, bool> predicate, CancellationToken cancellationToken) => await AsAsyncEnumerableAsync(cancellationToken).CountAsync(predicate, cancellationToken);

	async Task<bool> UpdateAsync(Func<T, bool> predicate, Func<T, T> update, CancellationToken cancellationToken = default)
	{
		bool updated = false;

		await ApplyAsync(collection =>
		{
			updated = false;

			if (collection is null or { Count: 0 })
			{
				return collection;
			}

			for (int i = 0; i < collection.Count; i++)
			{
				var item = collection[i];
				if (predicate(item))
				{
					var updatedItem = update(item);

					if (!EqualityComparer<T>.Default.Equals(item, updatedItem))
					{
						collection = collection.SetItem(i, updatedItem);
					}

					updated = true;
				}
			}

			return collection;
		}, cancellationToken);

		return updated;
	}

	Task<bool> DeleteAsync(T value, CancellationToken cancellationToken = default) => DeleteAsync(item => EqualityComparer<T>.Default.Equals(item, value), cancellationToken);

	async Task<bool> DeleteAsync(Func<T, bool> predicate, CancellationToken cancellationToken = default)
	{
		var removed = false;

		await ApplyAsync(items =>
		{
			removed = false;
			if (items is null or { Count: 0 })
			{
				return items;
			}

			var newItems = items.RemoveAll(item => predicate(item));

			removed = newItems.Count < items.Count;

			if (newItems.Count == 0)
			{
				return null;
			}

			return newItems;
		}, cancellationToken);

		return removed;
	}

	Task ClearAsync(CancellationToken cancellationToken = default);

	Task<string?> GetVersionAsync(CancellationToken cancellationToken = default);

	Task StoreAsync(IImmutableList<T> items, CancellationToken cancellationToken = default) => ApplyAsync(_ => items, cancellationToken);
	Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken);
	IAsyncEnumerable<T> AsAsyncEnumerableAsync(CancellationToken cancellationToken);
}