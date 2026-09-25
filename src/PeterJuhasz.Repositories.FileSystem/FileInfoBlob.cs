using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.FileSystem;

public sealed class FileInfoBlob(FileInfo file) : FileInfoBlobBase(file), IBlob
{
	public Task<string> SetMetadataAsync(string concurrencyToken, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken) =>
		Task.FromException<string>(new NotSupportedException("Metadata is not supported by file system blobs."));

	public Task<bool> ExistsAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return System.IO.File.Exists(_path) switch
		{
			true => SpecializedTasks.True,
			false => SpecializedTasks.False,
		};
	}

	public async Task<IBlob.BlobReadResult?> ReadAsync(CancellationToken cancellationToken)
	{
		if (await OpenReadAsync(cancellationToken) is not { } result)
		{
			return null;
		}

		await using var stream = result.Value;
		var data = await BinaryData.FromStreamAsync(stream, cancellationToken);
		return new(data, result.Info);
	}

	public async Task<string> WriteAsync(ReadOnlyMemory<byte> data, string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureWriteConditionMet(concurrencyToken);

		try
		{
			await System.IO.File.WriteAllBytesAsync(_path, data, cancellationToken);
			return GetConcurrencyToken(GetFresh());
		}
		catch (DirectoryNotFoundException)
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

			await System.IO.File.WriteAllBytesAsync(_path, data, cancellationToken);
			return GetConcurrencyToken(GetFresh());
		}
	}

	public Task<Stream> OpenWriteAsync(string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EnsureWriteConditionMet(concurrencyToken);

		try
		{
			return Task.FromResult<Stream>(new FileStream(_path, FileMode.Create, FileAccess.Write));
		}
		catch (DirectoryNotFoundException)
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

			return Task.FromResult<Stream>(new FileStream(_path, FileMode.Create, FileAccess.Write));
		}
	}

	public Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// File.Delete is a no-op for a missing file, so there is no exception to catch and translate.
		// The check-then-delete race is accepted, as file system blobs don't support concurrency.
		// An atomic alternative would be opening with FileMode.Open + FileOptions.DeleteOnClose, but that
		// costs an extra open, needs read access, and on Windows leaves the file pending delete (blocking
		// re-creation) until every other handle is closed.
		EnsureExistsAndMatches(concurrencyToken);

		System.IO.File.Delete(_path);
		return Task.CompletedTask;
	}

	public Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		// See DeleteAsync for why this checks existence up front.
		if (!System.IO.File.Exists(_path))
		{
			return SpecializedTasks.False;
		}

		System.IO.File.Delete(_path);
		return SpecializedTasks.True;
	}

	// Concurrency tokens are only checked before the operation, not atomically with it, so a change
	// made in between (e.g. by another process) goes undetected.

	/// <summary>
	/// Write precondition: <see langword="null"/> requires the blob to not exist, <see cref="IBlob.AnyOrNoneConcurrencyToken"/> is unconditional,
	/// <see cref="IBlob.AnyConcurrencyToken"/> requires the blob to exist, any other value requires a matching token.
	/// </summary>
	private void EnsureWriteConditionMet(string? concurrencyToken)
	{
		if (concurrencyToken == IBlob.AnyOrNoneConcurrencyToken)
		{
			return;
		}

		var file = GetFresh();
		var isMet = concurrencyToken switch
		{
			null => !file.Exists,
			IBlob.AnyConcurrencyToken => file.Exists,
			_ => file.Exists && GetConcurrencyToken(file) == concurrencyToken,
		};
		if (!isMet)
		{
			throw new ConflictException(concurrencyToken ?? "EXISTS");
		}
	}

	/// <summary>
	/// If-Match precondition: the blob must exist, and <paramref name="concurrencyToken"/> must be <see cref="IBlob.AnyConcurrencyToken"/> or match its token.
	/// </summary>
	private void EnsureExistsAndMatches(string concurrencyToken)
	{
		var file = GetFresh();
		if (!file.Exists)
		{
			throw new NotFoundException($"Blob '{Name}' not found.");
		}

		if (concurrencyToken != IBlob.AnyConcurrencyToken && GetConcurrencyToken(file) != concurrencyToken)
		{
			throw new ConflictException(concurrencyToken);
		}
	}
}

public static partial class Extensions
{
	extension(FileInfo file)
	{
		public IBlob AsBlob() => new FileInfoBlob(file);
	}
}
