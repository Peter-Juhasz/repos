using Azure;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.ObjectPool;

namespace PeterJuhasz.Repositories.AzureStorage;


/**
| Method            | Mean     | Error    | StdDev   | Allocated |
|------------------ |---------:|---------:|---------:|----------:|
| UploadStreamAsync | 28.62 ms | 1.116 ms | 0.664 ms |  15.46 KB |
| StageBlockAsync   | 55.35 ms | 0.522 ms | 0.273 ms |  34.92 KB |
 */

internal sealed class BufferingStagedBlockWriterStream(
	BlockBlobClient blobClient,
	MemoryStream inner,
	CommitBlockListOptions options,
	string? concurrencyToken,
	PooledObject<MemoryStream> pooledObject,
	CancellationToken cancellationToken
) : Stream
{
	public override bool CanRead => false;
	public override bool CanSeek => false;
	public override bool CanWrite => inner.CanWrite;
	public override long Length => inner.Length;
	public override long Position
	{
		get => inner.Position;
		set => throw new NotSupportedException();
	}

	public override void Flush() => inner.Flush();

	public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

	public override async ValueTask DisposeAsync()
	{
		var blockId = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
		inner.Position = 0;

		try
		{
			try
			{
				await blobClient.StageBlockAsync(blockId, inner, null, cancellationToken);
			}
			catch (RequestFailedException ex) when (
				ex.ErrorCode == BlobErrorCode.ContainerNotFound
			)
			{
				await blobClient.GetParentBlobContainerClient().CreateIfNotExistsAsync(cancellationToken: cancellationToken);
				inner.Position = 0;
				await blobClient.StageBlockAsync(blockId, inner, null, cancellationToken);
			}

			try
			{
				await blobClient.CommitBlockListAsync([blockId], options, cancellationToken);
			}
			catch (RequestFailedException ex) when (
				ex.ErrorCode == BlobErrorCode.ConditionNotMet ||
				ex.ErrorCode == BlobErrorCode.BlobAlreadyExists
			)
			{
				throw new ConflictException(concurrencyToken ?? "NONE");
			}
		}
		finally
		{
			pooledObject.Dispose();
		}
	}


	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
	public override void SetLength(long value) => throw new NotSupportedException();

	public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
	public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
	public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
}
