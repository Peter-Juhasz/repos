using PeterJuhasz.Repositories.Compression;
using PeterJuhasz.Repositories.Compression.Brotli;
using PeterJuhasz.Repositories.Compression.GZip;

namespace PeterJuhasz.Repositories.Tests.Compression;

[TestClass]
public class CompressionOptionsTests
{
	// FromContentEncoding

	[TestMethod]
	[DataRow(null)]
	[DataRow("identity")]
	public void FromContentEncoding_NoCompression_ReturnsNull(string? contentEncoding)
	{
		Assert.IsNull(CompressionOptions.FromContentEncoding(contentEncoding));
	}

	[TestMethod]
	public void FromContentEncoding_Br_ReturnsMaximumBrotli()
	{
		Assert.AreEqual(BrotliCompressionOptions.Maximum, CompressionOptions.FromContentEncoding("br"));
	}

	[TestMethod]
	public void FromContentEncoding_GZip_ReturnsMaximumGZip()
	{
		Assert.AreEqual(GZipCompressionOptions.Maximum, CompressionOptions.FromContentEncoding("gzip"));
	}

	[TestMethod]
	public void FromContentEncoding_Unknown_Throws()
	{
		Assert.ThrowsExactly<NotSupportedException>(() => CompressionOptions.FromContentEncoding("deflate"));
	}

	// CreateCompressor / CreateStreamCompressor

	[TestMethod]
	public void CreateCompressor_MaximumBrotli_ReturnsSharedInstance()
	{
		Assert.AreSame(BrotliCompressor.Maximum, CompressionOptions.CreateCompressor(BrotliCompressionOptions.Maximum));
		Assert.AreSame(BrotliCompressor.Maximum, CompressionOptions.CreateCompressor(new BrotliCompressionOptions()));
		Assert.AreSame(BrotliCompressor.Maximum, CompressionOptions.CreateStreamCompressor(BrotliCompressionOptions.Maximum));
	}

	[TestMethod]
	public void CreateCompressor_OtherBrotli_ReturnsNewInstance()
	{
		var options = new BrotliCompressionOptions(Quality: 5);

		var compressor = CompressionOptions.CreateCompressor(options);

		Assert.IsInstanceOfType<BrotliCompressor>(compressor);
		Assert.AreNotSame(BrotliCompressor.Maximum, compressor);
		Assert.IsInstanceOfType<BrotliCompressor>(CompressionOptions.CreateStreamCompressor(options));
	}

	[TestMethod]
	public void CreateCompressor_MaximumGZip_ReturnsSharedInstance()
	{
		Assert.AreSame(GZipCompressor.Maximum, CompressionOptions.CreateCompressor(GZipCompressionOptions.Maximum));
		Assert.AreSame(GZipCompressor.Maximum, CompressionOptions.CreateStreamCompressor(new GZipCompressionOptions()));
	}

	[TestMethod]
	public void CreateCompressor_OtherGZip_ReturnsNewInstance()
	{
		var options = new GZipCompressionOptions(Level: 1);

		var compressor = CompressionOptions.CreateCompressor(options);

		Assert.IsInstanceOfType<GZipCompressor>(compressor);
		Assert.AreNotSame(GZipCompressor.Maximum, compressor);
		Assert.IsInstanceOfType<GZipCompressor>(CompressionOptions.CreateStreamCompressor(options));
	}

	[TestMethod]
	public void CreateCompressor_Identity_ReturnsIdentity()
	{
		Assert.AreSame(IdentityCompressor.Default, CompressionOptions.CreateCompressor(IdentityCompressionOptions.Default));
		Assert.AreSame(IdentityCompressor.Default, CompressionOptions.CreateStreamCompressor(IdentityCompressionOptions.Default));
	}

	[TestMethod]
	[DataRow("br")]
	[DataRow("gzip")]
	public void FromContentEncoding_ThenCreate_MatchesContentEncoding(string contentEncoding)
	{
		var options = CompressionOptions.FromContentEncoding(contentEncoding);
		Assert.IsNotNull(options);

		Assert.AreEqual(contentEncoding, CompressionOptions.CreateCompressor(options).ContentEncoding);
		Assert.AreEqual(contentEncoding, CompressionOptions.CreateStreamCompressor(options).ContentEncoding);
	}
}
