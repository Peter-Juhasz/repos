using System.Buffers;

namespace PeterJuhasz.Repositories.Serialization;

public sealed class DateTimeTicksSerializer(string format = "") : ISerializer<DateTime>, ISerializer<DateTimeOffset>
{
	public static readonly DateTimeTicksSerializer Instance = new();

	public string MediaType { get; } = "text/plain";

	public bool Deserialize(ReadOnlySpan<byte> buffer, out DateTime value)
	{
		if (!long.TryParse(buffer, out long ticks))
		{
			value = default;
			return false;
		}

		value = new DateTime(ticks, DateTimeKind.Utc);
		return true;
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out DateTimeOffset value)
	{
		if (!Deserialize(buffer, out DateTime dateTime))
		{
			value = default;
			return false;
		}

		value = new DateTimeOffset(dateTime, TimeSpan.Zero);
		return true;
	}

	public void Serialize(DateTime value, IBufferWriter<byte> buffer)
	{
		var span = buffer.GetSpan(20);
		if (!Serialize(value, span, out var written))
		{
			throw new InvalidOperationException($"Failed to format {typeof(DateTime).FullName} ticks as UTF-8.");
		}
		buffer.Advance(written);
	}

	public bool Serialize(DateTime value, Span<byte> buffer, out int bytesWritten)
	{
		return value.Ticks.TryFormat(buffer, out bytesWritten, format);
	}

	public void Serialize(DateTimeOffset value, IBufferWriter<byte> buffer)
	{
		Serialize(value.UtcDateTime, buffer);
	}

	public bool Serialize(DateTimeOffset value, Span<byte> buffer, out int bytesWritten)
	{
		return Serialize(value.UtcDateTime, buffer, out bytesWritten);
	}


	public Task SerializeAsync(DateTime value, Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	public Task SerializeAsync(DateTimeOffset value, Stream stream, CancellationToken cancellationToken)
	{
		throw new NotImplementedException();
	}

	public Task<DateTime> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		throw new NotSupportedException();
	}

	Task<DateTimeOffset> ISerializer<DateTimeOffset>.DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		throw new NotImplementedException();
	}
}