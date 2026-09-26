using System.Buffers;

namespace PeterJuhasz.Repositories.Serialization.Json;

internal static class Utf8Bom
{
	private static ReadOnlySpan<byte> Preamble => [0xEF, 0xBB, 0xBF];

	public static ReadOnlySpan<byte> Skip(ReadOnlySpan<byte> buffer) => buffer.StartsWith(Preamble) ? buffer[Preamble.Length..] : buffer;

	public static ReadOnlySequence<byte> Skip(ReadOnlySequence<byte> buffer)
	{
		if (buffer.Length < Preamble.Length)
		{
			return buffer;
		}

		Span<byte> prefix = stackalloc byte[3];
		buffer.Slice(0, prefix.Length).CopyTo(prefix);
		return prefix.SequenceEqual(Preamble) ? buffer.Slice(Preamble.Length) : buffer;
	}
}