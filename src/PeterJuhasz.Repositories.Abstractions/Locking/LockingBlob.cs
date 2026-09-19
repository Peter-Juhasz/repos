using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.Locking;

public sealed class LockingBlob(IBlob inner, SemaphoreSlim semaphore) : IBlob
{
	public string Name => inner.Name;

	public IBlob Blob => inner;

	public async Task<IBlob.ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			return await inner.GetInfoAsync(cancellationToken);
		}
	}

	public async Task<string> SetMetadataAsync(string concurrencyToken, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			return await inner.SetMetadataAsync(concurrencyToken, metadata, cancellationToken);
		}
	}

	public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			return await inner.ExistsAsync(cancellationToken);
		}
	}

	public async Task<IBlob.BlobReadResult?> ReadAsync(CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			return await inner.ReadAsync(cancellationToken);
		}
	}

	public async Task<string> WriteAsync(ReadOnlyMemory<byte> data, string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			return await inner.WriteAsync(data, concurrencyToken, options, cancellationToken);
		}
	}

	public async Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			return await inner.OpenReadAsync(cancellationToken);
		}
	}

	public async Task<Stream> OpenWriteAsync(string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			return await inner.OpenWriteAsync(concurrencyToken, options, cancellationToken);
		}
	}

	public async Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			await inner.DeleteAsync(concurrencyToken, cancellationToken);
		}
	}

	public async Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			return await inner.DeleteIfExistsAsync(cancellationToken);
		}
	}
}

public static partial class Extensions
{
	extension(IBlob blob)
	{
		public LockingBlob WithLocking() => new(blob, new SemaphoreSlim(1, 1));

		public LockingBlob WithLocking(SemaphoreSlim semaphore) => new(blob, semaphore);
	}
}
