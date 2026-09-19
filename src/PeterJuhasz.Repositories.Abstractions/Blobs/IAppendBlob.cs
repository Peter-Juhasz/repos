namespace PeterJuhasz.Repositories.Blobs;

public interface IAppendBlob
{
	string Name { get; }

	Task<IBlob.ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken);

	Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

	Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken);

	Task DeleteAsync(CancellationToken cancellationToken);
}
