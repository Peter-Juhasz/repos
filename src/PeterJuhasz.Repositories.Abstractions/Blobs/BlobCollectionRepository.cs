using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Caching;
using PeterJuhasz.Repositories.Serialization;
using PeterJuhasz.Repositories.Serialization.Json;
using PeterJuhasz.Repositories.Serialization.Separated;
using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Separated;

namespace PeterJuhasz.Repositories.Blobs;

public class BufferedBlobCollectionRepository<T>(
	IBlob blob,
	ICollectionSerializer<T> serializer,
	IEqualityComparer<T>? comparer = null,
	OptimisticConcurrency.Options? concurrencyOptions = null
) : ICollectionRepository<T>
{
	public IBlob Blob => blob;

	public Task ApplyAsync(Func<IImmutableList<T>?, CancellationToken, ValueTask<IReadOnlyCollection<T>?>> update, CancellationToken cancellationToken) => OptimisticConcurrency.RetryAsync(async ct =>
	{
		var result = await blob.ReadAsync(ct);
		var oldData = result?.Value;
		IImmutableList<T>? oldItems = null;
		if (oldData != null)
		{
			oldItems = serializer.Deserialize(oldData.ToMemory().Span).ToImmutableList();
		}

		var newItems = await update(oldItems, ct);
		if (newItems == null)
		{
			if (result != null)
			{
				await blob.DeleteAsync(result.Info.ConcurrencyToken, ct);
			}

			return;
		}

		if (ReferenceEquals(newItems, oldItems))
		{
			return;
		}

		using var writer = new MemoryPoolBufferWriter<byte>(MemoryPool<byte>.Shared);
		serializer.Serialize(newItems, writer);
		await blob.WriteAsync(writer.WrittenMemory, result?.Info.ConcurrencyToken, new(MediaType: serializer.MediaType, Metadata: result?.Info.Metadata), ct);
	}, concurrencyOptions, cancellationToken);

	public async IAsyncEnumerable<T> AsAsyncEnumerableAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var result = await blob.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			yield break;
		}

		var stream = result.Value;
		await using (stream)
		{
			await foreach (var item in serializer.DeserializeAsyncEnumerable(stream, cancellationToken))
			{
				yield return item;
			}
		}
	}

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		var result = await blob.GetInfoAsync(cancellationToken);
		return result?.ConcurrencyToken;
	}

	public async Task<Versioned<IReadOnlyCollection<T>>> ListWithVersionAsync(CancellationToken cancellationToken)
	{
		var result = await blob.ReadAsync(cancellationToken);
		if (result == null)
		{
			return new([], null!);
		}

		var items = serializer.Deserialize(result.Value.ToMemory().Span).ToImmutableList();
		return new(items, result.Info.ConcurrencyToken);
	}

	public Task<bool> DeleteAsync(T value, CancellationToken cancellationToken)
	{
		var effectiveComparer = comparer ?? EqualityComparer<T>.Default;
		return ((ICollectionRepository<T>)this).DeleteAsync(item => effectiveComparer.Equals(item, value), cancellationToken);
	}

	public async Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken)
	{
		var result = await blob.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			return null;
		}

		var stream = result.Value;
		return new RawStreamResult(stream, result.Info.LastModified, result.Info.ConcurrencyToken, result.Info.ContentEncoding);
	}

	public Task ClearAsync(CancellationToken cancellationToken) => blob.DeleteIfExistsAsync(cancellationToken);
}

