using PeterJuhasz.Repositories.Compression;
using PeterJuhasz.Repositories.Compression.GZip;
using System.IO.Compression;
using static PeterJuhasz.Repositories.Tests.Compression.CompressorTestHelpers;

namespace PeterJuhasz.Repositories.Tests.Compression;

[TestClass]
public class GZipCompressorTests(TestContext testContext)
{
	private static readonly GZipCompressor Compressor = GZipCompressor.Maximum;

	private CancellationToken CT => testContext.CancellationToken;

	[TestMethod]
	public void ContentEncoding_IsGZip()
	{
		Assert.AreEqual("gzip", ((ICompressor)Compressor).ContentEncoding);
		Assert.AreEqual("gzip", ((IStreamCompressor)Compressor).ContentEncoding);
	}

	// Compress / Decompress (span)

	[TestMethod]
	public void Compress_ReducesCompressibleData()
	{
		Assert.IsLessThan(CompressibleData.Length / 10, Compressor.CompressToArray(CompressibleData).Length);
	}

	[TestMethod]
	public void Compress_WritesGZipHeader()
	{
		var compressed = Compressor.CompressToArray(CompressibleData);

		Assert.AreEqual(0x1F, compressed[0]);
		Assert.AreEqual(0x8B, compressed[1]);
	}

	[TestMethod]
	public void Compress_OutputIsReadableByGZipStream()
	{
		var compressed = Compressor.CompressToArray(CompressibleData);

		using var stream = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
		using var output = new MemoryStream();
		stream.CopyTo(output);
		Assert.AreSequenceEqual(CompressibleData, output.ToArray());
	}

	[TestMethod]
	public void Compress_OutputIsComplete()
	{
		var compressed = Compressor.CompressToArray(CompressibleData);

		// gzip trailer: CRC32 followed by the uncompressed length (mod 2^32), little endian
		Assert.AreEqual(CompressibleData.Length, BitConverter.ToInt32(compressed, compressed.Length - 4));
	}

	[TestMethod]
	public void Compress_Repeatedly_ProducesIndependentOutputs()
	{
		var first = Compressor.CompressToArray(CompressibleData);
		var second = Compressor.CompressToArray(IncompressibleData);
		var third = Compressor.CompressToArray(CompressibleData);

		Assert.AreSequenceEqual(first, third);
		Assert.AreSequenceEqual(IncompressibleData, Compressor.DecompressToArray(second, IncompressibleData.Length));
	}

	[TestMethod]
	public void Compress_OutputTooSmall_ReturnsFalse()
	{
		Assert.IsFalse(Compressor.Compress(IncompressibleData, new byte[16], out _));
	}

	[TestMethod]
	public void Compress_UsesLevel()
	{
		var fastest = new GZipCompressor(new GZipCompressionOptions(Level: 1));

		Assert.IsGreaterThan(Compressor.CompressToArray(CompressibleData).Length, fastest.CompressToArray(CompressibleData).Length);
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
	public void Decompress_ReadsGZipStreamOutput()
	{
		var buffer = new MemoryStream();
		using (var stream = new GZipStream(buffer, CompressionLevel.Fastest))
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
	public async Task CompressStream_UsesLevel()
	{
		var fastest = new GZipCompressor(new GZipCompressionOptions(Level: 1));

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
	public async Task DecompressStream_DisposesInnerStream()
	{
		var inner = new TrackingStream(await Compressor.CompressStreamAsync(CompressibleData, CT));

		Compressor.Decompress(inner).Dispose();

		Assert.IsTrue(inner.IsDisposed);
	}
}
