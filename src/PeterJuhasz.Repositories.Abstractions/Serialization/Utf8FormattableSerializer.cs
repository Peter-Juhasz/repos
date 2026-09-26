using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
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

	private const int MaximumCharCount = 64;

	private readonly int _maximumByteCount = encoding.GetMaxByteCount(MaximumCharCount);

	public string MediaType { get; } = "text/plain";

	public bool TryGetMaximumSerializedLength(T value, out int size)
	{
		size = _maximumByteCount;
		return true;
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out T value)
	{
		var charCount = encoding.GetCharCount(buffer);
		Span<char> charBuffer = stackalloc char[charCount];
		encoding.GetChars(buffer, charBuffer);

		// the generic parser drops the kind of a round-trip ("O") formatted UTC value, and converts it to local time
		if (typeof(T) == typeof(DateTime))
		{
			var result = DateTime.TryParse(charBuffer, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime);
			value = Unsafe.As<DateTime, T>(ref dateTime);
			return result;
		}

		return T.TryParse(charBuffer, CultureInfo.InvariantCulture, out value!);
	}

	public Task<T> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public void Serialize(T value, IBufferWriter<byte> buffer)
	{
		var span = buffer.GetSpan(_maximumByteCount);
		if (!Serialize(value, span, out int written))
		{
			throw new InvalidOperationException($"Failed to format {typeof(T).FullName} as UTF-8.");
		}
		buffer.Advance(written);
	}

	public bool Serialize(T value, Span<byte> buffer, out int bytesWritten)
	{
		Span<char> charBuffer = stackalloc char[MaximumCharCount];
		if (!value.TryFormat(charBuffer, out bytesWritten, format, CultureInfo.InvariantCulture))
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

	private const int MaximumLength = 64;

	public string MediaType { get; } = "text/plain";

	public bool TryGetMaximumSerializedLength(T value, out int size)
	{
		size = MaximumLength;
		return true;
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out T value)
	{
		return T.TryParse(buffer, CultureInfo.InvariantCulture, out value!);
	}

	public Task<T> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public void Serialize(T value, IBufferWriter<byte> buffer)
	{
		var span = buffer.GetSpan(MaximumLength);
		if (!Serialize(value, span, out var written))
		{
			throw new InvalidOperationException($"Failed to format {typeof(T).FullName} as UTF-8.");
		}
		buffer.Advance(written);
	}

	public bool Serialize(T value, Span<byte> buffer, out int bytesWritten)
	{
		return value.TryFormat(buffer, out bytesWritten, format, CultureInfo.InvariantCulture);
	}

	public Task SerializeAsync(T value, Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}
}
