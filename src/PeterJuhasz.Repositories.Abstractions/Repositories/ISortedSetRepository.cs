namespace PeterJuhasz.Repositories.Abstractions;

public interface ISortedSetRepository<TKey, TItem>
{
	Task<bool> AddAsync(TKey sortKey, TItem value, CancellationToken cancellationToken);
	Task<bool> ContainsAsync(TKey sortKey, TItem value, CancellationToken cancellationToken);
	Task<bool> DeleteAsync(TKey sortKey, TItem value, CancellationToken cancellationToken);
	IAsyncEnumerable<(TKey sortkey, TItem value)> ListAsync(CancellationToken cancellationToken);
	Task ClearAsync(CancellationToken cancellationToken);
}