using PeterJuhasz.Repositories.Compression;
using System.IO.Compression;
using BrotliCompressionOptions = PeterJuhasz.Repositories.Compression.Brotli.BrotliCompressionOptions;
using static PeterJuhasz.Repositories.Tests.Compression.CompressorTestHelpers;

namespace PeterJuhasz.Repositories.Tests.Compression;

[TestClass]
public class BrotliCompressorTests(TestContext testContext)
{
	private static readonly BrotliCompressor Compressor = BrotliCompressor.Maximum;

	private CancellationToken CT => testContext.CancellationToken;

	[TestMethod]
	public void ContentEncoding_IsBr()
	{
		Assert.AreEqual("br", ((ICompressor)Compressor).ContentEncoding);
		Assert.AreEqual("br", ((IStreamCompressor)Compressor).ContentEncoding);
	}

	// TryGetMaxCompressedLength

	[TestMethod]
	[DataRow(0)]
	[DataRow(1)]
	[DataRow(4096)]
	public void TryGetMaxCompressedLength_MatchesEncoder(int inputLength)
	{
		Assert.IsTrue(Compressor.TryGetMaxCompressedLength(inputLength, out var length));
		Assert.AreEqual(BrotliEncoder.GetMaxCompressedLength(inputLength), length);
	}

	[TestMethod]
	public void TryGetMaxCompressedLength_FitsIncompressibleData()
	{
		Assert.IsTrue(Compressor.TryGetMaxCompressedLength(IncompressibleData.Length, out var length));
		Assert.IsTrue(Compressor.Compress(IncompressibleData, new byte[length], out _));
	}

	// Compress / Decompress (span)

	[TestMethod]
	public void Compress_ReducesCompressibleData()
	{
		Assert.IsLessThan(CompressibleData.Length / 10, Compressor.CompressToArray(CompressibleData).Length);
	}

	[TestMethod]
	public void Compress_ThenDecompress_RoundTrips()
	{
		var compressed = Compressor.CompressToArray(CompressibleData);

		Assert.AreSequenceEqual(CompressibleData, Compressor.DecompressToArray(compressed, CompressibleData.Length));
	}

	[TestMethod]
	public void Compress_ThenDecompress_Incompressible_RoundTrips()
	{
		var compressed = Compressor.CompressToArray(IncompressibleData);

		Assert.AreSequenceEqual(IncompressibleData, Compressor.DecompressToArray(compressed, IncompressibleData.Length));
	}

	[TestMethod]
	public void Compress_ThenDecompress_Empty_RoundTrips()
	{
		var compressed = Compressor.CompressToArray([]);

		Assert.IsEmpty(Compressor.DecompressToArray(compressed, 0));
	}

	[TestMethod]
	public void Compress_OutputTooSmall_ReturnsFalse()
	{
		Assert.IsFalse(Compressor.Compress(IncompressibleData, new byte[16], out _));
	}

	[TestMethod]
	public void Compress_UsesQuality()
	{
		var fastest = new BrotliCompressor(new BrotliCompressionOptions(Quality: 0, Window: 10));

		Assert.IsGreaterThan(Compressor.CompressToArray(CompressibleData).Length, fastest.CompressToArray(CompressibleData).Length);
	}

	[TestMethod]
	public void Compress_OutputIsReadableByBrotliStream()
	{
		var compressed = Compressor.CompressToArray(CompressibleData);

		using var stream = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress);
		using var output = new MemoryStream();
		stream.CopyTo(output);
		Assert.AreSequenceEqual(CompressibleData, output.ToArray());
	}

	[TestMethod]
	public void Decompress_ReadsBrotliStreamOutput()
	{
		var buffer = new MemoryStream();
		using (var stream = new BrotliStream(buffer, CompressionLevel.Fastest))
		{
			stream.Write(CompressibleData);
		}

		Assert.AreSequenceEqual(CompressibleData, Compressor.DecompressToArray(buffer.ToArray(), CompressibleData.Length));
	}

	[TestMethod]
	public void Decompress_OutputTooSmall_ReturnsFalse()
	{
		var compressed = Compressor.CompressToArray(CompressibleData);

		Assert.IsFalse(Compressor.Decompress(compressed, new byte[CompressibleData.Length - 1], out _));
	}

	[TestMethod]
	public void Decompress_InvalidData_ReturnsFalse()
	{
		Assert.IsFalse(Compressor.Decompress(IncompressibleData, new byte[IncompressibleData.Length * 4], out _));
	}

	// Compress / Decompress (stream)

	[TestMethod]
	public async Task CompressStream_ThenDecompressStream_RoundTrips()
	{
		var compressed = await Compressor.CompressStreamAsync(CompressibleData, CT);

		Assert.IsLessThan(CompressibleData.Length / 10, compressed.Length);
		Assert.AreSequenceEqual(CompressibleData, await Compressor.DecompressStreamAsync(compressed, CT));
	}

	[TestMethod]
	public async Task CompressStream_ThenDecompressSpan_RoundTrips()
	{
		var compressed = await Compressor.CompressStreamAsync(CompressibleData, CT);

		Assert.AreSequenceEqual(CompressibleData, Compressor.DecompressToArray(compressed, CompressibleData.Length));
	}

	[TestMethod]
	public async Task CompressSpan_ThenDecompressStream_RoundTrips()
	{
		var compressed = Compressor.CompressToArray(CompressibleData);

		Assert.AreSequenceEqual(CompressibleData, await Compressor.DecompressStreamAsync(compressed, CT));
	}

	[TestMethod]
	public async Task CompressStream_UsesQuality()
	{
		var fastest = new BrotliCompressor(new BrotliCompressionOptions(Quality: 0, Window: 10));

		Assert.IsGreaterThan((await Compressor.CompressStreamAsync(CompressibleData, CT)).Length, (await fastest.CompressStreamAsync(CompressibleData, CT)).Length);
	}

	[TestMethod]
	public void CompressStream_DisposesInnerStream()
	{
		var inner = new TrackingStream();

		Compressor.Compress(inner).Dispose();

		Assert.IsTrue(inner.IsDisposed);
	}

	[TestMethod]
	public void DecompressStream_DisposesInnerStream()
	{
		var inner = new TrackingStream(Compressor.CompressToArray(CompressibleData));

		Compressor.Decompress(inner).Dispose();

		Assert.IsTrue(inner.IsDisposed);
	}
}
