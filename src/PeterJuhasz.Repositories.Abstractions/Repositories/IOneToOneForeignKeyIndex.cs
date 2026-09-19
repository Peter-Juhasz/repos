namespace PeterJuhasz.Repositories.Abstractions;

public interface IOneToOneForeignKeyIndex
{
	Task AddAsync(string foreignKey, string principalKey, CancellationToken cancellationToken);

	Task<bool> AddOrUpdateAsync(string foreignKey, string principalKey, CancellationToken cancellationToken);

	async Task<string> GetAsync(string foreignKey, CancellationToken cancellationToken) => await GetOrDefaultAsync(foreignKey, cancellationToken) ?? throw new NotFoundException("Foreign key not found.");

	Task<string?> GetOrDefaultAsync(string foreignKey, CancellationToken cancellationToken);

	async Task<bool> ExistsAsync(string foreignKey, CancellationToken cancellationToken) => await GetOrDefaultAsync(foreignKey, cancellationToken) is not null;

	Task DeleteAsync(string foreignKey, CancellationToken cancellationToken);

	Task<bool> DeleteIfExistsAsync(string foreignKey, CancellationToken cancellationToken);
}

