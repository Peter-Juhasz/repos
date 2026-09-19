using System.Buffers;

namespace PeterJuhasz.Repositories.Serialization;

public interface ISerializer<T>
{
	string MediaType { get; }

	bool TryGetMaximumSerializedLength(T value, out int size)
	{
		size = default;
		return false;
	}

	bool Serialize(T value, Span<byte> buffer, out int bytesWritten) => throw new NotSupportedException();

	void Serialize(T value, IBufferWriter<byte> buffer);

	bool Deserialize(ReadOnlySpan<byte> buffer, out T value);

	bool Deserialize(ReadOnlySequence<byte> buffer, out T value)
	{
		if (buffer.IsSingleSegment)
		{
			return Deserialize(buffer.First.Span, out value);
		}

		Span<byte> temp = stackalloc byte[(int)buffer.Length];
		buffer.CopyTo(temp);
		return Deserialize(temp, out value);
	}

	Task SerializeAsync(T value, Stream stream, CancellationToken cancellationToken);

	Task<T> DeserializeAsync(Stream stream, CancellationToken cancellationToken);
}

public interface ICollectionSerializer<T>
{
	string MediaType { get; }

	void Serialize(IReadOnlyCollection<T> items, IBufferWriter<byte> buffer);

	bool Deserialize(ReadOnlySpan<byte> buffer, out IReadOnlyCollection<T> items);

	bool Deserialize(ReadOnlySequence<byte> buffer, out IReadOnlyCollection<T> items);

	Task SerializeAsync(IReadOnlyCollection<T> items, Stream stream, CancellationToken cancellationToken);

	async Task<IReadOnlyCollection<T>> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		var items = new List<T>();
		await foreach (var item in DeserializeAsyncEnumerable(stream, cancellationToken))
		{
			items.Add(item);
		}
		return items;
	}

	IAsyncEnumerable<T> DeserializeAsyncEnumerable(Stream stream, CancellationToken cancellationToken);
}

public static partial class Extensions
{
	extension<T>(ISerializer<T> serializer)
	{
		public T Deserialize(ReadOnlySpan<byte> bytes)
		{
			if (!serializer.Deserialize(bytes, out T value))
			{
				throw new InvalidOperationException("Failed to deserialize the provided bytes.");
			}

			return value;
		}
	}

	extension<T>(ICollectionSerializer<T> serializer)
	{
		public IReadOnlyCollection<T> Deserialize(ReadOnlySpan<byte> bytes)
		{
			if (!serializer.Deserialize(bytes, out IReadOnlyCollection<T> value))
			{
				throw new InvalidOperationException("Failed to deserialize the provided bytes.");
			}

			return value;
		}
	}
}