using System.Collections.Immutable;

namespace PeterJuhasz.Repositories.Abstractions;

public sealed class ObjectOneToOneForeignKeyIndex(
	IObjectRepository<IImmutableDictionary<string, string>> repository
) : IOneToOneForeignKeyIndex
{
	public async Task AddAsync(string foreignKey, string principalKey, CancellationToken cancellationToken)
	{
		// throwing a ConflictException inside ApplyAsync would be retried as a concurrency conflict
		var exists = false;
		await repository.ApplyAsync(dict =>
		{
			exists = dict?.ContainsKey(foreignKey) ?? false;
			if (exists)
			{
				return dict;
			}

			return dict.Safe().SetItem(foreignKey, principalKey);
		}, cancellationToken);

		if (exists)
		{
			throw new ConflictException(foreignKey);
		}
	}

	public async Task<bool> AddOrUpdateAsync(string foreignKey, string principalKey, CancellationToken cancellationToken)
	{
		var exists = false;
		await repository.ApplyAsync(dict =>
		{
			dict = dict.Safe();
			exists = dict.ContainsKey(foreignKey);
			return dict.SetItem(foreignKey, principalKey);
		}, cancellationToken);
		return !exists;
	}

	public async Task DeleteAsync(string foreignKey, CancellationToken cancellationToken)
	{
		if (!await DeleteIfExistsAsync(foreignKey, cancellationToken))
		{
			throw new NotFoundException(foreignKey);
		}
	}

	public async Task<bool> DeleteIfExistsAsync(string foreignKey, CancellationToken cancellationToken)
	{
		var exists = false;
		await repository.ApplyAsync(dict =>
		{
			exists = dict?.ContainsKey(foreignKey) ?? false;

			// return the original (possibly null) dictionary, so nothing is written when there is nothing to delete
			return exists ? dict!.Remove(foreignKey) : dict;
		}, cancellationToken);
		return exists;
	}

	public async Task<string?> GetOrDefaultAsync(string foreignKey, CancellationToken cancellationToken)
	{
		var dict = await repository.GetOrDefaultAsync(cancellationToken);
		if (dict == null)
		{
			return null;
		}

		if (!dict.TryGetValue(foreignKey, out var principalKey))
		{
			return null;
		}

		return principalKey;
	}
}

public static partial class Extensions
{
	extension(IObjectRepository<IImmutableDictionary<string, string>> blob)
	{
		public IOneToOneForeignKeyIndex AsOneToOneForeignKeyIndex() =>
			new ObjectOneToOneForeignKeyIndex(blob);
	}
}