public class StreamingBlobCollectionRepository<T>(
	IBlob blob,
	ICollectionSerializer<T> serializer,
	IEqualityComparer<T>? comparer = null
) : ICollectionRepository<T>
{
	public IBlob Blob => blob;

	public Task ApplyAsync(Func<IImmutableList<T>?, CancellationToken, ValueTask<IReadOnlyCollection<T>?>> update, CancellationToken cancellationToken) => OptimisticConcurrency.RetryAsync(async ct =>
	{
		// read items
		IImmutableList<T>? oldItems = null;

		var openResult = await blob.OpenReadAsync(ct);
		if (openResult != null)
		{
			var stream = openResult.Value;
			using var _ = ImmutableListBuilderPool<T>.GetPooledObject(out var builder);
			await using (stream)
			{
				await foreach (var item in serializer.DeserializeAsyncEnumerable(stream, ct))
				{
					builder.Add(item);
				}
			}
			oldItems = builder.ToImmutable();
		}

		// transform
		var newItems = await update(oldItems, ct);

		if (newItems == null)
		{
			if (openResult != null)
			{
				await blob.DeleteAsync(openResult.Info.ConcurrencyToken, ct);
			}

			return;
		}

		if (ReferenceEquals(newItems, oldItems))
		{
			return;
		}

		// write items
		await using var writeResult = await blob.OpenWriteAsync(openResult?.Info.ConcurrencyToken, new(MediaType: serializer.MediaType, Metadata: openResult?.Info.Metadata), ct);
		await serializer.SerializeAsync(newItems, writeResult, ct);
		await writeResult.FlushAsync(ct);
	}, cancellationToken);

	public async IAsyncEnumerable<T> AsAsyncEnumerableAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var result = await blob.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			yield break;
		}

		var stream = result.Value;
		await using (stream)
		{
			await foreach (var item in serializer.DeserializeAsyncEnumerable(stream, cancellationToken))
			{
				yield return item;
			}
		}
	}

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		var result = await blob.GetInfoAsync(cancellationToken);
		return result?.ConcurrencyToken;
	}

	public async Task<Versioned<IReadOnlyCollection<T>>> ListWithVersionAsync(CancellationToken cancellationToken)
	{
		var result = await blob.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			return new([], null!);
		}

		var stream = result.Value;

		await using (stream)
		{
			var list = new List<T>();

			await foreach (var item in serializer.DeserializeAsyncEnumerable(stream, cancellationToken))
			{
				list.Add(item);
			}

			if (list.Count == 0)
			{
				return new([], result.Info.ConcurrencyToken);
			}

			return new(list, result.Info.ConcurrencyToken);
		}
	}

	public Task<bool> DeleteAsync(T value, CancellationToken cancellationToken)
	{
		var effectiveComparer = comparer ?? EqualityComparer<T>.Default;
		return ((ICollectionRepository<T>)this).DeleteAsync(item => effectiveComparer.Equals(item, value), cancellationToken);
	}

	public async Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken)
	{
		var result = await blob.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			return null;
		}

		var stream = result.Value;
		return new RawStreamResult(stream, result.Info.LastModified, result.Info.ConcurrencyToken, result.Info.ContentEncoding);
	}

	public Task ClearAsync(CancellationToken cancellationToken) => blob.DeleteIfExistsAsync(cancellationToken);
}

public static partial class Extensions
{
	extension(IBlob blob)
	{
		public ICollectionRepository<T> AsCollectionRepository<T>(ICollectionSerializer<T> serializer, IEqualityComparer<T>? comparer = null) =>
			new BufferedBlobCollectionRepository<T>(blob, serializer, comparer);

		public ICollectionRepository<T> AsStreamingCollectionRepository<T>(ICollectionSerializer<T> serializer, IEqualityComparer<T>? comparer = null) =>
			new StreamingBlobCollectionRepository<T>(blob, serializer, comparer);


		public ICollectionRepository<T> AsJsonCollectionRepository<T>(JsonSerializerOptions jsonSerializerOptions, IEqualityComparer<T>? comparer = null) =>
			blob.AsCollectionRepository<T>(new JsonSerializerOptionsJsonCollectionSerializer<T>(jsonSerializerOptions), comparer);

		public ICollectionRepository<T> AsJsonCollectionRepository<T>(JsonSerializerContext context, IEqualityComparer<T>? comparer = null) =>
			blob.AsCollectionRepository<T>(new JsonTypeInfoJsonCollectionSerializer<T>(context), comparer);

		public ICollectionRepository<T> AsStreamingJsonCollectionRepository<T>(JsonSerializerOptions jsonSerializerOptions, IEqualityComparer<T>? comparer = null) =>
			blob.AsStreamingCollectionRepository<T>(new JsonSerializerOptionsJsonCollectionSerializer<T>(jsonSerializerOptions), comparer);

		public ICollectionRepository<T> AsStreamingJsonCollectionRepository<T>(JsonSerializerContext context, IEqualityComparer<T>? comparer = null) =>
			blob.AsStreamingCollectionRepository<T>(new JsonTypeInfoJsonCollectionSerializer<T>(context), comparer);


		public ICollectionRepository<T> AsCsvCollectionRepository<T>(IEqualityComparer<T>? comparer = null) =>
			blob.AsStreamingCollectionRepository<T>(new SeparatedValuesSerializer<T>(SeparatedValuesReaderOptions.Csv, SeparatedValuesWriterOptions.Csv), comparer);

		public ICollectionRepository<T> AsTsvCollectionRepository<T>(IEqualityComparer<T>? comparer = null) =>
			blob.AsStreamingCollectionRepository<T>(new SeparatedValuesSerializer<T>(SeparatedValuesReaderOptions.Tsv, SeparatedValuesWriterOptions.Tsv), comparer);
	}

	extension<T>(ICollectionRepository<T> repository)
	{
		public bool TryGetBlob([NotNullWhen(true)] out IBlob? blob)
		{
			switch (repository)
			{
				case BufferedBlobCollectionRepository<T> blobRepo:
					blob = blobRepo.Blob;
					return true;

				case StreamingBlobCollectionRepository<T> streamingBlobRepo:
					blob = streamingBlobRepo.Blob;
					return true;

				case CachingCollectionRepository<T> cachingRepo:
					return cachingRepo.Inner.TryGetBlob(out blob);

				default:
					blob = null;
					return false;
			}
		}
	}
}