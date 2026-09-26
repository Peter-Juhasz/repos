using System.Buffers;
using System.Text;

namespace PeterJuhasz.Repositories.Serialization;

public sealed class StringSerializer(Encoding encoding) : ISerializer<string>
{
	public static readonly StringSerializer Utf8 = new(Encoding.UTF8);

	public string MediaType { get; } = "text/plain";

	public bool TryGetMaximumSerializedLength(string value, out int size)
	{
		size = encoding.GetMaxByteCount(value.Length);
		return true;
	}

	public void Serialize(string value, IBufferWriter<byte> buffer)
	{
		var bytes = encoding.GetBytes(value);
		buffer.Write(bytes);
	}

	public bool Serialize(string value, Span<byte> buffer, out int bytesWritten)
	{
		return encoding.TryGetBytes(value, buffer, out bytesWritten);
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out string value)
	{
		value = encoding.GetString(buffer);
		return true;
	}

	public bool Deserialize(ReadOnlySequence<byte> buffer, out string value)
	{
		value = encoding.GetString(buffer);
		return true;
	}

	public async Task SerializeAsync(string value, Stream stream, CancellationToken cancellationToken)
	{
		var encodedLength = encoding.GetByteCount(value);
		using var owner = MemoryPool<byte>.Shared.Rent(encodedLength);
		encoding.GetBytes(value, owner.Memory.Span);
		var memory = owner.Memory[..encodedLength];
		await stream.WriteAsync(memory, cancellationToken);
	}

	public async Task<string> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		using var reader = new StreamReader(stream, encoding);
		return await reader.ReadToEndAsync(cancellationToken);
	}
}

public sealed class AsciiStringSerializer() : ISerializer<string>
{
	public static readonly AsciiStringSerializer Instance = new();

	public string MediaType { get; } = "text/plain";

	public bool TryGetMaximumSerializedLength(string value, out int size)
	{
		size = value.Length;
		return true;
	}

	public void Serialize(string value, IBufferWriter<byte> buffer)
	{
		var span = buffer.GetSpan(value.Length);
		if (!Serialize(value, span, out var written))
		{
			throw new ArgumentException("Value contains non-ASCII characters.", nameof(value));
		}
		buffer.Advance(written);
	}

	public bool Serialize(string value, Span<byte> buffer, out int bytesWritten)
	{
		return Ascii.FromUtf16(value, buffer, out bytesWritten) is OperationStatus.Done;
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out string value)
	{
		if (!Ascii.IsValid(buffer))
		{
			value = string.Empty;
			return false;
		}

		value = String.Create(buffer.Length, buffer, (span, bytes) =>
		{
			Ascii.ToUtf16(bytes, span, out _);
		});
		return true;
	}

	public bool Deserialize(ReadOnlySequence<byte> buffer, out string value)
	{
		if (buffer.IsSingleSegment)
		{
			return Deserialize(buffer.First.Span, out value);
		}

		Span<byte> temp = stackalloc byte[(int)buffer.Length];
		buffer.CopyTo(temp);
		return Deserialize(temp, out value);
	}

	public async Task SerializeAsync(string value, Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public async Task<string> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}
}