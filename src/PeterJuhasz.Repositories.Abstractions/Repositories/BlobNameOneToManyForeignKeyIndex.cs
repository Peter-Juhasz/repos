using PeterJuhasz.Repositories.Abstractions;
using System.Runtime.CompilerServices;

namespace PeterJuhasz.Repositories.Blobs;

public sealed class BlobNameOneToManyForeignKeyIndex(
	IBlobPartition partition
) : IOneToManyForeignKeyIndex
{
	private IBlob GetRepository(string principalKey, string foreignKey) =>
		partition.GetSubPartition(principalKey).GetBlob(foreignKey);

	public async Task AddAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		await GetRepository(principalKey, foreignKey).WriteAsync(ReadOnlyMemory<byte>.Empty, null, default, cancellationToken);
	}

	public async Task<bool> AddOrUpdateAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		try
		{
			await GetRepository(principalKey, foreignKey).WriteAsync(ReadOnlyMemory<byte>.Empty, null, default, cancellationToken);
			return true;
		}
		catch (ConflictException)
		{
			return false;
		}
	}

	public async Task<bool> ContainsAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		return await GetRepository(principalKey, foreignKey).ExistsAsync(cancellationToken);
	}

	public async IAsyncEnumerable<string> ListAsync(string principalKey, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (var blob in partition.GetSubPartition(principalKey).GetBlobs(cancellationToken))
		{
			var lastSlash = blob.Name.LastIndexOf('/');
			if (lastSlash != -1)
			{
				throw new InvalidOperationException($"Blob name '{blob.Name}' contains a slash, which is not allowed in this index.");
			}

			var foreignKey = blob.Name[(lastSlash + 1)..];
			yield return foreignKey;
		}
	}

	public async Task DeleteAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		var deleted = await GetRepository(principalKey, foreignKey).DeleteIfExistsAsync(cancellationToken);
		if (!deleted)
		{
			throw new NotFoundException(foreignKey);
		}
	}

	public async Task DeleteAsync(string principalKey, CancellationToken cancellationToken)
	{
		await partition.GetSubPartition(principalKey).ClearAsync(cancellationToken);
	}

	public async Task<bool> DeleteIfExistsAsync(string principalKey, string foreignKey, CancellationToken cancellationToken)
	{
		return await GetRepository(principalKey, foreignKey).DeleteIfExistsAsync(cancellationToken);
	}
}

public static partial class Extensions
{
	extension(IBlobPartition partition)
	{
		public IOneToManyForeignKeyIndex AsOneToManyForeignKeyIndexInBlobNames() =>
			new BlobNameOneToManyForeignKeyIndex(partition);
	}
}
