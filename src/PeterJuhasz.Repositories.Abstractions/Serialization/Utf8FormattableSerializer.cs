using System.Buffers;
using System.Text;

namespace PeterJuhasz.Repositories.Serialization;

public sealed class FormattableSerializer<T>(Encoding encoding, string format = "") : ISerializer<T>
	where T : ISpanFormattable, ISpanParsable<T>
{
	public static readonly ISerializer<int> Int32Serializer = new FormattableSerializer<int>(Encoding.ASCII);
	public static readonly ISerializer<uint> UInt32Serializer = new FormattableSerializer<uint>(Encoding.ASCII);
	public static readonly ISerializer<long> Int64Serializer = new FormattableSerializer<long>(Encoding.ASCII);
	public static readonly ISerializer<ulong> UInt64Serializer = new FormattableSerializer<ulong>(Encoding.ASCII);
	public static readonly ISerializer<DateTime> DateTimeSerializer = new FormattableSerializer<DateTime>(Encoding.ASCII, "O");
	public static readonly ISerializer<DateTimeOffset> DateTimeOffsetSerializer = new FormattableSerializer<DateTimeOffset>(Encoding.ASCII, "O");

	public string MediaType { get; } = "text/plain";

	public bool Deserialize(ReadOnlySpan<byte> buffer, out T value)
	{
		var charCount = encoding.GetCharCount(buffer);
		Span<char> charBuffer = stackalloc char[charCount];
		encoding.GetChars(buffer, charBuffer);

		return T.TryParse(charBuffer, null, out value!);
	}

	public Task<T> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public void Serialize(T value, IBufferWriter<byte> buffer)
	{
		var span = buffer.GetSpan(64);
		if (!Serialize(value, span, out int written))
		{
			throw new InvalidOperationException($"Failed to format {typeof(T).FullName} as UTF-8.");
		}
		buffer.Advance(written);
	}

	public bool Serialize(T value, Span<byte> buffer, out int bytesWritten)
	{
		Span<char> charBuffer = stackalloc char[64];
		if (!value.TryFormat(charBuffer, out bytesWritten, format, default))
		{
			return false;
		}

		return encoding.TryGetBytes(charBuffer[..bytesWritten], buffer, out bytesWritten);
	}

	public Task SerializeAsync(T value, Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}
}

public sealed class Utf8FormattableSerializer<T>(string format = "") : ISerializer<T>
	where T : IUtf8SpanFormattable, IUtf8SpanParsable<T>
{
	public static readonly ISerializer<int> Int32Serializer = new Utf8FormattableSerializer<int>();
	public static readonly ISerializer<uint> UInt32Serializer = new Utf8FormattableSerializer<uint>();
	public static readonly ISerializer<long> Int64Serializer = new Utf8FormattableSerializer<long>();
	public static readonly ISerializer<ulong> UInt64Serializer = new Utf8FormattableSerializer<ulong>();

	public string MediaType { get; } = "text/plain";

	public bool Deserialize(ReadOnlySpan<byte> buffer, out T value)
	{
		return T.TryParse(buffer, null, out value!);
	}

	public Task<T> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public void Serialize(T value, IBufferWriter<byte> buffer)
	{
		var span = buffer.GetSpan(64);
		if (!value.TryFormat(span, out var written, format, default))
		{
			throw new InvalidOperationException($"Failed to format {typeof(T).FullName} as UTF-8.");
		}
		buffer.Advance(written);
	}

	public bool Serialize(T value, Span<byte> buffer, out int bytesWritten)
	{
		return value.TryFormat(buffer, out bytesWritten, format, default);
	}

	public Task SerializeAsync(T value, Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}
}
