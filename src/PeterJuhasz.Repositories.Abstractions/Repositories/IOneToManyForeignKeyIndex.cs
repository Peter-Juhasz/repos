namespace PeterJuhasz.Repositories.Abstractions;

public interface IOneToManyForeignKeyIndex
{
	Task AddAsync(string principalKey, string foreignKey, CancellationToken cancellationToken);

	Task<bool> AddOrUpdateAsync(string principalKey, string foreignKey, CancellationToken cancellationToken);

	Task<bool> ContainsAsync(string principalKey, string foreignKey, CancellationToken cancellationToken);

	IAsyncEnumerable<string> ListAsync(string principalKey, CancellationToken cancellationToken);

	Task DeleteAsync(string principalKey, string foreignKey, CancellationToken cancellationToken);

	async Task DeleteAsync(string principalKey, CancellationToken cancellationToken)
	{
		await foreach (var foreignKey in ListAsync(principalKey, cancellationToken))
		{
			await DeleteAsync(principalKey, foreignKey, cancellationToken);
		}
	}

	Task<bool> DeleteIfExistsAsync(string principalKey, string foreignKey, CancellationToken cancellationToken);
}

