namespace PeterJuhasz.Repositories.Abstractions;

public sealed class SetDeduplicatingQueue<T>(
	IStorageQueue<T> inner,
	ISetRepository<string> set,
	Func<T, string?> deduplicationKeySelector
) : IStorageQueue<T>
{
	public ValueTask<long> GetCountAsync(CancellationToken cancellationToken)
	{
		return inner.GetCountAsync(cancellationToken);
	}

	public IAsyncEnumerable<T> ReceiveAsync(int batchCount, TimeSpan? visibilityTimeout, CancellationToken cancellationToken)
	{
		return inner.ReceiveAsync(batchCount, visibilityTimeout, cancellationToken);
	}

	public async ValueTask SendMessageAsync(T value, IStorageQueue<T>.SendOptions<T> options, CancellationToken cancellationToken)
	{
		var key = deduplicationKeySelector(value);

		if (key == null)
		{
			await inner.SendMessageAsync(value, options, cancellationToken);
			return;
		}

		if (await set.AddAsync(key, cancellationToken))
		{
			await inner.SendMessageAsync(value, options, cancellationToken);
		}
	}

	public async ValueTask SendMessagesAsync(IReadOnlyList<T> value, IStorageQueue<T>.SendOptions<T> options, CancellationToken cancellationToken)
	{
		foreach (var item in value)
		{
			await SendMessageAsync(item, options, cancellationToken);
		}
	}
}

public static partial class Extensions
{
	extension<T>(IStorageQueue<T> queue)
	{
		public IStorageQueue<T> WithDeduplication(ISetRepository<string> set, Func<T, string> deduplicationKeySelector) =>
			new SetDeduplicatingQueue<T>(queue, set, deduplicationKeySelector);
	}
}