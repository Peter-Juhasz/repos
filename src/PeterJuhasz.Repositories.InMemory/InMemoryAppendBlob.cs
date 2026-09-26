using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.InMemory;

public sealed class InMemoryAppendBlob(string name, TimeProvider timeProvider, string? mediaType = null) : IAppendBlob
{
	private BlobState? _state;

	public string Name { get; } = name;

	public Task<IBlob.ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken)
	{
		var state = _state;
		if (state is null)
		{
			return SpecializedTasks.Null<IBlob.ReadBlobInfo>();
		}

		return Task.FromResult<IBlob.ReadBlobInfo?>(state.ToReadInfo(mediaType));
	}

	public Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
	{
		while (true)
		{
			var capturedState = _state;
			var newState = new BlobState(Concat(capturedState?.Data, data.Span), NewConcurrencyToken(), timeProvider.GetUtcNow());
			if (ReferenceEquals(Interlocked.CompareExchange(ref _state, newState, capturedState), capturedState))
			{
				return Task.CompletedTask;
			}
		}
	}

	public Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken)
	{
		var state = _state;
		if (state is null)
		{
			return SpecializedTasks.Null<IBlob.BlobReadStreamResult>();
		}

		var result = new IBlob.BlobReadStreamResult(new MemoryStream(state.Data, writable: false), state.ToReadInfo(mediaType));
		return Task.FromResult<IBlob.BlobReadStreamResult?>(result);
	}

	public Task DeleteAsync(CancellationToken cancellationToken)
	{
		Interlocked.Exchange(ref _state, null);
		return Task.CompletedTask;
	}

	private static byte[] Concat(byte[]? existing, ReadOnlySpan<byte> data)
	{
		if (existing is null)
		{
			return data.ToArray();
		}

		var result = new byte[existing.Length + data.Length];
		existing.CopyTo(result, 0);
		data.CopyTo(result.AsSpan(existing.Length));
		return result;
	}

	private static string NewConcurrencyToken() => Guid.NewGuid().ToString();

	/// <summary>
	/// Immutable snapshot of the blob; <see cref="Data"/> is never mutated after construction, so readers can share it.
	/// </summary>
	private sealed record BlobState(byte[] Data, string ConcurrencyToken, DateTimeOffset LastModified)
	{
		public IBlob.ReadBlobInfo ToReadInfo(string? mediaType) => new(
			ConcurrencyToken,
			LastModified,
			MediaType: mediaType
		);
	}
}
