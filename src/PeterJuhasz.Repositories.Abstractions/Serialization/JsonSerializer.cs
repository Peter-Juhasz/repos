using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PeterJuhasz.Repositories.Serialization.Json;

public sealed class JsonSerializerOptionsJsonSerializer<T>(JsonSerializerOptions jsonSerializerOptions) : ISerializer<T>
{
	public string MediaType { get; } = "application/json";

	public void Serialize(T value, IBufferWriter<byte> buffer)
	{
		using var _ = Utf8JsonWriterPool.GetPooledObject(out var writer);
		writer.Reset(buffer);
		JsonSerializer.Serialize(writer, value, jsonSerializerOptions);
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out T value)
	{
		if (buffer.StartsWith([(byte)0xEF, (byte)0xBB, (byte)0xBF]))
		{
			buffer = buffer[3..];
		}

		value = JsonSerializer.Deserialize<T>(buffer, jsonSerializerOptions)!;
		return true;
	}

	public bool Deserialize(ReadOnlySequence<byte> buffer, out T value)
	{
		var reader = new Utf8JsonReader(buffer);
		value = JsonSerializer.Deserialize<T>(ref reader, jsonSerializerOptions)!;
		return true;
	}

	public Task SerializeAsync(T value, Stream stream, CancellationToken cancellationToken) => JsonSerializer.SerializeAsync(stream, value, jsonSerializerOptions, cancellationToken);

	public async Task<T> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		return await JsonSerializer.DeserializeAsync<T>(stream, jsonSerializerOptions, cancellationToken) ?? throw new JsonException("Deserialization returned null");
	}
}

public sealed class JsonSerializerOptionsJsonCollectionSerializer<T>(JsonSerializerOptions jsonSerializerOptions) : ICollectionSerializer<T>
{
	public static readonly JsonSerializerOptionsJsonCollectionSerializer<T> Default = new(JsonSerializerOptions.Default);
	public static readonly JsonSerializerOptionsJsonCollectionSerializer<T> Web = new(JsonSerializerOptions.Web);
	public static readonly JsonSerializerOptionsJsonCollectionSerializer<T> Strict = new(JsonSerializerOptions.Strict);

	public string MediaType { get; } = "application/json";

	public void Serialize(IReadOnlyCollection<T> value, IBufferWriter<byte> buffer)
	{
		using var _ = Utf8JsonWriterPool.GetPooledObject(out var writer);
		writer.Reset(buffer);
		JsonSerializer.Serialize(writer, value, jsonSerializerOptions);
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out IReadOnlyCollection<T> value)
	{
		if (buffer.StartsWith([(byte)0xEF, (byte)0xBB, (byte)0xBF]))
		{
			buffer = buffer[3..];
		}

		value = JsonSerializer.Deserialize<IReadOnlyCollection<T>>(buffer, jsonSerializerOptions)!;
		return true;
	}

	public bool Deserialize(ReadOnlySequence<byte> buffer, out IReadOnlyCollection<T> value)
	{
		var reader = new Utf8JsonReader(buffer);
		value = JsonSerializer.Deserialize<IReadOnlyCollection<T>>(ref reader, jsonSerializerOptions)!;
		return true;
	}

	public Task SerializeAsync(IReadOnlyCollection<T> value, Stream stream, CancellationToken cancellationToken) => JsonSerializer.SerializeAsync(stream, value, jsonSerializerOptions, cancellationToken);

	public async Task<IReadOnlyCollection<T>> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		return await JsonSerializer.DeserializeAsync<IReadOnlyCollection<T>>(stream, jsonSerializerOptions, cancellationToken) ?? throw new JsonException("Deserialization returned null");
	}

	public async IAsyncEnumerable<T> DeserializeAsyncEnumerable(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<T>(stream, jsonSerializerOptions, cancellationToken))
		{
			if (item is null)
			{
				continue;
			}

			yield return item;
		}
	}
}

