using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.InMemory;

public sealed class InMemoryBlob(string name, TimeProvider timeProvider) : IBlob
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

		return Task.FromResult<IBlob.ReadBlobInfo?>(state.ToReadInfo());
	}

	public Task<string> SetMetadataAsync(string concurrencyToken, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken)
	{
		var capturedState = _state;
		if (capturedState is null)
		{
			throw new NotFoundException($"Blob '{Name}' not found.");
		}
		if (capturedState.ConcurrencyToken != concurrencyToken)
		{
			throw new ConflictException(concurrencyToken);
		}
		var newState = capturedState with
		{
			WriteInfo = (capturedState.WriteInfo ?? new()) with
			{
				Metadata = metadata,
			}
		};
		var oldState = Interlocked.CompareExchange(ref _state, newState, capturedState);
		if (!ReferenceEquals(oldState, capturedState))
		{
			throw new ConflictException(concurrencyToken);
		}
		return Task.FromResult(newState.ConcurrencyToken);
	}

	public Task<bool> ExistsAsync(CancellationToken cancellationToken)
	{
		return _state is not null ? SpecializedTasks.True : SpecializedTasks.False;
	}

	public Task<IBlob.BlobReadResult?> ReadAsync(CancellationToken cancellationToken)
	{
		var state = _state;
		if (state is null)
		{
			return SpecializedTasks.Null<IBlob.BlobReadResult>();
		}

		var result = new IBlob.BlobReadResult(state.Data, state.ToReadInfo());
		return Task.FromResult<IBlob.BlobReadResult?>(result);
	}

	public Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken)
	{
		var state = _state;
		if (state is null)
		{
			return SpecializedTasks.Null<IBlob.BlobReadStreamResult>();
		}

		var result = new IBlob.BlobReadStreamResult(state.Data.ToStream(), state.ToReadInfo());
		return Task.FromResult<IBlob.BlobReadStreamResult?>(result);
	}

	public Task<string> WriteAsync(ReadOnlyMemory<byte> data, string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		var capturedState = _state;
		if (capturedState != null)
		{
			if (capturedState.ConcurrencyToken != concurrencyToken)
			{
				throw new ConflictException(concurrencyToken ?? "EXISTS");
			}
		}
		else if (concurrencyToken != null)
		{
			throw new ConflictException(concurrencyToken);
		}

		var newState = new BlobState(new BinaryData(data.ToArray()), Guid.NewGuid().ToString(), timeProvider.GetUtcNow(), options);
		var oldState = Interlocked.CompareExchange(ref _state, newState, capturedState);
		if (!ReferenceEquals(oldState, capturedState))
		{
			throw new ConflictException(concurrencyToken ?? "EXISTS");
		}

		return Task.FromResult(newState.ConcurrencyToken);
	}

	public Task<Stream> OpenWriteAsync(string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		var capturedState = _state;
		if (capturedState != null)
		{
			if (capturedState.ConcurrencyToken != concurrencyToken)
			{
				throw new ConflictException(concurrencyToken ?? "EXISTS");
			}
		}
		else if (concurrencyToken != null)
		{
			throw new ConflictException(concurrencyToken);
		}

		return Task.FromResult<Stream>(new WriteStream(this, options, capturedState, timeProvider));
	}

	public Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		if (_state is not { } state)
		{
			throw new NotFoundException($"Blob '{Name}' not found.");
		}

		if (state.ConcurrencyToken != concurrencyToken)
		{
			throw new ConflictException(concurrencyToken);
		}

		var oldState = Interlocked.CompareExchange(ref _state, state, null);
		if (!ReferenceEquals(oldState, state))
		{
			throw new ConflictException(concurrencyToken);
		}

		return Task.CompletedTask;
	}

	public Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		var oldState = Interlocked.Exchange(ref _state, null);
		return oldState is not null ? SpecializedTasks.True : SpecializedTasks.False;
	}

	private sealed record BlobState(BinaryData Data, string ConcurrencyToken, DateTimeOffset LastModified, IBlob.WriteBlobInfo? WriteInfo)
	{
		public IBlob.ReadBlobInfo ToReadInfo() => new(
			ConcurrencyToken,
			LastModified,
			ContentEncoding: WriteInfo?.ContentEncoding,
			MediaType: WriteInfo?.MediaType,
			Metadata: WriteInfo?.Metadata
		);
	}

	private sealed class WriteStream(InMemoryBlob blob, IBlob.WriteBlobInfo options, BlobState? capturedState, TimeProvider timeProvider) : MemoryStream
	{
		private bool _committed;

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				Commit();
			}

			base.Dispose(disposing);
		}

		public override ValueTask DisposeAsync()
		{
			Commit();
			return base.DisposeAsync();
		}

		private void Commit()
		{
			if (_committed)
			{
				return;
			}

			_committed = true;

			var newState = new BlobState(new BinaryData(ToArray()), Guid.NewGuid().ToString(), timeProvider.GetUtcNow(), options);
			var oldState = Interlocked.CompareExchange(ref blob._state, newState, capturedState);
			if (!ReferenceEquals(oldState, capturedState))
			{
				throw new ConflictException(capturedState?.ConcurrencyToken ?? "EXISTS");
			}
		}
	}
}
