using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.Serialization;

namespace PeterJuhasz.Repositories.Abstractions.Blobs;

public sealed class BlobPartitionBlobOneToOneForeignKeyIndex(
	IBlobPartition partition,
	ISerializer<string> serializer
) : IOneToOneForeignKeyIndex
{
	private IObjectRepository<string> GetRepository(string foreignKey) => partition.GetBlob(foreignKey, ".ref").AsObjectRepository(serializer);

	public async Task AddAsync(string foreignKey, string principalKey, CancellationToken cancellationToken)
	{
		await GetRepository(foreignKey).CreateAsync(principalKey, cancellationToken);
	}

	public async Task<bool> AddOrUpdateAsync(string foreignKey, string principalKey, CancellationToken cancellationToken)
	{
		var exists = false;
		await GetRepository(foreignKey).ApplyAsync(dict =>
		{
			exists = dict != null;
			return principalKey;
		}, cancellationToken);
		return !exists;
	}

	public async Task DeleteAsync(string foreignKey, CancellationToken cancellationToken)
	{
		await GetRepository(foreignKey).DeleteAsync(cancellationToken);
	}

	public async Task<bool> DeleteIfExistsAsync(string foreignKey, CancellationToken cancellationToken)
	{
		return await GetRepository(foreignKey).DeleteIfExistsAsync(cancellationToken);
	}

	public async Task<string?> GetOrDefaultAsync(string foreignKey, CancellationToken cancellationToken)
	{
		var repository = GetRepository(foreignKey);
		var value = await repository.GetOrDefaultAsync(cancellationToken);
		return value;
	}
}

public static partial class Extensions
{
	extension(IBlobPartition partition)
	{
		public IOneToOneForeignKeyIndex AsOneToOneForeignKeyIndex(ISerializer<string> serializer) =>
			new BlobPartitionBlobOneToOneForeignKeyIndex(partition, serializer);

		public IOneToOneForeignKeyIndex AsOneToOneForeignKeyIndex() =>
			partition.AsOneToOneForeignKeyIndex(StringSerializer.Utf8);
	}
}