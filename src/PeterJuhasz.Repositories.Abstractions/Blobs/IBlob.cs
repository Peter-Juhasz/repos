using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Caching;
using PeterJuhasz.Repositories.Compression;
using PeterJuhasz.Repositories.Locking;

namespace PeterJuhasz.Repositories.Blobs;

public interface IBlob
{
	const string AnyConcurrencyToken = "*";
	const string AnyOrNoneConcurrencyToken = "**";

	string Name { get; }

	Task<ReadBlobInfo?> GetInfoAsync(CancellationToken cancellationToken);

	Task<string> SetMetadataAsync(string concurrencyToken, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);

	Task<bool> ExistsAsync(CancellationToken cancellationToken);

	async Task<BlobReadResult?> ReadAsync(CancellationToken cancellationToken)
	{
		var streamResult = await OpenReadAsync(cancellationToken);
		if (streamResult == null)
		{
			return null;
		}

		await using var stream = streamResult.Value;
		var data = await BinaryData.FromStreamAsync(stream, cancellationToken);
		return new(data, streamResult.Info);
	}

	Task<string> WriteAsync(ReadOnlyMemory<byte> data, string? concurrencyToken, WriteBlobInfo options, CancellationToken cancellationToken);

	Task<BlobReadStreamResult?> OpenReadAsync(CancellationToken cancellationToken);

	Task<Stream> OpenWriteAsync(string? concurrencyToken, WriteBlobInfo options, CancellationToken cancellationToken);

	Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken);

	Task<bool> DeleteIfExistsAsync(CancellationToken cancellationToken);


	public sealed record class BlobReadResult(BinaryData Value, ReadBlobInfo Info)
	{
		public static implicit operator BinaryData(BlobReadResult result) => result.Value;
	}

	public sealed record class BlobReadStreamResult(Stream Value, ReadBlobInfo Info)
	{
		public static implicit operator Stream(BlobReadStreamResult result) => result.Value;
	}

	public sealed record class ReadBlobInfo(
		string ConcurrencyToken,
		DateTimeOffset LastModified,
		string? ContentEncoding = null,
		string? MediaType = null,
		IReadOnlyDictionary<string, string>? Metadata = null
	);

	public readonly record struct WriteBlobInfo(
		string? ContentEncoding = null,
		string? MediaType = null,
		IReadOnlyDictionary<string, string>? Metadata = null
	);
}

public static partial class Extensions
{
	extension(IBlob blob)
	{
		public Task<BinaryData?> TransformAsync(
			Func<BinaryData?, CancellationToken, ValueTask<BinaryData?>> transform,
			CancellationToken cancellationToken,
			OptimisticConcurrency.Options? concurrencyOptions = null
		) => OptimisticConcurrency.RetryAsync(async ct =>
		{
			var result = await blob.ReadAsync(ct);
			var oldData = result?.Value;
			var newData = await transform(oldData, ct);
			if (newData == null)
			{
				if (result != null)
				{
					await blob.DeleteAsync(result.Info.ConcurrencyToken, ct);
				}

				return null;
			}

			if (ReferenceEquals(oldData, newData))
			{
				return oldData;
			}

			await blob.WriteAsync(newData, result?.Info.ConcurrencyToken, new(MediaType: newData.MediaType, Metadata: result?.Info.Metadata), ct);
			return newData;
		}, concurrencyOptions, cancellationToken);

		public Task<BinaryData?> TransformAsync(
			Func<BinaryData?, BinaryData?> transform,
			CancellationToken cancellationToken,
			OptimisticConcurrency.Options? concurrencyOptions = null
		) => blob.TransformAsync((data, ct) => new(transform(data)), cancellationToken, concurrencyOptions);

		public async Task CopyToAsync(IBlob other, CancellationToken cancellationToken)
		{
			var original = await blob.OpenReadAsync(cancellationToken);
			if (original == null)
			{
				throw new NotFoundException("Blob not found.");
			}

			var oldStream = original.Value;
			await using (oldStream)
			{
				await using var newStream = await other.OpenWriteAsync(null, new(
					ContentEncoding: original.Info.ContentEncoding,
					MediaType: original.Info.MediaType,
					Metadata: original.Info.Metadata
				), cancellationToken);
				await oldStream.CopyToAsync(newStream, cancellationToken);
				await newStream.FlushAsync(cancellationToken);
			}
		}

		public IBlob GetUnderlying()
		{
			var current = blob;

			while (true)
			{
				switch (current)
				{
					case CompressedBlob compressed:
						current = compressed.Blob;
						break;

					case CachedBlob cached:
						current = cached.Blob;
						break;

					case LockingBlob locking:
						current = locking.Blob;
						break;

					default:
						return current;
				}
			}
		}
	}
}