public sealed class JsonTypeInfoJsonSerializer<T>(JsonTypeInfo<T> jsonTypeInfo) : ISerializer<T>
{
	public JsonTypeInfoJsonSerializer(JsonSerializerContext context)
		: this(context.GetTypeInfo<T>())
	{ }

	public string MediaType { get; } = "application/json";

	public void Serialize(T value, IBufferWriter<byte> buffer)
	{
		using var _ = Utf8JsonWriterPool.GetPooledObject(out var writer);
		writer.Reset(buffer);
		JsonSerializer.Serialize(writer, value, jsonTypeInfo);
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out T value)
	{
		if (buffer.StartsWith([(byte)0xEF, (byte)0xBB, (byte)0xBF]))
		{
			buffer = buffer[3..];
		}

		value = JsonSerializer.Deserialize(buffer, jsonTypeInfo)!;
		return true;
	}

	public bool Deserialize(ReadOnlySequence<byte> buffer, out T value)
	{
		var reader = new Utf8JsonReader(buffer);
		value = JsonSerializer.Deserialize<T>(ref reader, jsonTypeInfo)!;
		return true;
	}

	public async Task SerializeAsync(T value, Stream stream, CancellationToken cancellationToken)
	{
		await JsonSerializer.SerializeAsync(stream, value, jsonTypeInfo, cancellationToken);
	}

	public async Task<T> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		return await JsonSerializer.DeserializeAsync<T>(stream, jsonTypeInfo, cancellationToken) ?? throw new JsonException("Deserialization returned null");
	}
}

public sealed class JsonTypeInfoJsonCollectionSerializer<T>(
	JsonTypeInfo<IReadOnlyCollection<T>> collectionJsonTypeInfo,
	JsonTypeInfo<T> itemJsonTypeInfo
) : ICollectionSerializer<T>
{
	public JsonTypeInfoJsonCollectionSerializer(JsonSerializerContext context) : this(
		context.GetTypeInfo<IReadOnlyCollection<T>>(),
		context.GetTypeInfo<T>()
	)
	{ }

	public string MediaType { get; } = "application/json";

	public void Serialize(IReadOnlyCollection<T> value, IBufferWriter<byte> buffer)
	{
		using var _ = Utf8JsonWriterPool.GetPooledObject(out var writer);
		writer.Reset(buffer);
		JsonSerializer.Serialize(writer, value, collectionJsonTypeInfo);
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out IReadOnlyCollection<T> value)
	{
		if (buffer.StartsWith([(byte)0xEF, (byte)0xBB, (byte)0xBF]))
		{
			buffer = buffer[3..];
		}

		value = JsonSerializer.Deserialize(buffer, collectionJsonTypeInfo)!;
		return true;
	}

	public bool Deserialize(ReadOnlySequence<byte> buffer, out IReadOnlyCollection<T> value)
	{
		var reader = new Utf8JsonReader(buffer);
		value = JsonSerializer.Deserialize<IReadOnlyCollection<T>>(ref reader, collectionJsonTypeInfo)!;
		return true;
	}

	public async Task SerializeAsync(IReadOnlyCollection<T> value, Stream stream, CancellationToken cancellationToken)
	{
		await JsonSerializer.SerializeAsync(stream, value, collectionJsonTypeInfo, cancellationToken);
	}

	public async Task<IReadOnlyCollection<T>> DeserializeAsync(Stream stream, CancellationToken cancellationToken)
	{
		return await JsonSerializer.DeserializeAsync<IReadOnlyCollection<T>>(stream, collectionJsonTypeInfo, cancellationToken) ?? throw new JsonException("Deserialization returned null");
	}

	public async IAsyncEnumerable<T> DeserializeAsyncEnumerable(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<T>(stream, itemJsonTypeInfo, cancellationToken))
		{
			if (item is null)
			{
				continue;
			}

			yield return item;
		}
	}
}