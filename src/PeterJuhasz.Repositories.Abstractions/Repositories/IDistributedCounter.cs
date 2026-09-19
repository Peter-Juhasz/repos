namespace PeterJuhasz.Repositories.Abstractions;

public interface IDistributedCounter
{
	ValueTask<int> IncrementAsync(CancellationToken cancellationToken) => IncrementAsync(1, cancellationToken);

	ValueTask<int> IncrementAsync(int delta, CancellationToken cancellationToken) => ApplyAsync(oldValue => oldValue + delta, cancellationToken);

	async ValueTask SetAsync(int count, CancellationToken cancellationToken)
	{
		await ApplyAsync(_ => count, cancellationToken);
	}

	ValueTask<int> ApplyAsync(Func<int, int> transform, CancellationToken cancellationToken);

	ValueTask<int> GetAsync(CancellationToken cancellationToken);

	ValueTask<int> DecrementAsync(CancellationToken cancellationToken) => DecrementAsync(1, cancellationToken);

	ValueTask<int> DecrementAsync(int delta, CancellationToken cancellationToken) => IncrementAsync(-delta, cancellationToken);

	ValueTask ResetAsync(CancellationToken cancellationToken) => SetAsync(0, cancellationToken);
}
