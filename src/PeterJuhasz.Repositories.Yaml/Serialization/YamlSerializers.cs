using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using PeterJuhasz.Text.Yaml.Serialization;
using PeterJuhasz.Text.Yaml.Writing;

namespace PeterJuhasz.Repositories.Serialization.Yaml;

public sealed class YamlSerializerOptionsYamlSerializer<T>(YamlSerializerOptions yamlSerializerOptions) : ISerializer<T>
{
	public static readonly YamlSerializerOptionsYamlSerializer<T> Default = new(YamlSerializerOptions.Default);

	public string MediaType { get; } = YamlEncoding.MediaType;

	public void Serialize(T value, IBufferWriter<byte> buffer) => YamlEncoding.Serialize(value, typeof(T), buffer, yamlSerializerOptions);

	public bool Deserialize(ReadOnlySpan<byte> buffer, out T value)
	{
		value = YamlSerializer.Deserialize<T>(YamlEncoding.GetString(buffer), yamlSerializerOptions)!;
		return true;
	}

	public bool Deserialize(ReadOnlySequence<byte> buffer, out T value)
	{
		value = YamlSerializer.Deserialize<T>(YamlEncoding.GetString(buffer), yamlSerializerOptions)!;
		return true;
	}

	public Task SerializeAsync(T value, Stream stream, CancellationToken cancellationToken) => YamlEncoding.SerializeAsync(value, typeof(T), stream, yamlSerializerOptions, cancellationToken);

	public async Task<T> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		var yaml = await YamlEncoding.ReadToEndAsync(stream, cancellationToken);
		return YamlSerializer.Deserialize<T>(yaml, yamlSerializerOptions) ?? throw new InvalidOperationException("Deserialization returned null");
	}
}

public sealed class YamlSerializerOptionsYamlCollectionSerializer<T>(YamlSerializerOptions yamlSerializerOptions) : ICollectionSerializer<T>
{
	public static readonly YamlSerializerOptionsYamlCollectionSerializer<T> Default = new(YamlSerializerOptions.Default);

	public string MediaType { get; } = YamlEncoding.MediaType;

	public void Serialize(IReadOnlyCollection<T> items, IBufferWriter<byte> buffer) => YamlEncoding.Serialize(items, typeof(IReadOnlyCollection<T>), buffer, yamlSerializerOptions);

	public bool Deserialize(ReadOnlySpan<byte> buffer, out IReadOnlyCollection<T> items)
	{
		items = YamlSerializer.Deserialize<T[]>(YamlEncoding.GetString(buffer), yamlSerializerOptions)!;
		return true;
	}

	public bool Deserialize(ReadOnlySequence<byte> buffer, out IReadOnlyCollection<T> items)
	{
		items = YamlSerializer.Deserialize<T[]>(YamlEncoding.GetString(buffer), yamlSerializerOptions)!;
		return true;
	}

	public Task SerializeAsync(IReadOnlyCollection<T> items, Stream stream, CancellationToken cancellationToken) => YamlEncoding.SerializeAsync(items, typeof(IReadOnlyCollection<T>), stream, yamlSerializerOptions, cancellationToken);

	public async Task<IReadOnlyCollection<T>> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		var yaml = await YamlEncoding.ReadToEndAsync(stream, cancellationToken);
		return YamlSerializer.Deserialize<T[]>(yaml, yamlSerializerOptions) ?? throw new InvalidOperationException("Deserialization returned null");
	}

	// YAML documents can't be read incrementally, so the whole document is parsed before the items are yielded
	public async IAsyncEnumerable<T> DeserializeAsyncEnumerable(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var items = await DeserializeAsync(stream, cancellationToken);
		foreach (var item in items)
		{
			if (item is null)
			{
				continue;
			}

			yield return item;
		}
	}
}

internal static class YamlEncoding
{
	public const string MediaType = "application/yaml";

	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	private static ReadOnlySpan<byte> Preamble => [0xEF, 0xBB, 0xBF];

	public static void Serialize(object? value, Type type, IBufferWriter<byte> buffer, YamlSerializerOptions options)
	{
		using var _ = ArrayBufferWriterPool<char>.GetPooledObject(out var chars);
		YamlSerializer.Serialize(YamlWriter.Create(chars, options.FormattingOptions), value, type, options);
		Utf8.GetBytes(chars.WrittenSpan, buffer);
	}

	public static async Task SerializeAsync(object? value, Type type, Stream stream, YamlSerializerOptions options, CancellationToken cancellationToken)
	{
		using var _ = ArrayBufferWriterPool<byte>.GetPooledObject(out var bytes);
		Serialize(value, type, bytes, options);
		await stream.WriteAsync(bytes.WrittenMemory, cancellationToken);
	}

	public static string GetString(ReadOnlySpan<byte> buffer) => Utf8.GetString(buffer.StartsWith(Preamble) ? buffer[Preamble.Length..] : buffer);

	public static string GetString(ReadOnlySequence<byte> buffer)
	{
		if (buffer.Length >= Preamble.Length)
		{
			Span<byte> prefix = stackalloc byte[3];
			buffer.Slice(0, prefix.Length).CopyTo(prefix);
			if (prefix.SequenceEqual(Preamble))
			{
				buffer = buffer.Slice(Preamble.Length);
			}
		}

		return Utf8.GetString(buffer);
	}

	public static async Task<string> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
	{
		// detects and skips the byte order mark
		using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
		return await reader.ReadToEndAsync(cancellationToken);
	}
}
