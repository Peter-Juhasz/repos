using System.IO.Compression;

namespace PeterJuhasz.Repositories.Compression.GZip;

public sealed class GZipCompressor(GZipCompressionOptions options) : ICompressor, IStreamCompressor
{
	public static readonly GZipCompressor Maximum = new(GZipCompressionOptions.Maximum);

	private readonly ZLibCompressionOptions _options = new() { CompressionLevel = options.Level };

	public string ContentEncoding { get; } = "gzip";

	public Stream Compress(Stream input) => new GZipStream(input, _options, leaveOpen: false);

	public bool Compress(ReadOnlySpan<byte> input, Span<byte> output, out int written) => GZipEncoder.TryCompress(input, output, out written, options.Level);

	public Stream Decompress(Stream input) => new GZipStream(input, CompressionMode.Decompress, leaveOpen: false);

	public bool Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
	{
		// the decoder reports a zero-length destination as too small even for an empty payload
		if (output.IsEmpty)
		{
			Span<byte> scratch = stackalloc byte[1];
			var isEmpty = GZipDecoder.TryDecompress(input, scratch, out var scratchWritten) && scratchWritten == 0;
			written = 0;
			return isEmpty;
		}

		return GZipDecoder.TryDecompress(input, output, out written);
	}

	public bool TryGetMaxCompressedLength(int inputLength, out int compressedLength)
	{
		var maxLength = GZipEncoder.GetMaxCompressedLength(inputLength);
		if (maxLength > int.MaxValue)
		{
			compressedLength = -1;
			return false;
		}

		compressedLength = (int)maxLength;
		return true;
	}
}