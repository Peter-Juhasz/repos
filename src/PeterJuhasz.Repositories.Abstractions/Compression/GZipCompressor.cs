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

		// the gzip trailer is only written when the stream is disposed
		using (var gzipStream = new GZipStream(buffer, _options, leaveOpen: true))
		{
			gzipStream.Write(input);
		}

		var compressed = buffer.TryGetBuffer(out var data) ? data.AsSpan() : buffer.ToArray();
		if (!compressed.TryCopyTo(output))
		{
			written = 0;
			return false;
		}

		written = compressed.Length;
		return true;
	}

	public Stream Decompress(Stream input) => new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);

	public bool Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
	{
		using var _ = MemoryStreamPool.GetPooledObject(out var buffer);
		buffer.Write(input);
		buffer.Position = 0;

		try
		{
			using var gzipStream = new GZipStream(buffer, CompressionMode.Decompress, leaveOpen: true);
			written = 0;
			while (written < output.Length)
			{
				var read = gzipStream.Read(output[written..]);
				if (read == 0)
				{
					return true;
				}
				written += read;
			}

			// output is full, succeed only if there is nothing left to decompress
			if (gzipStream.ReadByte() != -1)
			{
				written = 0;
				return false;
			}

			return true;
		}
		catch (InvalidDataException)
		{
			written = 0;
			return false;
		}
	}
}