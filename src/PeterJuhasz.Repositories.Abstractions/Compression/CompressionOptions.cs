using PeterJuhasz.Repositories.Compression.Brotli;
using PeterJuhasz.Repositories.Compression.GZip;
using PeterJuhasz.Repositories.Compression.Zstd;

namespace PeterJuhasz.Repositories.Compression
{
	public abstract record class CompressionOptions()
	{
		public static CompressionOptions? FromContentEncoding(string? contentEncoding) => contentEncoding switch
		{
			null or "identity" => null,
			"br" => BrotliCompressionOptions.Maximum,
			"gzip" => GZipCompressionOptions.Maximum,
			"zstd" => new ZstdCompressionOptions(),
			_ => throw new NotSupportedException($"Unsupported content encoding: {contentEncoding}")
		};

		public static ICompressor CreateCompressor(CompressionOptions options) => options switch
		{
			BrotliCompressionOptions { Quality: 11, Window: 24 } => BrotliCompressor.Maximum,
			BrotliCompressionOptions brotli => new BrotliCompressor(brotli),
			GZipCompressionOptions { Level: 9 } => GZipCompressor.Maximum,
			GZipCompressionOptions gzip => new GZipCompressor(gzip),
			IdentityCompressionOptions => IdentityCompressor.Default,
			_ => throw new NotSupportedException($"Unsupported compression options type: {options.GetType().FullName}")
		};

		public static IStreamCompressor CreateStreamCompressor(CompressionOptions options) => options switch
		{
			BrotliCompressionOptions { Quality: 11, Window: 24 } => BrotliCompressor.Maximum,
			BrotliCompressionOptions brotli => new BrotliCompressor(brotli),
			GZipCompressionOptions { Level: 9 } => GZipCompressor.Maximum,
			GZipCompressionOptions gzip => new GZipCompressor(gzip),
			IdentityCompressionOptions => IdentityCompressor.Default,
			_ => throw new NotSupportedException($"Unsupported compression options type: {options.GetType().FullName}")
		};
	}

	public record class IdentityCompressionOptions() : CompressionOptions()
	{
		public static readonly IdentityCompressionOptions Default = new();
	}

	namespace Brotli
	{
		public record class BrotliCompressionOptions(
			int Quality = 11,
			int Window = 24
		) : CompressionOptions()
		{
			public static readonly BrotliCompressionOptions Maximum = new(Quality: 11, Window: 24);
		}
	}

	namespace GZip
	{
		public record class GZipCompressionOptions(
			int Level = 9
		) : CompressionOptions()
		{
			public static readonly GZipCompressionOptions Maximum = new(Level: 9);
		}
	}

	namespace Zstd
	{
		public record class ZstdCompressionOptions(
			int Level = 22
		) : CompressionOptions();
	}
}