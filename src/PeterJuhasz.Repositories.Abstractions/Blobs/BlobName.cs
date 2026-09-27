namespace PeterJuhasz.Repositories.Blobs;

internal static class BlobName
{
	/// <summary>
	/// The last segment of a full blob name (see <see cref="IBlob.Name"/>), the name of the blob within its partition.
	/// </summary>
	public static ReadOnlySpan<char> GetLastSegment(string name) => name.AsSpan(name.LastIndexOf('/') + 1);
}
