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
		try
		{
			var info = GetFresh();
			return Task.FromResult<Stream>(info.OpenWrite());

		}
		catch (DirectoryNotFoundException)
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);

			var info = GetFresh();
			return Task.FromResult<Stream>(info.OpenWrite());
		}
	}

	public Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		file.Delete();
		return Task.CompletedTask;
	}

	public Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		try
		{
			file.Delete();
			return SpecializedTasks.True;
		}
		catch (FileNotFoundException)
		{
			return SpecializedTasks.False;
		}
		catch (DirectoryNotFoundException)
		{
			return SpecializedTasks.False;
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
