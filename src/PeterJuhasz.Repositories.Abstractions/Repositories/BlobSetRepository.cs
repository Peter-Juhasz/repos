using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Serialization;
using PeterJuhasz.Repositories.Serialization.Json;
using PeterJuhasz.Repositories.Serialization.Separated;
using System.Buffers;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Separated;

namespace PeterJuhasz.Repositories.Blobs;

public class BlobSetRepository<T>(
	IBlob blob,
	ICollectionSerializer<T> serializer,
	IEqualityComparer<T>? comparer = null,
	OptimisticConcurrencyOptions? concurrencyOptions = null
) : ISetRepository<T>
{
	public IBlob Blob => blob;

	private readonly IEqualityComparer<T> effectiveComparer = comparer ?? EqualityComparer<T>.Default;

	public Task ApplyAsync(Func<IImmutableList<T>?, CancellationToken, ValueTask<IReadOnlyCollection<T>?>> update, CancellationToken cancellationToken) => OptimisticConcurrency.Retry(async ct =>
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

	Task ApplyAsync(Func<IImmutableList<T>?, IReadOnlyCollection<T>?> update, CancellationToken cancellationToken = default) => ApplyAsync((items, ct) => new(update(items)), cancellationToken);


	public async Task<bool> DeleteAsync(T value, CancellationToken cancellationToken)
	{
		var removed = false;

		await ApplyAsync(items =>
		{
			removed = false;
			if (items is null or { Count: 0 })
			{
				return items;
			}

			var newItems = items.RemoveAll(item => effectiveComparer.Equals(item, value));

			removed = newItems.Count < items.Count;

			if (newItems.Count == 0)
			{
				return null;
			}

			return newItems;
		}, cancellationToken);

		return removed;
	}

	public Task ClearAsync(CancellationToken cancellationToken) => blob.DeleteIfExistsAsync(cancellationToken);

	public async Task<bool> AddAsync(T value, CancellationToken cancellationToken)
	{
		var added = false;

		await ApplyAsync(oldItems =>
		{
			added = false;

			// empty
			if (oldItems is null or { Count: 0 })
			{
				added = true;
				return [value];
			}

			// add
			var newItems = oldItems.Add(value);
			added = true;

			return newItems;
		}, cancellationToken);

		return added;
	}

	public async Task<bool> ContainsAsync(T value, CancellationToken cancellationToken)
	{
		await foreach (var item in ListAsync(cancellationToken))
		{
			if (effectiveComparer.Equals(item, value))
			{
				return true;
			}
		}

		return false;
	}

	public async IAsyncEnumerable<T> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
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
}

public static partial class Extensions
{
	extension(IBlob blob)
	{
		public ISetRepository<T> AsSetRepository<T>(ICollectionSerializer<T> serializer, IEqualityComparer<T>? comparer = null) =>
			new BlobSetRepository<T>(blob, serializer, comparer);


		public ISetRepository<T> AsJsonSetRepository<T>(JsonSerializerOptions jsonSerializerOptions, IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new JsonSerializerOptionsJsonCollectionSerializer<T>(jsonSerializerOptions), comparer);

		public ISetRepository<T> AsJsonSetRepository<T>(JsonSerializerContext context, IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new JsonTypeInfoJsonCollectionSerializer<T>(context), comparer);


		public ISetRepository<T> AsCsvSetRepository<T>(IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new SeparatedValuesSerializer<T>(SeparatedValuesReaderOptions.Csv, SeparatedValuesWriterOptions.Csv), comparer);

		public ISetRepository<T> AsTsvSetRepository<T>(IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new SeparatedValuesSerializer<T>(SeparatedValuesReaderOptions.Tsv, SeparatedValuesWriterOptions.Tsv), comparer);
	}
}