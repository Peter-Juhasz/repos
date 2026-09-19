using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.FileSystem;

public sealed class FileInfoAppendBlob(FileInfo file) : FileInfoBlobBase(file), IAppendBlob
{
	public Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
	{
		try
		{
			return System.IO.File.AppendAllBytesAsync(_path, data, cancellationToken);
		}
		catch (FileNotFoundException)
		{
			return System.IO.File.WriteAllBytesAsync(_path, data, cancellationToken);
		}
		catch (DirectoryNotFoundException)
		{
			Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
			return System.IO.File.WriteAllBytesAsync(_path, data, cancellationToken);
		}
	}

	public Task DeleteAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		file.Delete();
		return Task.CompletedTask;
	}
}

public static partial class Extensions
{
	extension(FileInfo file)
	{
		public IAppendBlob AsAppendBlob() => new FileInfoAppendBlob(file);
	}
}
