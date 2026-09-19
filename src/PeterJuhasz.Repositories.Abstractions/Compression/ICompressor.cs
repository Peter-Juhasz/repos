namespace PeterJuhasz.Repositories.Compression;

public interface ICompressor
{
	string ContentEncoding { get; }

	bool TryGetMaxCompressedLength(int inputLength, out int compressedLength)
	{
		compressedLength = -1;
		return false;
	}

	bool Compress(ReadOnlySpan<byte> input, Span<byte> output, out int written);

	bool Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written);
}

public interface IStreamCompressor
{
	string ContentEncoding { get; }

	Stream Compress(Stream input);

	Stream Decompress(Stream input);
}

public class IdentityCompressor : ICompressor, IStreamCompressor
{
	public static readonly IdentityCompressor Default = new();

	public string ContentEncoding { get; } = "identity";

	public Stream Compress(Stream input) => input;

	public Stream Decompress(Stream input) => input;

	public bool TryGetMaxCompressedLength(int inputLength, out int compressedLength)
	{
		compressedLength = inputLength;
		return true;
	}

	public bool Compress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
	{
		if (output.Length < input.Length)
		{
			written = 0;
			return false;
		}
		input.CopyTo(output);
		written = input.Length;
		return true;
	}

	public bool Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int written)
	{
		if (output.Length < input.Length)
		{
			written = 0;
			return false;
		}
		input.CopyTo(output);
		written = input.Length;
		return true;
	}
}
