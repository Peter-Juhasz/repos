using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.ObjectPool;

namespace PeterJuhasz.Repositories.AzureStorage;

internal sealed class BufferingWriterStream(
	BlobClient blobClient,
	MemoryStream inner,
	BlobUploadOptions options,
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

	private bool _disposed;

	protected override void Dispose(bool disposing)
	{
		if (disposing && !_disposed)
		{
			throw new NotSupportedException("The blob is uploaded on dispose, use DisposeAsync instead.");
		}
	}

	public override async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		try
		{
			inner.Position = 0;
			await blobClient.UploadAsync(inner, options, cancellationToken);
		}
		catch (RequestFailedException ex) when (
			ex.ErrorCode == BlobErrorCode.ContainerNotFound
		)
		{
			await blobClient.GetParentBlobContainerClient().CreateIfNotExistsAsync(cancellationToken: cancellationToken);
			inner.Position = 0;
			try
			{
				await blobClient.UploadAsync(inner, options, cancellationToken);
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
