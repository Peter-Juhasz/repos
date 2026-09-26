using PeterJuhasz.Repositories.Compression;
using static PeterJuhasz.Repositories.Tests.Compression.CompressorTestHelpers;

namespace PeterJuhasz.Repositories.Tests.Compression;

[TestClass]
public class IdentityCompressorTests
{
	private static readonly IdentityCompressor Compressor = IdentityCompressor.Default;

	[TestMethod]
	public void ContentEncoding_IsIdentity()
	{
		Assert.AreEqual("identity", ((ICompressor)Compressor).ContentEncoding);
		Assert.AreEqual("identity", ((IStreamCompressor)Compressor).ContentEncoding);
	}

	[TestMethod]
	[DataRow(0)]
	[DataRow(4096)]
	public void TryGetMaxCompressedLength_IsInputLength(int inputLength)
	{
		Assert.IsTrue(Compressor.TryGetMaxCompressedLength(inputLength, out var length));
		Assert.AreEqual(inputLength, length);
	}

	[TestMethod]
	public void Compress_CopiesInput()
	{
		Assert.AreSequenceEqual(CompressibleData, Compressor.CompressToArray(CompressibleData));
	}

	[TestMethod]
	public void Compress_LargerOutput_WritesInputLength()
	{
		Assert.IsTrue(Compressor.Compress(CompressibleData, new byte[CompressibleData.Length + 10], out var written));
		Assert.AreEqual(CompressibleData.Length, written);
	}

	[TestMethod]
	public void Compress_OutputTooSmall_ReturnsFalse()
	{
		Assert.IsFalse(Compressor.Compress(CompressibleData, new byte[CompressibleData.Length - 1], out var written));
		Assert.AreEqual(0, written);
	}

	[TestMethod]
	public void Decompress_CopiesInput()
	{
		Assert.AreSequenceEqual(CompressibleData, Compressor.DecompressToArray(CompressibleData, CompressibleData.Length));
	}

	[TestMethod]
	public void Decompress_OutputTooSmall_ReturnsFalse()
	{
		Assert.IsFalse(Compressor.Decompress(CompressibleData, new byte[CompressibleData.Length - 1], out var written));
		Assert.AreEqual(0, written);
	}

	[TestMethod]
	public void Empty_RoundTrips()
	{
		Assert.IsEmpty(Compressor.DecompressToArray(Compressor.CompressToArray([]), 0));
	}

	[TestMethod]
	public void CompressStream_ReturnsSameStream()
	{
		using var stream = new MemoryStream();

		Assert.AreSame(stream, Compressor.Compress(stream));
	}

	[TestMethod]
	public void DecompressStream_ReturnsSameStream()
	{
		using var stream = new MemoryStream();

		Assert.AreSame(stream, Compressor.Decompress(stream));
	}
}
