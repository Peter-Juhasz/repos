using PeterJuhasz.Repositories.Serialization;
using System.Buffers;
using System.Globalization;

namespace PeterJuhasz.Repositories.Tests.Serialization;

internal static class SerializerTestHelpers
{
	public static byte[] SerializeToBufferWriter<T>(this ISerializer<T> serializer, T value)
	{
		var writer = new ArrayBufferWriter<byte>();
		serializer.Serialize(value, writer);
		return writer.WrittenSpan.ToArray();
	}

	public static byte[] SerializeToSpan<T>(this ISerializer<T> serializer, T value)
	{
		Assert.IsTrue(serializer.TryGetMaximumSerializedLength(value, out var maximumLength));
		var buffer = new byte[maximumLength];
		Assert.IsTrue(serializer.Serialize(value, buffer, out var bytesWritten));
		Assert.IsLessThanOrEqualTo(maximumLength, bytesWritten);
		return buffer[..bytesWritten];
	}

	public static T DeserializeSegmented<T>(this ISerializer<T> serializer, byte[] data)
	{
		Assert.IsTrue(serializer.Deserialize(Segmented(data), out var value));
		return value;
	}

	/// <summary>Creates a sequence with one segment per byte.</summary>
	public static ReadOnlySequence<byte> Segmented(byte[] data)
	{
		if (data.Length == 0)
		{
			return ReadOnlySequence<byte>.Empty;
		}

		var first = new Segment(data.AsMemory(0, 1), 0);
		var last = first;
		for (var i = 1; i < data.Length; i++)
		{
			last = last.Append(data.AsMemory(i, 1));
		}

		return new(first, 0, last, last.Memory.Length);
	}

	public static void WithCulture(string name, Action action)
	{
		var original = CultureInfo.CurrentCulture;
		CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
		try
		{
			action();
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}

	private sealed class Segment : ReadOnlySequenceSegment<byte>
	{
		public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
		{
			Memory = memory;
			RunningIndex = runningIndex;
		}

		public Segment Append(ReadOnlyMemory<byte> memory)
		{
			var next = new Segment(memory, RunningIndex + Memory.Length);
			Next = next;
			return next;
		}
	}
}
