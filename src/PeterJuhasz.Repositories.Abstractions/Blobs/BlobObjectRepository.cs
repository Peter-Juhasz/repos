using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Caching;
using PeterJuhasz.Repositories.Serialization;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PeterJuhasz.Repositories.Blobs;

public class BlobObjectRepository<T>(
	IBlob blob,
	ISerializer<T> serializer,
	IEqualityComparer<T>? comparer = null,
	OptimisticConcurrencyOptions? concurrencyOptions = null
)
	: IObjectRepository<T>
	where T : class
{
	public IBlob Blob => blob;

	public Task<bool> ExistsAsync(CancellationToken cancellationToken) => blob.ExistsAsync(cancellationToken);

	public Task DeleteWithVersionAsync(string etag, CancellationToken cancellationToken) => blob.DeleteAsync(etag, cancellationToken);

	public async ValueTask<Versioned<T>?> GetOrDefaultWithVersionAsync(CancellationToken cancellationToken)
	{
		var result = await blob.ReadAsync(cancellationToken);
		if (result == null)
		{
			return null;
		}

		if (result.Info.MediaType != serializer.MediaType)
		{
			throw new InvalidOperationException($"Unexpected content type: {result.Info.MediaType}");
		}

		if (!serializer.Deserialize(result.Value, out var value))
		{
			throw new InvalidOperationException("Failed to deserialize blob content.");
		}

		return new(value, result.Info.ConcurrencyToken);
	}

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		var result = await blob.GetInfoAsync(cancellationToken);
		return result?.ConcurrencyToken;
	}

	public async Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken)
	{
		var result = await blob.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			return null;
		}

		return new(result.Value, result.Info.LastModified, result.Info.ConcurrencyToken, result.Info.ContentEncoding);
	}

	public async Task<Versioned<T>> StoreAsync(T value, string? etag, CancellationToken cancellationToken)
	{
		using var _ = ArrayBufferWriterPool<byte>.GetPooledObject(out var writer);
		serializer.Serialize(value, writer);
		var concurrencyToken = await blob.WriteAsync(writer.WrittenMemory, etag, new(MediaType: serializer.MediaType), cancellationToken);
		return new(value, concurrencyToken);
	}

	public Task<T?> ApplyAsync(Func<T?, CancellationToken, ValueTask<T?>> transform, CancellationToken cancellationToken)
	{
		var effectiveComparer = comparer ?? EqualityComparer<T>.Default;

		return OptimisticConcurrency.Retry(async ct =>
		{
			var result = await blob.ReadAsync(ct);
			var oldData = result?.Value;
			T? oldObject = null;
			if (oldData != null)
			{
				oldObject = serializer.Deserialize(oldData.ToMemory().Span);
			}

			var newObject = await transform(oldObject, ct);
			if (newObject == null)
			{
				if (result != null)
				{
					await blob.DeleteAsync(result.Info.ConcurrencyToken, ct);
				}

				return null;
			}

			if (ReferenceEquals(newObject, oldObject) || effectiveComparer.Equals(oldObject, newObject))
			{
				return oldObject;
			}

			using var writer = new MemoryPoolBufferWriter<byte>(MemoryPool<byte>.Shared);
			serializer.Serialize(newObject, writer);
			await blob.WriteAsync(writer.WrittenMemory, result?.Info.ConcurrencyToken, new(MediaType: serializer.MediaType, Metadata: result?.Info.Metadata), ct);
			return newObject;
		}, concurrencyOptions, cancellationToken);
	}
}

public static partial class Extensions
{
	extension(IBlob blob)
	{
		public IObjectRepository<T> AsObjectRepository<T>(ISerializer<T> serializer, IEqualityComparer<T>? comparer = null) where T : class =>
			new BlobObjectRepository<T>(blob, serializer, comparer);


		public IObjectRepository<string> AsStringObjectRepository(IEqualityComparer<string>? comparer = null) =>
			new BlobObjectRepository<string>(blob, StringSerializer.Utf8, comparer);


		public IObjectRepository<T> AsJsonObjectRepository<T>(JsonSerializerOptions jsonSerializerOptions, IEqualityComparer<T>? comparer = null) where T : class =>
			new BlobObjectRepository<T>(blob, new JsonSerializerOptionsJsonSerializer<T>(jsonSerializerOptions), comparer);

		public IObjectRepository<T> AsJsonObjectRepository<T>(JsonSerializerContext context, IEqualityComparer<T>? comparer = null) where T : class =>
			new BlobObjectRepository<T>(blob, new JsonTypeInfoJsonSerializer<T>(context), comparer);

		public IObjectRepository<T> AsJsonObjectRepository<T>(JsonTypeInfo<T> jsonTypeInfo, IEqualityComparer<T>? comparer = null) where T : class =>
			new BlobObjectRepository<T>(blob, new JsonTypeInfoJsonSerializer<T>(jsonTypeInfo), comparer);
	}

	extension<T>(IObjectRepository<T> repository) where T : class
	{
		public bool TryGetBlob([NotNullWhen(true)] out IBlob? blob)
		{
			switch (repository)
			{
				case BlobObjectRepository<T> blobRepo:
					blob = blobRepo.Blob;
					return true;

				case CachingObjectRepository<T> cachingRepo:
					return cachingRepo.Inner.TryGetBlob(out blob);

				case GlobalCachingBlobSingleObjectRepository<T> globalCachingBlobSingleObjectRepository:
					return globalCachingBlobSingleObjectRepository.Inner.TryGetBlob(out blob);

				default:
					blob = null;
					return false;
			}
		}
	}
}