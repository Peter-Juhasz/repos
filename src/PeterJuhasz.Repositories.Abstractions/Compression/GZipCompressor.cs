using System.IO.Compression;

namespace PeterJuhasz.Repositories.Compression.GZip;

public sealed class GZipCompressor(GZipCompressionOptions options) : ICompressor, IStreamCompressor
{
	public static readonly GZipCompressor Maximum = new(GZipCompressionOptions.Maximum);

	private readonly ZLibCompressionOptions _options = new() { CompressionLevel = options.Level };

	public string ContentEncoding { get; } = "gzip";

	public Stream Compress(Stream input) => new GZipStream(input, _options, leaveOpen: false);

	public bool Compress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
	{
		using var _ = MemoryStreamPool.GetPooledObject(out var buffer);
		using var gzipStream = new GZipStream(buffer, _options, leaveOpen: true);
		gzipStream.Write(input);
		gzipStream.Flush();
		buffer.Flush();
		written = (int)buffer.Position;
		if (buffer.TryGetBuffer(out var data))
		{
			data.CopyTo(output);
		}
		else
		{
			var array = buffer.ToArray();
			array.AsSpan().CopyTo(output);
		}
		return true;
	}

	public Stream Decompress(Stream input) => new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);

	public bool Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
	{
		using var _ = MemoryStreamPool.GetPooledObject(out var buffer);
		using var gzipStream = new GZipStream(buffer, CompressionMode.Decompress, leaveOpen: true);
		gzipStream.Write(input);
		gzipStream.Flush();
		buffer.Flush();
		written = (int)buffer.Position;
		if (buffer.TryGetBuffer(out var data))
		{
			data.CopyTo(output);
		}
		else
		{
			var array = buffer.ToArray();
			array.AsSpan().CopyTo(output);
		}
		return true;
	}
}