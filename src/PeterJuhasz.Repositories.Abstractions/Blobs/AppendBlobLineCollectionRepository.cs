using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Serialization;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Buffers;
using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PeterJuhasz.Repositories.Blobs;

public class AppendBlobLineCollectionRepository<T>(
	IAppendBlob blob,
	ISerializer<T> serializer
)
	: ICollectionRepository<T>
{
	private const byte NewLineByte = (byte)'\n';

	public async Task<bool> AddAsync(T value, CancellationToken cancellationToken)
	{
		using var _ = ArrayBufferWriterPool<byte>.GetPooledObject(out var writer);
		serializer.Serialize(value, writer);
		var newLineSpan = writer.GetSpan(1);
		newLineSpan[0] = NewLineByte;
		writer.Advance(1);
		await blob.AppendAsync(writer.WrittenMemory, cancellationToken);
		return true;
	}

	public async Task AddRangeAsync(IReadOnlyCollection<T> value, CancellationToken cancellationToken)
	{
		if (value.Count == 0)
		{
			return;
		}

		using var writer = new MemoryPoolBufferWriter<byte>(MemoryPool<byte>.Shared);
		foreach (var item in value)
		{
			serializer.Serialize(item, writer);
			var newLineSpan = writer.GetSpan(1);
			newLineSpan[0] = NewLineByte;
			writer.Advance(1);
		}
		await blob.AppendAsync(writer.WrittenMemory, cancellationToken);
	}

	public async IAsyncEnumerable<T> AsAsyncEnumerableAsync([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var result = await blob.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			yield break;
		}

		var stream = result.Value;
		await using (stream)
		{
			await foreach (var item in ReadItemsAsync(stream, cancellationToken))
			{
				yield return item;
			}
		}
	}

	private async IAsyncEnumerable<T> ReadItemsAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var reader = PipeReader.Create(stream);
		try
		{
			while (await reader.ReadLineAsync(maximumLength: 65_536, cancellationToken) is { IsEmpty: false } line)
			{
				// the last line may not be terminated
				var trimmed = TrimEndNewLine(line);

				if (!trimmed.IsEmpty && serializer.Deserialize(trimmed, out var item))
				{
					yield return item;
				}

				reader.AdvanceTo(line.End, line.End);
			}
		}
		finally
		{
			await reader.CompleteAsync();
		}
	}

	private static ReadOnlySequence<byte> TrimEndNewLine(ReadOnlySequence<byte> line)
	{
		if (line.Length > 0 && line.Slice(line.Length - 1).FirstSpan[0] == NewLineByte)
		{
			line = line.Slice(0, line.Length - 1);
		}

		if (line.Length > 0 && line.Slice(line.Length - 1).FirstSpan[0] == (byte)'\r')
		{
			line = line.Slice(0, line.Length - 1);
		}

		return line;
	}

	public Task ClearAsync(CancellationToken cancellationToken) => blob.DeleteAsync(cancellationToken);

	public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
	{
		var result = await blob.GetInfoAsync(cancellationToken);
		return result?.ConcurrencyToken;
	}

	public async Task<Versioned<IReadOnlyCollection<T>>> ListWithVersionAsync(CancellationToken cancellationToken)
	{
		var result = await blob.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			return new([], null!);
		}

		var stream = result.Value;
		await using (stream)
		{
			var list = new List<T>();
			await foreach (var item in ReadItemsAsync(stream, cancellationToken))
			{
				list.Add(item);
			}
			return new(list, result.Info.ConcurrencyToken);
		}
	}

	public async Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken)
	{
		var result = await blob.OpenReadAsync(cancellationToken);
		if (result == null)
		{
			return null;
		}

		return new(result.Value, result.Info.LastModified, result.Info.ConcurrencyToken, result.Info.ContentEncoding);
	}


	public Task ApplyAsync(Func<IImmutableList<T>?, CancellationToken, ValueTask<IReadOnlyCollection<T>?>> update, CancellationToken cancellationToken) => throw new NotSupportedException();
}

public static partial class Extensions
{
	extension(IAppendBlob blob)
	{
		public ICollectionRepository<T> AsLineCollectionRepository<T>(ISerializer<T> serializer) =>
			new AppendBlobLineCollectionRepository<T>(blob, serializer);


		public ICollectionRepository<T> AsJsonLineCollectionRepository<T>(JsonSerializerOptions jsonSerializerOptions) =>
			new AppendBlobLineCollectionRepository<T>(blob, new JsonSerializerOptionsJsonSerializer<T>(jsonSerializerOptions));

		public ICollectionRepository<T> AsJsonLineCollectionRepository<T>(JsonTypeInfo<T> context) =>
			new AppendBlobLineCollectionRepository<T>(blob, new JsonTypeInfoJsonSerializer<T>(context));

		public ICollectionRepository<T> AsJsonLineCollectionRepository<T>(JsonSerializerContext context) =>
			new AppendBlobLineCollectionRepository<T>(blob, new JsonTypeInfoJsonSerializer<T>(context.GetTypeInfo<T>()));
	}
}