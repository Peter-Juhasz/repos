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
		while (true)
		{
			var capturedState = GetExistingMatchingState(concurrencyToken);
			var newState = capturedState with
			{
				ConcurrencyToken = NewConcurrencyToken(),
				LastModified = timeProvider.GetUtcNow(),
				WriteInfo = (capturedState.WriteInfo ?? new()) with
				{
					Metadata = metadata,
				}
			};
			if (ReferenceEquals(Interlocked.CompareExchange(ref _state, newState, capturedState), capturedState))
			{
				return Task.FromResult(newState.ConcurrencyToken);
			}
		}
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
		var newState = CreateState(data.ToArray(), options);
		Replace(concurrencyToken, newState);
		return Task.FromResult(newState.ConcurrencyToken);
	}

	public Task<Stream> OpenWriteAsync(string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		EnsureWriteConditionMet(_state, concurrencyToken);
		return Task.FromResult<Stream>(new WriteStream(this, concurrencyToken, options));
	}

	public Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		while (true)
		{
			var capturedState = GetExistingMatchingState(concurrencyToken);
			if (ReferenceEquals(Interlocked.CompareExchange(ref _state, null, capturedState), capturedState))
			{
				return Task.CompletedTask;
			}
		}
	}

	public Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		var oldState = Interlocked.Exchange(ref _state, null);
		return oldState is not null ? SpecializedTasks.True : SpecializedTasks.False;
	}

	private BlobState CreateState(byte[] data, IBlob.WriteBlobInfo options) =>
		new(new BinaryData(data, options.MediaType), NewConcurrencyToken(), timeProvider.GetUtcNow(), options);

	private static string NewConcurrencyToken() => Guid.NewGuid().ToString();

	/// <summary>
	/// Write precondition: <see langword="null"/> requires the blob to not exist, <see cref="IBlob.AnyOrNoneConcurrencyToken"/> is unconditional,
	/// <see cref="IBlob.AnyConcurrencyToken"/> requires the blob to exist, any other value requires a matching token.
	/// </summary>
	private static void EnsureWriteConditionMet(BlobState? state, string? concurrencyToken)
	{
		var isMet = concurrencyToken switch
		{
			null => state is null,
			IBlob.AnyOrNoneConcurrencyToken => true,
			IBlob.AnyConcurrencyToken => state is not null,
			_ => state?.ConcurrencyToken == concurrencyToken,
		};
		if (!isMet)
		{
			throw new ConflictException(concurrencyToken ?? "EXISTS");
		}
	}

	/// <summary>
	/// Atomically replaces the state if the write precondition is met at the time of the swap.
	/// </summary>
	private void Replace(string? concurrencyToken, BlobState newState)
	{
		while (true)
		{
			var capturedState = _state;
			EnsureWriteConditionMet(capturedState, concurrencyToken);
			if (ReferenceEquals(Interlocked.CompareExchange(ref _state, newState, capturedState), capturedState))
			{
				return;
			}
		}
	}

	/// <summary>
	/// If-Match precondition: the blob must exist, and <paramref name="concurrencyToken"/> must be <see cref="IBlob.AnyConcurrencyToken"/> or match its token.
	/// A specific token on a missing blob is a conflict (the version it refers to is gone), so optimistic retries can recover.
	/// </summary>
	private BlobState GetExistingMatchingState(string concurrencyToken)
	{
		if (_state is not { } state)
		{
			if (concurrencyToken != IBlob.AnyConcurrencyToken)
			{
				throw new ConflictException(concurrencyToken);
			}

			throw new NotFoundException($"Blob '{Name}' not found.");
		}

		if (concurrencyToken != IBlob.AnyConcurrencyToken && state.ConcurrencyToken != concurrencyToken)
		{
			throw new ConflictException(concurrencyToken);
		}

		return state;
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

	private sealed class WriteStream(InMemoryBlob blob, string? concurrencyToken, IBlob.WriteBlobInfo options) : MemoryStream
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

			blob.Replace(concurrencyToken, blob.CreateState(ToArray(), options));
		}
	}
}
