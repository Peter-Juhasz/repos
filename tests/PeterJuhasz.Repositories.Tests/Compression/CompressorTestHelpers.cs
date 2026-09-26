using PeterJuhasz.Repositories.Compression;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Compression;

internal static class CompressorTestHelpers
{
	/// <summary>Highly compressible text.</summary>
	public static readonly byte[] CompressibleData = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog. ", 200)));

	/// <summary>Incompressible bytes.</summary>
	public static readonly byte[] IncompressibleData = CreateRandom(4096);

	private static byte[] CreateRandom(int length)
	{
		var bytes = new byte[length];
		new Random(42).NextBytes(bytes);
		return bytes;
	}

	public static byte[] CompressToArray(this ICompressor compressor, byte[] input, int? capacity = null)
	{
		var size = capacity ?? (compressor.TryGetMaxCompressedLength(input.Length, out var maximum) ? maximum : input.Length * 2 + 1024);
		var output = new byte[size];
		Assert.IsTrue(compressor.Compress(input, output, out var written));
		Assert.IsLessThanOrEqualTo(size, written);
		return output[..written];
	}

	public static byte[] DecompressToArray(this ICompressor compressor, byte[] input, int capacity)
	{
		var output = new byte[capacity];
		Assert.IsTrue(compressor.Decompress(input, output, out var written));
		Assert.IsLessThanOrEqualTo(capacity, written);
		return output[..written];
	}

	public static async Task<byte[]> CompressStreamAsync(this IStreamCompressor compressor, byte[] input, CancellationToken cancellationToken)
	{
		var buffer = new MemoryStream();
		await using (var stream = compressor.Compress(buffer))
		{
			await stream.WriteAsync(input, cancellationToken);
		}
		return buffer.ToArray();
	}

	public static async Task<byte[]> DecompressStreamAsync(this IStreamCompressor compressor, byte[] input, CancellationToken cancellationToken)
	{
		await using var stream = compressor.Decompress(new MemoryStream(input));
		using var output = new MemoryStream();
		await stream.CopyToAsync(output, cancellationToken);
		return output.ToArray();
	}

	/// <summary>A stream that records whether it was disposed.</summary>
	public sealed class TrackingStream : MemoryStream
	{
		public TrackingStream() { }

		public TrackingStream(byte[] buffer) : base(buffer) { }

		public bool IsDisposed { get; private set; }

		protected override void Dispose(bool disposing)
		{
			IsDisposed = true;
			base.Dispose(disposing);
		}
	}
}
