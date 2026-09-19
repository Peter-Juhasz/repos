using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.AzureStorage;

public sealed class AzureAppendBlob(AppendBlobClient blob, string contentType = "application/octet-stream") : IAppendBlob
{
	private string? _name;
	public string Name
	{
		get
		{
			_name ??= blob.Uri.ToString();
			return _name;
		}
	}

	public async Task<IBlob.ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken)
	{
		try
		{
			var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
			return AzureBlob.ToBlobInfo(properties.Value);
		}
		catch (RequestFailedException ex) when (
			ex.ErrorCode == BlobErrorCode.ContainerNotFound ||
			ex.ErrorCode == BlobErrorCode.BlobNotFound
		)
		{
			return null;
		}
	}

	public async Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
	{
		using var stream = new ReadOnlyMemoryStream(data);
		try
		{
			await blob.AppendBlockAsync(stream, cancellationToken: cancellationToken);
		}
		catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
		{
			await blob.GetParentBlobContainerClient().CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
			await blob.CreateIfNotExistsAsync(new AppendBlobCreateOptions()
			{
				HttpHeaders = GetHeaders(contentType),
			}, cancellationToken: cancellationToken);
			stream.Position = 0;
			await blob.AppendBlockAsync(stream, cancellationToken: cancellationToken);
		}
		catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.BlobNotFound)
		{
			await blob.CreateIfNotExistsAsync(new AppendBlobCreateOptions()
			{
				HttpHeaders = GetHeaders(contentType),
			}, cancellationToken: cancellationToken);
			stream.Position = 0;
			await blob.AppendBlockAsync(stream, cancellationToken: cancellationToken);
		}
	}

	public async Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken)
	{
		try
		{
			var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
			return new(response.Value.Content, AzureBlob.ToBlobInfo(response.Value.Details));
		}
		catch (RequestFailedException ex) when (
			ex.ErrorCode == BlobErrorCode.ContainerNotFound ||
			ex.ErrorCode == BlobErrorCode.BlobNotFound
		)
		{
			return null;
		}
	}

	public Task DeleteAsync(CancellationToken cancellationToken) => blob.DeleteAsync(cancellationToken: cancellationToken);


	private static BlobHttpHeaders GetHeaders(string contentType) => contentType switch
	{
		"application/jsonl" => JsonLHttpHeaders,
		_ => new BlobHttpHeaders()
		{
			ContentType = contentType,
		},
	};

	private static readonly BlobHttpHeaders JsonLHttpHeaders = new() { ContentType = "application/jsonl" };
}

public static partial class Extensions
{
	extension(AppendBlobClient blob)
	{
		public IAppendBlob AsBlob() => new AzureAppendBlob(blob);
	}
}