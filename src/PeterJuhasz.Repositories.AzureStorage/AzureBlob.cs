using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.AzureStorage;

public sealed partial class AzureBlob(BlobClient blob, WriteMode writeMode = WriteMode.BufferStreamUpload) : IBlob
{
	private static readonly BlobRequestConditions IfNoneMatchAll = new()
	{
		IfNoneMatch = ETag.All,
	};

	private static readonly IDictionary<string, string> EmptyMetadata = new SmallDictionary<string, string>(capacity: 0);

	private string? _name;
	public string Name
	{
		get
		{
			_name ??= blob.Uri.ToString().DecodeUri();
			return _name;
		}
	}

	public BlobClient Client => blob;

	public async Task<IBlob.ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken)
	{
		try
		{
			var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
			return ToBlobInfo(properties.Value);
		}
		catch (RequestFailedException ex) when (
			ex.ErrorCode == BlobErrorCode.ContainerNotFound ||
			ex.ErrorCode == BlobErrorCode.BlobNotFound
		)
		{
			return null;
		}
	}

	public async Task<string> SetMetadataAsync(string concurrencyToken, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
	{
		try
		{
			var properties = await blob.SetMetadataAsync(metadata?.AsOrToMutable() ?? EmptyMetadata, conditions: new()
			{
				IfMatch = new(concurrencyToken)
			}, cancellationToken: cancellationToken);
			return properties.Value.ETag.ToString();
		}
		catch (RequestFailedException ex) when (
			ex.ErrorCode == BlobErrorCode.ContainerNotFound ||
			ex.ErrorCode == BlobErrorCode.BlobNotFound
		)
		{
			throw new NotFoundException("The specified blob does not exist.");
		}
		catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ConditionNotMet)
		{
			// a specific ETag condition fails with 412 even when the blob does not exist
			if (!await ExistsAsync(cancellationToken))
			{
				throw new NotFoundException("The specified blob does not exist.");
			}

			throw new ConflictException(concurrencyToken);
		}
	}


	public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
	{
		try
		{
			return await blob.ExistsAsync(cancellationToken);
		}
		catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
		{
			return false;
		}
	}


	public async Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken)
	{
		try
		{
			var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
			return new(response.Value.Content, ToBlobInfo(response.Value.Details));
		}
		catch (RequestFailedException ex) when (
			ex.ErrorCode == BlobErrorCode.ContainerNotFound ||
			ex.ErrorCode == BlobErrorCode.BlobNotFound
		)
		{
			return null;
		}
	}

	public async Task<Stream> OpenWriteAsync(string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		switch (writeMode)
		{
			case WriteMode.OpenWrite:
				{
					var uploadOptions = new BlobOpenWriteOptions()
					{
						OpenConditions = concurrencyToken switch
						{
							null => IfNoneMatchAll,
							IBlob.AnyOrNoneConcurrencyToken => null,
							string => new BlobRequestConditions()
							{
								IfMatch = new ETag(concurrencyToken),
							},
						},
						HttpHeaders = GetHttpHeaders(options.MediaType, options.ContentEncoding),
						Metadata = options.Metadata?.AsOrToMutable(),
					};
					try
					{
						return await blob.OpenWriteAsync(overwrite: true, uploadOptions, cancellationToken);
					}
					catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
					{
						await blob.GetParentBlobContainerClient().CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
						try
						{
							return await blob.OpenWriteAsync(overwrite: true, uploadOptions, cancellationToken);
						}
						catch (RequestFailedException ex2) when (
							ex2.ErrorCode == BlobErrorCode.ConditionNotMet ||
							ex2.ErrorCode == BlobErrorCode.BlobAlreadyExists
						)
						{
							throw new ConflictException(concurrencyToken ?? "NONE");
						}
					}
					catch (RequestFailedException ex) when (
						ex.ErrorCode == BlobErrorCode.ConditionNotMet ||
						ex.ErrorCode == BlobErrorCode.BlobAlreadyExists
					)
					{
						throw new ConflictException(concurrencyToken ?? "NONE");
					}
				}

			case WriteMode.BufferStreamUpload:
				{
					var uploadOptions = new BlobUploadOptions()
					{
						Conditions = concurrencyToken switch
						{
							null => IfNoneMatchAll,
							IBlob.AnyOrNoneConcurrencyToken => null,
							string => new BlobRequestConditions()
							{
								IfMatch = new ETag(concurrencyToken),
							},
						},
						HttpHeaders = GetHttpHeaders(options.MediaType, options.ContentEncoding),
						Metadata = options.Metadata?.AsOrToMutable(),
						TransferOptions = new()
						{
							MaximumConcurrency = 1
						}
					};
					var pooledObject = MemoryStreamPool.GetPooledObject(out var buffer);
					return new BufferingWriterStream(blob, buffer, uploadOptions, concurrencyToken, pooledObject, cancellationToken);
				}

			case WriteMode.BufferStreamStageBlock:
				{
					var commitOptions = new CommitBlockListOptions()
					{
						Conditions = concurrencyToken switch
						{
							null => IfNoneMatchAll,
							IBlob.AnyOrNoneConcurrencyToken => null,
							string => new BlobRequestConditions()
							{
								IfMatch = new ETag(concurrencyToken),
							},
						},
						HttpHeaders = GetHttpHeaders(options.MediaType, options.ContentEncoding),
						Metadata = options.Metadata?.AsOrToMutable(),
					};
					var pooledObject = MemoryStreamPool.GetPooledObject(out var buffer);
					var blockBlobClient = blob.GetParentBlobContainerClient().GetBlockBlobClient(blob.Name);
					return new BufferingStagedBlockWriterStream(blockBlobClient, buffer, commitOptions, concurrencyToken, pooledObject, cancellationToken);
				}

			default:
				throw new NotSupportedException($"The specified write mode '{writeMode}' is not supported.");
		}
	}

	public async Task<IBlob.BlobReadResult?> ReadAsync(CancellationToken cancellationToken)
	{
		try
		{
			var response = await blob.DownloadContentAsync(cancellationToken: cancellationToken);
			var content = response.Value.Content;

			if (content.MediaType != response.Value.Details.ContentType)
			{
				content = content.WithMediaType(response.Value.Details.ContentType);
			}

			return new(content, ToBlobInfo(response.Value.Details));
		}
		catch (RequestFailedException ex) when (
			ex.ErrorCode == BlobErrorCode.ContainerNotFound ||
			ex.ErrorCode == BlobErrorCode.BlobNotFound
		)
		{
			return null;
		}
	}

	public async Task<string> WriteAsync(ReadOnlyMemory<byte> data, string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		var uploadOptions = new BlobUploadOptions()
		{
			Conditions = concurrencyToken switch
			{
				null => IfNoneMatchAll,
				IBlob.AnyOrNoneConcurrencyToken => null,
				string => new BlobRequestConditions()
				{
					IfMatch = new ETag(concurrencyToken),
				},
			},
			HttpHeaders = GetHttpHeaders(options.MediaType, options.ContentEncoding),
			Metadata = options.Metadata?.AsOrToMutable(),
		};
		var binaryData = new BinaryData(data, options.MediaType);
		try
		{
			var result = await blob.UploadAsync(binaryData, uploadOptions, cancellationToken: cancellationToken);
			return result.Value.ETag.ToString();
		}
		catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
		{
			await blob.GetParentBlobContainerClient().CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
			try
			{
				var result = await blob.UploadAsync(binaryData, uploadOptions, cancellationToken: cancellationToken);
				return result.Value.ETag.ToString();
			}
			catch (RequestFailedException ex2) when (
				ex2.ErrorCode == BlobErrorCode.ConditionNotMet ||
				ex2.ErrorCode == BlobErrorCode.BlobAlreadyExists
			)
			{
				throw new ConflictException(concurrencyToken ?? "NONE");
			}
		}
		catch (RequestFailedException ex) when (
			ex.ErrorCode == BlobErrorCode.ConditionNotMet ||
			ex.ErrorCode == BlobErrorCode.BlobAlreadyExists
		)
		{
			throw new ConflictException(concurrencyToken ?? "NONE");
		}
	}

	public async Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		try
		{
			var result = await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
			return result.Value;
		}
		catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ContainerNotFound)
		{
			return false;
		}
	}

	public async Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		try
		{
			await blob.DeleteAsync(conditions: new()
			{
				IfMatch = new ETag(concurrencyToken),
			}, cancellationToken: cancellationToken);
		}
		catch (RequestFailedException ex) when (
			ex.ErrorCode == BlobErrorCode.ContainerNotFound ||
			ex.ErrorCode == BlobErrorCode.BlobNotFound
		)
		{
			throw new NotFoundException("The specified blob does not exist.");
		}
		catch (RequestFailedException ex) when (ex.ErrorCode == BlobErrorCode.ConditionNotMet)
		{
			// a specific ETag condition fails with 412 even when the blob does not exist
			if (!await ExistsAsync(cancellationToken))
			{
				throw new NotFoundException("The specified blob does not exist.");
			}

			throw new ConflictException(concurrencyToken);
		}
	}


	internal static IBlob.ReadBlobInfo ToBlobInfo(BlobProperties properties) => new(
		ConcurrencyToken: properties.ETag.ToString(),
		LastModified: properties.LastModified,
		ContentEncoding: properties.ContentEncoding,
		MediaType: properties.ContentType,
		Metadata: properties.Metadata?.AsOrToReadOnly()
	);

	internal static IBlob.ReadBlobInfo ToBlobInfo(BlobContentInfo properties) => new(
		ConcurrencyToken: properties.ETag.ToString(),
		LastModified: properties.LastModified
	);

	internal static IBlob.ReadBlobInfo ToBlobInfo(BlobDownloadDetails properties) => new(
		ConcurrencyToken: properties.ETag.ToString(),
		LastModified: properties.LastModified,
		ContentEncoding: properties.ContentEncoding,
		MediaType: properties.ContentType,
		Metadata: properties.Metadata?.AsOrToReadOnly()
	);


	private static readonly BlobHttpHeaders JsonBrotliHttpHeaders = new()
	{
		ContentType = "application/json",
		ContentEncoding = "br",
	};

	private static readonly BlobHttpHeaders JsonHttpHeaders = new()
	{
		ContentType = "application/json",
	};


	private static BlobHttpHeaders GetHttpHeaders(string? mediaType, string? contentEncoding)
	{
		if (mediaType == "application/json")
		{
			if (contentEncoding == "br")
			{
				return JsonBrotliHttpHeaders;
			}
			else if (contentEncoding == null)
			{
				return JsonHttpHeaders;
			}
		}

		return new BlobHttpHeaders()
		{
			ContentType = mediaType,
			ContentEncoding = contentEncoding,
		};
	}
}

public enum WriteMode
{
	OpenWrite,
	BufferStreamUpload,
	BufferStreamStageBlock,
}

public static partial class Extensions
{
	extension(BlobClient blob)
	{
		public IBlob AsBlob(WriteMode writeMode = WriteMode.BufferStreamUpload) => new AzureBlob(blob, writeMode);
	}
}