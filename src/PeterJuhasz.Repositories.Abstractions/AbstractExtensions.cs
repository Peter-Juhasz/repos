using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.Serialization;
using System.Buffers;

namespace PeterJuhasz.Repositories.Abstractions;

public static partial class StorageExtensions
{
	extension(IBlob blob)
	{
		public async Task<TTo?> TransformBufferedAsync<TFrom, TTo>(
			Func<TFrom?, CancellationToken, ValueTask<TTo?>> transform,
			ISerializer<TFrom> fromSerializer,
			ISerializer<TTo> toSerializer,
			CancellationToken cancellationToken,
			IEqualityComparer<TTo>? comparer = null,
			OptimisticConcurrencyOptions? concurrencyOptions = null
		)
			where TFrom : class, TTo
			where TTo : class
		{
			comparer ??= EqualityComparer<TTo>.Default;
			TTo? result = null;

			await blob.TransformAsync(async (BinaryData? data, CancellationToken ct) =>
			{
				TFrom? oldObject = null;
				if (data != null)
				{
					oldObject = fromSerializer.Deserialize(data.ToMemory().Span);
				}

				var newObject = await transform(oldObject, ct);
				if (newObject == null)
				{
					result = null;
					return null;
				}

				if (ReferenceEquals(oldObject, newObject) || comparer.Equals(oldObject, newObject))
				{
					result = oldObject;
					return data;
				}

				result = newObject;
				BinaryData newData;
				if (toSerializer.TryGetMaximumSerializedLength(newObject, out var maximumLength))
				{
					var buffer = new byte[maximumLength];
					var bufferWriter = new FixedSizeArrayBufferWriter<byte>(buffer);
					toSerializer.Serialize(newObject, bufferWriter);
					newData = new BinaryData(bufferWriter.WrittenMemory, toSerializer.MediaType);
				}
				else
				{
					using var _ = ArrayBufferWriterPool<byte>.GetPooledObject(out var bufferWriter);
					toSerializer.Serialize(newObject, bufferWriter);
					newData = new BinaryData(bufferWriter.WrittenMemory.ToArray(), toSerializer.MediaType);
				}
				return newData;
			}, cancellationToken, concurrencyOptions);

			return result;
		}

		public Task<TTo?> TransformBufferedAsync<TFrom, TTo>(
			Func<TFrom?, TTo?> transform,
			ISerializer<TFrom> fromSerializer,
			ISerializer<TTo> toSerializer,
			CancellationToken cancellationToken,
			IEqualityComparer<TTo>? comparer = null,
			OptimisticConcurrencyOptions? concurrencyOptions = null
		)
			where TFrom : class, TTo
			where TTo : class
			=> blob.TransformBufferedAsync<TFrom, TTo>(
			(old, ct) => new ValueTask<TTo?>(transform(old)),
			fromSerializer,
			toSerializer,
			cancellationToken,
			comparer,
			concurrencyOptions
		);

		public Task<T?> TransformBufferedAsync<T>(
			Func<T?, CancellationToken, ValueTask<T?>> transform,
			ISerializer<T> serializer,
			CancellationToken cancellationToken,
			IEqualityComparer<T>? comparer = null,
			OptimisticConcurrencyOptions? concurrencyOptions = null
		) where T : class => blob.TransformBufferedAsync<T, T>(
			transform,
			serializer,
			serializer,
			cancellationToken,
			comparer,
			concurrencyOptions
		);

		public Task<T?> TransformBufferedAsync<T>(
			Func<T?, T?> transform,
			ISerializer<T> serializer,
			CancellationToken cancellationToken,
			IEqualityComparer<T>? comparer = null,
			OptimisticConcurrencyOptions? concurrencyOptions = null
		) where T : class => blob.TransformBufferedAsync<T>(
			(old, ct) => new ValueTask<T?>(transform(old)),
			serializer,
			cancellationToken,
			comparer,
			concurrencyOptions
		);


		public async Task<TTo?> TransformStreamingAsync<TFrom, TTo>(
			Func<TFrom?, CancellationToken, ValueTask<TTo?>> transform,
			ISerializer<TFrom> fromSerializer,
			ISerializer<TTo> toSerializer,
			CancellationToken cancellationToken,
			IEqualityComparer<TTo>? comparer = null,
			OptimisticConcurrencyOptions? concurrencyOptions = null
		)
			where TFrom : class, TTo
			where TTo : class
		{
			comparer ??= EqualityComparer<TTo>.Default;
			TTo? result = null;

			await OptimisticConcurrency.Retry(async ct =>
			{
				// download and deserialize the existing blob content
				TFrom? oldObject;
				string? etag;
				if (await blob.OpenReadAsync(ct) is { } response)
				{
					etag = response.Info.ConcurrencyToken;
					await using var readStream = response.Value;
					oldObject = await fromSerializer.DeserializeAsync(readStream, ct);
				}
				else
				{
					oldObject = null;
					etag = null;
				}

				// transform the object
				var newObject = await transform(oldObject, ct);

				// compare
				if (ReferenceEquals(oldObject, newObject) || comparer.Equals(oldObject, newObject))
				{
					result = oldObject;
					return;
				}

				// delete
				if (newObject == null)
				{
					if (etag != null)
					{
						await blob.DeleteAsync(etag, ct);
					}

					return;
				}

				// serialize and upload the new object
				await using var writeStream = await blob.OpenWriteAsync(etag, new(MediaType: toSerializer.MediaType), ct);
				await toSerializer.SerializeAsync(newObject, writeStream, ct);
				await writeStream.FlushAsync(ct);
			}, concurrencyOptions, cancellationToken);

			return result;
		}

		public Task<TTo?> TransformStreamingAsync<TFrom, TTo>(
			Func<TFrom?, TTo?> transform,
			ISerializer<TFrom> fromSerializer,
			ISerializer<TTo> toSerializer,
			CancellationToken cancellationToken,
			IEqualityComparer<TTo>? comparer = null,
			OptimisticConcurrencyOptions? concurrencyOptions = null
		)
			where TFrom : class, TTo
			where TTo : class => blob.TransformStreamingAsync<TFrom, TTo>(
				(old, ct) => new ValueTask<TTo?>(transform(old)),
				fromSerializer,
				toSerializer,
				cancellationToken,
				comparer,
				concurrencyOptions
			);

		public Task<T?> TransformStreamingAsync<T>(
			Func<T?, T?> transform,
			ISerializer<T> serializer,
			CancellationToken cancellationToken,
			IEqualityComparer<T>? comparer = null,
			OptimisticConcurrencyOptions? concurrencyOptions = null
		) where T : class => blob.TransformStreamingAsync<T>(
			(old, ct) => new ValueTask<T?>(transform(old)),
			serializer,
			cancellationToken,
			comparer,
			concurrencyOptions
		);

		public Task<T?> TransformStreamingAsync<T>(
			Func<T?, CancellationToken, ValueTask<T?>> transform,
			ISerializer<T> serializer,
			CancellationToken cancellationToken,
			IEqualityComparer<T>? comparer = null,
			OptimisticConcurrencyOptions? concurrencyOptions = null
		) where T : class => blob.TransformStreamingAsync<T, T>(
			transform,
			serializer,
			serializer,
			cancellationToken,
			comparer,
			concurrencyOptions
		);
	}
}
