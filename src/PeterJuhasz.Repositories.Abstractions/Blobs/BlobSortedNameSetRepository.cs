using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.Serialization;
using System.Buffers;
using System.Text;

namespace PeterJuhasz.Repositories.Blobs;

public class BlobSortedNameSetRepository<T>(
	IBlobPartition partition,
	ISerializer<T> serializer,
	Func<T, string> sortKeySelector,
	Encoding encoding,
	char separator = '|'
)
	: ISetRepository<T>
{
	private string Encode(T value)
	{
		using var _ = ArrayBufferWriterPool<byte>.GetPooledObject(out var writer);

		var sortKey = sortKeySelector(value);
		if (sortKey.Contains(separator))
		{
			throw new ArgumentException("Sort key cannot contain the separator character.", nameof(sortKeySelector));
		}

		var byteCount = encoding.GetByteCount(sortKey) + 1;
		var span = writer.GetSpan(byteCount);
		encoding.GetBytes(sortKey, span);

		span[^1] = (byte)separator;
		writer.Advance(byteCount);

		serializer.Serialize(value, writer);
		return encoding.GetString(writer.WrittenSpan);
	}

	private T Decode(ReadOnlySpan<char> value)
	{
		var separatorIndex = value.IndexOf(separator);
		if (separatorIndex < 0)
		{
			throw new FormatException($"Invalid blob name format: {value}");
		}

		var encodedValue = value[..(separatorIndex + 1)];
		Span<byte> buffer = stackalloc byte[encoding.GetByteCount(encodedValue)];
		encoding.GetBytes(encodedValue, buffer);
		return serializer.Deserialize(buffer);
	}

	private IBlob GetBlob(T value) => partition.GetBlob(Encode(value));

	public async Task<bool> AddAsync(T value, CancellationToken cancellationToken)
	{
		var blob = GetBlob(value);
		try
		{
			await blob.WriteAsync(ReadOnlyMemory<byte>.Empty, null, default, cancellationToken);
		}
		catch (ConflictException)
		{
			return false;
		}
		return true;
	}

	public async Task<bool> ContainsAsync(T value, CancellationToken cancellationToken)
	{
		var blob = GetBlob(value);
		return await blob.ExistsAsync(cancellationToken);
	}

	public async Task ClearAsync(CancellationToken cancellationToken)
	{
		await partition.ClearAsync(cancellationToken);
	}

	public async Task<bool> DeleteAsync(T value, CancellationToken cancellationToken)
	{
		var blob = GetBlob(value);
		return await blob.DeleteIfExistsAsync(cancellationToken);
	}

	public IAsyncEnumerable<T> ListAsync(CancellationToken cancellationToken)
	{
		return partition.GetBlobs(cancellationToken).Select(item => Decode(item.Name));
	}
}

public class SetSortedSetRepository<TKey, TItem>(
	ISetRepository<string> inner,
	ISerializer<TKey> keySerializer,
	ISerializer<TItem> itemSerializer,
	Encoding encoding,
	char separator = '|'
) : ISortedSetRepository<TKey, TItem>
{
	private string Encode(TKey sortKey, TItem value)
	{
		using var _1 = ArrayBufferWriterPool<byte>.GetPooledObject(out var writer1);
		keySerializer.Serialize(sortKey, writer1);

		using var _2 = ArrayBufferWriterPool<byte>.GetPooledObject(out var writer2);
		itemSerializer.Serialize(value, writer2);

		var sortKeyCharCount = encoding.GetCharCount(writer1.WrittenSpan);
		var valueCharCount = encoding.GetCharCount(writer2.WrittenSpan);

		return string.Create(sortKeyCharCount + 1 + valueCharCount, (writer1, writer2), (span, state) =>
		{
			var sortKeyCharsWritten = encoding.GetChars(writer1.WrittenSpan, span);
			span[sortKeyCharsWritten] = separator;
			encoding.GetChars(writer2.WrittenSpan, span[(sortKeyCharsWritten + 1)..]);
		});
	}

	private (TKey, TItem) Decode(ReadOnlySpan<char> value)
	{
		var separatorIndex = value.IndexOf(separator);
		if (separatorIndex < 0)
		{
			throw new FormatException($"Invalid blob name format: {value}");
		}

		var sortKeySpan = value[..separatorIndex];
		Span<byte> sortKeyBuffer = stackalloc byte[encoding.GetByteCount(sortKeySpan)];
		encoding.GetBytes(sortKeySpan, sortKeyBuffer);
		TKey sortKey = keySerializer.Deserialize(sortKeyBuffer);

		var valueSpan = value[(separatorIndex + 1)..];
		Span<byte> valueBuffer = stackalloc byte[encoding.GetByteCount(valueSpan)];
		encoding.GetBytes(valueSpan, valueBuffer);
		TItem item = itemSerializer.Deserialize(valueBuffer);

		return (sortKey, item);
	}

	public async Task<bool> AddAsync(TKey sortKey, TItem value, CancellationToken cancellationToken)
	{
		return await inner.AddAsync(Encode(sortKey, value), cancellationToken);
	}

	public async Task<bool> ContainsAsync(TKey sortKey, TItem value, CancellationToken cancellationToken)
	{
		return await inner.ContainsAsync(Encode(sortKey, value), cancellationToken);
	}

	public async Task ClearAsync(CancellationToken cancellationToken)
	{
		await inner.ClearAsync(cancellationToken);
	}

	public async Task<bool> DeleteAsync(TKey sortKey, TItem value, CancellationToken cancellationToken)
	{
		return await inner.DeleteAsync(Encode(sortKey, value), cancellationToken);
	}

	public IAsyncEnumerable<(TKey, TItem)> ListAsync(CancellationToken cancellationToken)
	{
		return inner.ListAsync(cancellationToken).Select(item => Decode(item));
	}
}

public static partial class Extensions
{
	extension(IBlobPartition partition)
	{
		public ISetRepository<T> AsSortedSetRepositoryAsNames<T>(ISerializer<T> serializer, Func<T, string> sortKeySelector, Encoding encoding, char separator = '|') =>
			new BlobSortedNameSetRepository<T>(partition, serializer, sortKeySelector, encoding, separator);
	}

	extension(ISetRepository<string> inner)
	{
		public ISortedSetRepository<TKey, TItem> AsSortedSetRepository<TKey, TItem>(ISerializer<TKey> keySerializer, ISerializer<TItem> itemSerializer, Encoding? encoding = null, char separator = '|') =>
			new SetSortedSetRepository<TKey, TItem>(inner, keySerializer, itemSerializer, encoding ?? Encoding.UTF8, separator);
	}
}