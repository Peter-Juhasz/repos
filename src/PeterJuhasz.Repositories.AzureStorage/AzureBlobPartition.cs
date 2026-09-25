using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging.Abstractions;
using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.AzureStorage;

public sealed class AzureBlobPartition(BlobContainerClient client, string? path) : IBlobPartition
{
	public string? Path => path;

	public BlobContainerClient Container => client;

	private string GetBlobName(string name)
	{
		if (path == null)
		{
			return name;
		}

		return $"{path}/{name}";
	}

	public IAppendBlob GetAppendBlob(string name) => new AzureAppendBlob(client.GetAppendBlobClient(GetBlobName(name)));

	public IBlob GetBlob(string name) => new AzureBlob(client.GetBlobClient(GetBlobName(name)));

	public IBlobPartition GetSubPartition(string name)
	{
		if (path == null)
		{
			return new AzureBlobPartition(client, name);
		}
		else
		{
			return new AzureBlobPartition(client, $"{path}/{name}");
		}
	}

	public IAsyncEnumerable<IBlob> GetBlobs(CancellationToken cancellationToken)
	{
		return client.GetBlobsAsync(new()
		{
			Prefix = path == null ? null : $"{path}/",
		}, cancellationToken)
			.Catch<BlobItem, RequestFailedException>(ex => ex.ErrorCode == BlobErrorCode.ContainerNotFound, AsyncEnumerable.Empty<BlobItem>, NullLogger.Instance, cancellationToken)
			.Select(b => new AzureBlob(client.GetBlobClient(b.Name)));
	}

	public async Task ClearAsync(CancellationToken cancellationToken)
	{
		try
		{
			var batchClient = client.GetParentBlobServiceClient().GetBlobBatchClient();
			var batch = batchClient.CreateBatch();
			await foreach (var item in GetBlobs(cancellationToken))
			{
				batch.DeleteBlob(item.Name.ToUri(UriKind.Absolute));
				if (batch.RequestCount >= 200) // 256
				{
					await batchClient.SubmitBatchAsync(batch, throwOnAnyFailure: false, cancellationToken);
					batch.Dispose();
					batch = batchClient.CreateBatch();
				}
			}

			if (batch.RequestCount.Any())
			{
				await batchClient.SubmitBatchAsync(batch, throwOnAnyFailure: false, cancellationToken);
			}
			batch.Dispose();
		}
		catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
		{

		}
	}
}

public static partial class Extensions
{
	extension(BlobContainerClient container)
	{
		public IBlobPartition GetPartition() => new AzureBlobPartition(container, path: null);

		public IBlobPartition GetPartition(string path) => new AzureBlobPartition(container, path);

		public IBlobPartition GetPartition(params ReadOnlySpan<string> segments)
		{
			var path = segments.IsEmpty ? null : string.Join('/', segments);
			return new AzureBlobPartition(container, path);
		}
	}
}