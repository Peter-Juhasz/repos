using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using PeterJuhasz.Repositories.Serialization;
using System.Buffers;

namespace PeterJuhasz.Repositories.AzureStorage;

public static partial class QueueExtensions
{
	extension(QueueClient queue)
	{
		public async Task<SendReceipt> SendMessageAsync<T>(
			T obj,
			ISerializer<T> serializer,
			TimeSpan? visibilityTimeout = null,
			CancellationToken cancellationToken = default
		)
		{
			// the buffer is only borrowed until the message is sent
			using var _ = ArrayBufferWriterPool<byte>.GetPooledObject(out var buffer);
			serializer.Serialize(obj, buffer);
			var data = BinaryData.FromBytes(buffer.WrittenMemory);

			try
			{
				return await queue.SendMessageAsync(data, visibilityTimeout: visibilityTimeout, cancellationToken: cancellationToken);
			}
			catch (RequestFailedException ex) when (ex.ErrorCode == QueueErrorCode.QueueNotFound)
			{
				await queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
				return await queue.SendMessageAsync(data, visibilityTimeout: visibilityTimeout, cancellationToken: cancellationToken);
			}
		}
	}
}
