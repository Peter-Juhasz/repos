using System.Collections.Immutable;

namespace PeterJuhasz.Repositories.Abstractions;

public sealed class ObjectOneToOneForeignKeyIndex(
	IObjectRepository<IImmutableDictionary<string, string>> repository
) : IOneToOneForeignKeyIndex
{
	public Task AddAsync(string foreignKey, string principalKey, CancellationToken cancellationToken)
	{
		return repository.ApplyAsync(dict =>
		{
			dict = dict.Safe();
			if (dict.ContainsKey(foreignKey))
			{
				throw new ConflictException(foreignKey);
			}

			return dict.SetItem(foreignKey, principalKey);
		}, cancellationToken);
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

	public Task DeleteAsync(string foreignKey, CancellationToken cancellationToken) => repository.ApplyAsync(dict => dict.Safe().Remove(foreignKey), cancellationToken);

	public async Task<bool> DeleteIfExistsAsync(string foreignKey, CancellationToken cancellationToken)
	{
		var exists = false;
		await repository.ApplyAsync(dict =>
		{
			dict = dict.Safe();
			exists = dict.ContainsKey(foreignKey);
			return dict.Remove(foreignKey);
		}, cancellationToken);
		return !exists;
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