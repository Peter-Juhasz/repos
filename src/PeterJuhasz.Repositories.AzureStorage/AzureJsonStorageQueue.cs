using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using PeterJuhasz.Repositories.Abstractions;
using System.Runtime.CompilerServices;

namespace PeterJuhasz.Repositories.AzureStorage;

public class AzureJsonStorageQueue<T>(QueueClient queue) : ICancellableStorageQueue<T, SendReceipt>
{
	public async ValueTask SendMessageAsync(T value, IStorageQueue<T>.SendOptions<T> options, CancellationToken cancellationToken) =>
		await queue.SendMessageAsJsonAsync<T>(value, visibilityTimeout: options.ScheduledEnqueueTime, cancellationToken: cancellationToken);

	public async ValueTask SendMessagesAsync(IReadOnlyList<T> values, IStorageQueue<T>.SendOptions<T> options, CancellationToken cancellationToken)
	{
		if (values.Count == 0)
		{
			return;
		}

		var tasks = new Task[values.Count];
		for (var i = 0; i < values.Count; i++)
		{
			var value = values[i];
			tasks[i] = SendMessageAsync(value, options, cancellationToken).AsTask();
		}

		await Task.WhenAll(tasks);
	}
	public async ValueTask<SendReceipt> ScheduleMessageAsync(T value, IStorageQueue<T>.SendOptions<T> options, CancellationToken cancellationToken) =>
		await queue.SendMessageAsJsonAsync(value, visibilityTimeout: options.ScheduledEnqueueTime, cancellationToken: cancellationToken);

	public async ValueTask<IEnumerable<SendReceipt>> ScheduleMessagesAsync(IReadOnlyList<T> values, IStorageQueue<T>.SendOptions<T> options, CancellationToken cancellationToken)
	{
		if (values.Count == 0)
		{
			return [];
		}

		var tasks = new Task<SendReceipt>[values.Count];
		for (var i = 0; i < values.Count; i++)
		{
			var value = values[i];
			tasks[i] = ScheduleMessageAsync(value, options, cancellationToken).AsTask();
		}

		return await Task.WhenAll(tasks);
	}

	public async ValueTask CancelMessageAsync(SendReceipt receipt, CancellationToken cancellationToken) =>
		await queue.DeleteMessageAsync(receipt.MessageId, receipt.PopReceipt, cancellationToken);

	public async ValueTask CancelMessagesAsync(IReadOnlyList<SendReceipt> receipts, CancellationToken cancellationToken)
	{
		var tasks = new Task[receipts.Count];

		for (var i = 0; i < receipts.Count; i++)
		{
			var value = receipts[i];
			tasks[i] = CancelMessageAsync(value, cancellationToken).AsTask();
		}
		await Task.WhenAll(tasks);
	}

	public async ValueTask<long> GetCountAsync(CancellationToken cancellationToken)
	{
		try
		{
			var properties = await queue.GetPropertiesAsync(cancellationToken: cancellationToken);
			return properties.Value.ApproximateMessagesCount;
		}
		catch (RequestFailedException ex) when (ex.ErrorCode == QueueErrorCode.QueueNotFound)
		{
			return 0L;
		}
	}

	public async IAsyncEnumerable<T> ReceiveAsync(int batchCount, TimeSpan? visibilityTimeout, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		QueueMessage[]? items = null;

		// fetch first page
		try
		{
			if (batchCount == 1)
			{
				var item = await queue.ReceiveMessageAsync(visibilityTimeout: visibilityTimeout, cancellationToken: cancellationToken);
				items = item.Value switch
				{
					{ } => [item],
					null => []
				};
			}
			else
			{
				items = await queue.ReceiveMessagesAsync(maxMessages: batchCount, cancellationToken: cancellationToken);
			}
		}
		catch (RequestFailedException ex) when (ex.ErrorCode == QueueErrorCode.QueueNotFound)
		{
			yield break;
		}

		while (items.Length > 0)
		{
			// process page
			foreach (var item in items)
			{
				cancellationToken.ThrowIfCancellationRequested();

				yield return item.Body.ToObjectFromJson<T>(DefaultJsonSerializerOptions.Default)!;
				await queue.DeleteMessageAsync(item.MessageId, item.PopReceipt, cancellationToken);
			}

			// fetch next page
			if (batchCount == 1)
			{
				var item = await queue.ReceiveMessageAsync(visibilityTimeout: visibilityTimeout, cancellationToken: cancellationToken);
				items = item.Value switch
				{
					{ } => [item],
					null => []
				};
			}
			else
			{
				items = await queue.ReceiveMessagesAsync(maxMessages: batchCount, cancellationToken: cancellationToken);
			}
		}
	}
}

public static partial class QueueExtensions
{
	public static AzureJsonStorageQueue<T> AsJson<T>(this QueueClient partition) =>
		new(partition);
}