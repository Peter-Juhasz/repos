using PeterJuhasz.Repositories.Blobs;
using System.Runtime.CompilerServices;

namespace PeterJuhasz.Repositories.FileSystem;

public sealed class DirectoryInfoBlobPartition(DirectoryInfo root) : IBlobPartition
{
	private static readonly EnumerationOptions EnumerationOptions = new()
	{
		RecurseSubdirectories = true,
		AttributesToSkip = 0,
		IgnoreInaccessible = false,
	};

	public string? Path => root.FullName;

	public DirectoryInfo Directory => root;

	public IBlob GetBlob(string name) => new FileInfo(System.IO.Path.Combine(root.FullName, name)).AsBlob();

	public IAppendBlob GetAppendBlob(string name) => new FileInfo(System.IO.Path.Combine(root.FullName, name)).AsAppendBlob();

	public IBlobPartition GetSubPartition(string name) => new DirectoryInfo(System.IO.Path.Combine(root.FullName, name)).GetPartition();

	public async IAsyncEnumerable<IBlob> GetBlobs([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		foreach (var file in root.EnumerateFiles("*", EnumerationOptions))
		{
			cancellationToken.ThrowIfCancellationRequested();

			yield return file.AsBlob();
		}
	}

	public async Task ClearAsync(CancellationToken cancellationToken)
	{
		foreach (var file in root.EnumerateFiles("*", EnumerationOptions))
		{
			cancellationToken.ThrowIfCancellationRequested();

			file.Delete();
		}
	}
}

public static partial class Extensions
{
	extension(DirectoryInfo directory)
	{
		public IBlobPartition GetPartition() => new DirectoryInfoBlobPartition(directory);

		public IBlobPartition GetPartition(string path) => directory.GetPartition().GetSubPartition(path);

		public IBlobPartition GetPartition(params ReadOnlySpan<string> segments)
		{
			var path = System.IO.Path.Combine(segments);
			return directory.GetPartition().GetSubPartition(path);
		}
	}
}
