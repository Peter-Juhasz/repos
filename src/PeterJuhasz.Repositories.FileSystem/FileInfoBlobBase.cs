using PeterJuhasz.Repositories.Blobs;
using System.Runtime.CompilerServices;

namespace PeterJuhasz.Repositories.FileSystem;

public abstract class FileInfoBlobBase(FileInfo file)
{
	protected readonly string _path = file.FullName;

	public FileInfo File => file;

	public string Name => file.Name;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	protected static IBlob.ReadBlobInfo ToReadInfo(FileInfo file) => new(
		ConcurrencyToken: GetConcurrencyToken(file),
		LastModified: new(file.LastWriteTimeUtc, TimeSpan.Zero)
	);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	protected static string GetConcurrencyToken(FileInfo file) => file.LastWriteTimeUtc.ToString("O");

	protected FileInfo GetFresh() => new(_path);

	public Task<IBlob.ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		var file = GetFresh();
		if (!file.Exists)
		{
			return SpecializedTasks.Null<IBlob.ReadBlobInfo>();
		}

		return Task.FromResult<IBlob.ReadBlobInfo?>(ToReadInfo(file));
	}

	public Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken)
	{
		try
		{
			var info = GetFresh();
			return Task.FromResult<IBlob.BlobReadStreamResult?>(new(
				Value: info.OpenRead(),
				Info: ToReadInfo(info)
			));
		}
		catch (FileNotFoundException)
		{
			return SpecializedTasks.Null<IBlob.BlobReadStreamResult>();
		}
		catch (DirectoryNotFoundException)
		{
			return SpecializedTasks.Null<IBlob.BlobReadStreamResult>();
		}
	}
}