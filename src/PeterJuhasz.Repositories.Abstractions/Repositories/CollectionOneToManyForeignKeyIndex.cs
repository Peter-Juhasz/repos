using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.Serialization;
using System.Collections.Immutable;

namespace PeterJuhasz.Repositories.Caching;

public sealed class PartitionCollectionOneToManyForeignKeyIndex(
	IBlobPartition partition,
	ICollectionSerializer<string> serializer,
	IEqualityComparer<string>? comparer = null,
	string? extension = null
) : IOneToManyForeignKeyIndex
{
	private ICollectionRepository<string> GetRepository(string principalKey) => new BufferedBlobCollectionRepository<string>(
		extension != null ? partition.GetBlob(principalKey, extension) : partition.GetBlob(principalKey),
		serializer,
		comparer
	);

	private readonly IEqualityComparer<string> effectiveComparer = comparer ?? EqualityComparer<string>.Default;

	public async Task AddAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		await GetRepository(principalKey).ApplyAsync(items =>
		{
			items = items.Safe();

			if (items.Contains(foreignKey, comparer))
			{
				throw new ConflictException(foreignKey);
			}

			return items.Add(foreignKey);
		}, cancellationToken);
	}

	public async Task<bool> AddOrUpdateAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		var exists = false;
		await GetRepository(principalKey).ApplyAsync(items =>
		{
			items = items.Safe();
			exists = items.Contains(foreignKey, comparer);
			if (exists)
			{
				return items;
			}

			return items.Add(foreignKey);
		}, cancellationToken);
		return !exists;
	}

	public async Task<bool> ContainsAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		var items = await GetRepository(principalKey).ListAsync(cancellationToken);
		return items.Contains(foreignKey);
	}

	public IAsyncEnumerable<string> ListAsync(string principalKey, CancellationToken cancellationToken)
	{
		return GetRepository(principalKey).AsAsyncEnumerableAsync(cancellationToken);
	}

	public async Task DeleteAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		await GetRepository(principalKey).ApplyAsync(items =>
		{
			items = items.Safe();
			var newItems = items.RemoveAll(e => effectiveComparer.Equals(e, foreignKey));
			return newItems.Count is 0 ? null : newItems;
		}, cancellationToken);
	}

	public async Task DeleteAsync(string principalKey, CancellationToken cancellationToken)
	{
		await GetRepository(principalKey).ClearAsync(cancellationToken);
	}

	public async Task<bool> DeleteIfExistsAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		var existed = false;
		await GetRepository(principalKey).ApplyAsync(items =>
		{
			items = items.Safe();
			var newItems = items.RemoveAll(e => effectiveComparer.Equals(e, foreignKey));
			existed = newItems.Count < items.Count;
			return newItems.Count is 0 ? null : newItems;
		}, cancellationToken);
		return existed;
	}
}

public static partial class Extensions
{
	extension(IBlobPartition partition)
	{
		public IOneToManyForeignKeyIndex AsOneToManyForeignKeyIndexInBlob(ICollectionSerializer<string> serializer, IEqualityComparer<string>? comparer = null, string? extension = null) =>
			new PartitionCollectionOneToManyForeignKeyIndex(partition, serializer, comparer, extension);
	}
}
