namespace PeterJuhasz.Repositories.Blobs;

public interface IBlobPartition
{
	string? Path { get; }

	IBlob GetBlob(string name);

	IAppendBlob GetAppendBlob(string name);

	IBlobPartition GetSubPartition(string name);

	Task ClearAsync(CancellationToken cancellationToken);

	IAsyncEnumerable<IBlob> GetBlobs(CancellationToken cancellationToken);
}

public static partial class Extensions
{
	extension(IBlobPartition partition)
	{
		public IBlob GetBlob(string name, string extension) => partition.GetBlob(name + extension);

		public IAppendBlob GetAppendBlob(string name, string extension) => partition.GetAppendBlob(name + extension);
	}
}