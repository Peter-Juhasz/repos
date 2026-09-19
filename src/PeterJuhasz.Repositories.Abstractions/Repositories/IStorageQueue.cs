namespace PeterJuhasz.Repositories.Abstractions;

public interface IStorageQueue<T>
{
	ValueTask SendMessageAsync(T value, SendOptions<T> options, CancellationToken cancellationToken);

	ValueTask SendMessagesAsync(IReadOnlyList<T> value, SendOptions<T> options, CancellationToken cancellationToken);

	ValueTask<long> GetCountAsync(CancellationToken cancellationToken);

	IAsyncEnumerable<T> ReceiveAsync(int batchCount, TimeSpan? visibilityTimeout, CancellationToken cancellationToken);

	public readonly record struct SendOptions<TMessage>(
		Func<TMessage, string>? MessageIdSelector = null,
		TimeSpan? ScheduledEnqueueTime = null,
		TimeSpan? TimeToLive = null
	);
}

public interface ICancellableStorageQueue<T, TReceipt> : IStorageQueue<T>
{
	ValueTask<TReceipt> ScheduleMessageAsync(T value, SendOptions<T> options, CancellationToken cancellationToken);

	ValueTask<IEnumerable<TReceipt>> ScheduleMessagesAsync(IReadOnlyList<T> value, SendOptions<T> options, CancellationToken cancellationToken);

	ValueTask CancelMessageAsync(TReceipt receipt, CancellationToken cancellationToken);
	ValueTask CancelMessagesAsync(IReadOnlyList<TReceipt> receipts, CancellationToken cancellationToken);
}