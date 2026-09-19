namespace PeterJuhasz.Repositories.Abstractions;

public interface ISetRepository<T>
{
	Task<bool> AddAsync(T value, CancellationToken cancellationToken);

	async Task<IReadOnlySet<T>> AddRangeAsync(IEnumerable<T> values, CancellationToken cancellationToken)
	{
		var result = new HashSet<T>();

		foreach (var value in values)
		{
			if (await AddAsync(value, cancellationToken))
			{
				result.Add(value);
			}
		}

		return result;
	}

	Task<bool> ContainsAsync(T value, CancellationToken cancellationToken);

	async Task<IReadOnlySet<T>> ContainsRangeAsync(IEnumerable<T> values, CancellationToken cancellationToken)
	{
		var result = new HashSet<T>();
		foreach (var value in values)
		{
			if (await ContainsAsync(value, cancellationToken))
			{
				result.Add(value);
			}
		}
		return result;
	}

	IAsyncEnumerable<T> ListAsync(CancellationToken cancellationToken);

	Task<bool> DeleteAsync(T value, CancellationToken cancellationToken);

	Task ClearAsync(CancellationToken cancellationToken);
}