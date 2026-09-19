using System.Buffers;
using System.Numerics;

namespace PeterJuhasz.Repositories.Serialization;

public sealed class BigEndianBinaryIntegerSerializer<T>(bool unsigned) : ISerializer<T> where T : struct, IBinaryInteger<T>
{
	public static readonly ISerializer<int> Int32Serializer = new BigEndianBinaryIntegerSerializer<int>(unsigned: true);

	public string MediaType { get; } = "application/octet-stream";

	public void Serialize(T value, IBufferWriter<byte> buffer)
	{
		var span = buffer.GetSpan(64);
		var written = value.WriteBigEndian(span);
		buffer.Advance(written);
	}

	public bool Serialize(T value, Span<byte> buffer, out int bytesWritten)
	{
		bytesWritten = value.WriteBigEndian(buffer);
		return true;
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out T value)
	{
		value = T.ReadBigEndian(buffer, unsigned);
		return true;
	}

	public Task<T> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public Task SerializeAsync(T value, Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}
}