using PeterJuhasz.Repositories.Compression;

namespace PeterJuhasz.Repositories.Abstractions;

public readonly record struct RawStreamResult(Stream Stream, DateTimeOffset LastModified, string ETag, string? Encoding = null) : IAsyncDisposable
{
	public Stream GetDecodedStream()
	{
		if (CompressionOptions.FromContentEncoding(Encoding) is CompressionOptions compression &&
			CompressionOptions.CreateStreamCompressor(compression) is IStreamCompressor compressor)
		{
			return compressor.Decompress(Stream);
		}

		return Stream;
	}

	public ValueTask DisposeAsync()
	{
		return ((IAsyncDisposable)Stream).DisposeAsync();
	}
}
