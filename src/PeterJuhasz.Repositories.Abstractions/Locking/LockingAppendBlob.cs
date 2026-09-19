using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.Locking;

public sealed class LockingAppendBlob(IAppendBlob inner, SemaphoreSlim semaphore) : IAppendBlob
{
	public string Name => inner.Name;

	public IAppendBlob Blob => inner;

	public async Task<IBlob.ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			return await inner.GetInfoAsync(cancellationToken);
		}
	}

	public async Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			await inner.AppendAsync(data, cancellationToken);
		}
	}

	public async Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			return await inner.OpenReadAsync(cancellationToken);
		}
	}

	public async Task DeleteAsync(CancellationToken cancellationToken)
	{
		using (await AsyncLock.LockAsync(semaphore, cancellationToken))
		{
			await inner.DeleteAsync(cancellationToken);
		}
	}
}

public static partial class Extensions
{
	extension(IAppendBlob blob)
	{
		public LockingAppendBlob WithLocking() => new(blob, new SemaphoreSlim(1, 1));

		public LockingAppendBlob WithLocking(SemaphoreSlim semaphore) => new(blob, semaphore);
	}
}
