using System.IO.Compression;
using BrotliCompressionOptions = PeterJuhasz.Repositories.Compression.Brotli.BrotliCompressionOptions;

namespace PeterJuhasz.Repositories.Compression;

public sealed class BrotliCompressor(BrotliCompressionOptions options) : ICompressor, IStreamCompressor
{
	public static readonly BrotliCompressor Maximum = new(BrotliCompressionOptions.Maximum);

	public string ContentEncoding { get; } = "br";

	private readonly System.IO.Compression.BrotliCompressionOptions _streamOptions = new() { Quality = options.Quality };

	public Stream Compress(Stream input) => new BrotliStream(input, _streamOptions, leaveOpen: false);

	public bool Compress(ReadOnlySpan<byte> input, Span<byte> output, out int written) => BrotliEncoder.TryCompress(input, output, out written, options.Quality, options.Window);

	public Stream Decompress(Stream input) => new BrotliStream(input, CompressionMode.Decompress, leaveOpen: false);

	public bool Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written) => BrotliDecoder.TryDecompress(input, output, out written);

	public bool TryGetMaxCompressedLength(int inputLength, out int compressedLength)
	{
		compressedLength = BrotliEncoder.GetMaxCompressedLength(inputLength);
		return true;
	}
}