using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.Compression.Brotli;
using System.Buffers;
using System.Collections.Frozen;

namespace PeterJuhasz.Repositories.Compression;

public sealed class CompressedBlob(IBlob inner, ICompressor compressor, IStreamCompressor streamCompressor) : IBlob
{
	private const string UncompressedLengthKey = "UncompressedLength";


	public string Name => inner.Name;

	public IBlob Blob => inner;

	public Task<IBlob.ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken) => inner.GetInfoAsync(cancellationToken);

	public Task<string> SetMetadataAsync(string concurrencyToken, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken) => inner.SetMetadataAsync(concurrencyToken, metadata, cancellationToken);

	public Task<bool> ExistsAsync(CancellationToken cancellationToken) => inner.ExistsAsync(cancellationToken);

	public async Task<IBlob.BlobReadResult?> ReadAsync(CancellationToken cancellationToken)
	{
		var versionedData = await inner.ReadAsync(cancellationToken);
		if (versionedData == null)
		{
			return null;
		}

		if (versionedData.Info.ContentEncoding != compressor.ContentEncoding)
		{
			return versionedData;
		}

		// no length known, slow path
		if (versionedData.Info.Metadata == null ||
			!versionedData.Info.Metadata.TryGetValue(UncompressedLengthKey, out var uncompressedLengthStr) ||
			!uncompressedLengthStr.TryParseAs<int>(out var uncompressedLength))
		{
			using var compressedStream = versionedData.Value.ToStream();
			using var _ = MemoryStreamPool.GetPooledObject(out var buffer);
			using var wrappedStream = streamCompressor.Decompress(compressedStream);
			wrappedStream.CopyTo(buffer);
			var decompressedStreamData = new BinaryData(buffer.ToArray(), versionedData.Value.MediaType);
			return new(decompressedStreamData, versionedData.Info with
			{
				ContentEncoding = null
			});
		}

		var compressedData = versionedData.Value;
		var decompressionBuffer = new byte[uncompressedLength];
		compressor.Decompress(compressedData.ToMemory().Span, decompressionBuffer, out var bytesWritten);
		var decompressedData = new BinaryData(decompressionBuffer.AsMemory(0, bytesWritten), compressedData.MediaType);
		return new(decompressedData, versionedData.Info with
		{
			ContentEncoding = null,
		});
	}

	public async Task<string> WriteAsync(ReadOnlyMemory<byte> data, string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		if (options is { ContentEncoding: string })
		{
			return await inner.WriteAsync(data, concurrencyToken, options, cancellationToken);
		}

		var newMetadata = options.Metadata;
		if (newMetadata is null or { Count: 0 } || (newMetadata.Count == 1 && newMetadata.ContainsKey(UncompressedLengthKey)))
		{
			newMetadata = FrugalDictionary.Create(UncompressedLengthKey, data.Length.ToStringInvariant());
		}
		else
		{
			var mutableMetadata = new SmallDictionary<string, string>(capacity: newMetadata.Count + 1);
			foreach (var kvp in newMetadata)
			{
				mutableMetadata.Add(kvp.Key, kvp.Value);
			}
			mutableMetadata[UncompressedLengthKey] = data.Length.ToStringInvariant();
			newMetadata = mutableMetadata;
		}

		var blobInfo = options with
		{
			ContentEncoding = compressor.ContentEncoding,
			Metadata = newMetadata,
		};

		if (compressor.TryGetMaxCompressedLength(data.Length, out var maxCompressedLength))
		{
			using var owner = MemoryPool<byte>.Shared.Rent(maxCompressedLength);
			compressor.Compress(data.Span, owner.Memory.Span, out var compressedLength);
			return await inner.WriteAsync(owner.Memory[..compressedLength], concurrencyToken, blobInfo, cancellationToken);
		}
		else
		{
			// fallback to stream compression if we can't get the max compressed length
			using var _ = MemoryStreamPool.GetPooledObject(out var buffer);
			using var compressorStream = streamCompressor.Compress(buffer);
			compressorStream.Write(data.Span);
			compressorStream.Flush();
			buffer.Flush();
			buffer.Position = 0;
			ReadOnlyMemory<byte> compressedBuffer;
			if (buffer.TryGetBuffer(out var innerBuffer))
			{
				compressedBuffer = innerBuffer;
			}
			else
			{
				compressedBuffer = buffer.ToArray();
			}
			return await inner.WriteAsync(compressedBuffer, concurrencyToken, blobInfo, cancellationToken);
		}
	}


	public async Task<IBlob.BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken)
	{
		var stream = await inner.OpenReadAsync(cancellationToken);
		if (stream == null)
		{
			return null;
		}

		if (stream.Info.ContentEncoding != compressor.ContentEncoding)
		{
			return stream;
		}

		var decompressedStream = streamCompressor.Decompress(stream);
		return new(decompressedStream, stream.Info with
		{
			ContentEncoding = null
		});
	}

	public async Task<Stream> OpenWriteAsync(string? concurrencyToken, IBlob.WriteBlobInfo options, CancellationToken cancellationToken)
	{
		if (options is { ContentEncoding: string })
		{
			return await inner.OpenWriteAsync(concurrencyToken, options, cancellationToken);
		}

		var stream = await inner.OpenWriteAsync(concurrencyToken, options with
		{
			ContentEncoding = compressor.ContentEncoding
		}, cancellationToken);
		var wrapped = streamCompressor.Compress(stream);
		return wrapped;
	}


	public Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken) => inner.DeleteAsync(concurrencyToken, cancellationToken);

	public Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken) => inner.DeleteIfExistsAsync(cancellationToken);
}

public static partial class Extensions
{
	extension(IBlob blob)
	{
		public CompressedBlob WithCompression(ICompressor compressor, IStreamCompressor streamCompressor) => new(blob, compressor, streamCompressor);

		public CompressedBlob WithCompression(CompressionOptions options) => new(blob, CompressionOptions.CreateCompressor(options), CompressionOptions.CreateStreamCompressor(options));


		public CompressedBlob WithBrotliCompression(BrotliCompressionOptions options)
		{
			var compressor = new BrotliCompressor(options);
			return new(blob, compressor, compressor);
		}

		public CompressedBlob WithBrotliCompression() => new(blob, BrotliCompressor.Maximum, BrotliCompressor.Maximum);
	}
}