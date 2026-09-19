using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using PeterJuhasz.Repositories.Abstractions;
using System.Text.Json;

namespace PeterJuhasz.Repositories.AzureStorage;

public static partial class QueueExtensions
{
	extension(QueueClient queue)
	{
		public async Task<SendReceipt> SendMessageAsJsonAsync<T>(
			T obj,
			TimeSpan? visibilityTimeout = null,
			JsonSerializerOptions? jsonSerializerOptions = null,
			CancellationToken cancellationToken = default
		)
		{
			var data = BinaryData.FromObjectAsJson<T>(obj, jsonSerializerOptions ?? DefaultJsonSerializerOptions.Default);

			SendReceipt receipt;
			try
			{
				receipt = await queue.SendMessageAsync(data, visibilityTimeout: visibilityTimeout, cancellationToken: cancellationToken);
			}
			catch (RequestFailedException ex) when (ex.ErrorCode == QueueErrorCode.QueueNotFound)
			{
				await queue.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
				receipt = await queue.SendMessageAsync(data, visibilityTimeout: visibilityTimeout, cancellationToken: cancellationToken);
			}

			return receipt;
		}
	}
}
