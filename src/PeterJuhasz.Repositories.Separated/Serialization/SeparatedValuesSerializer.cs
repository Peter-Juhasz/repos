using PeterJuhasz.Text.Separated;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;

namespace PeterJuhasz.Repositories.Serialization.Separated;

public sealed class SeparatedValuesSerializer<T>(SeparatedValuesReaderOptions readerOptions, SeparatedValuesWriterOptions writerOptions) : ICollectionSerializer<T>
{
	public string MediaType { get; } = "text/csv";

	private static readonly StreamPipeWriterOptions LeaveOpen = new(leaveOpen: true);

	public void Serialize(IReadOnlyCollection<T> value, IBufferWriter<byte> buffer)
	{
		throw new NotImplementedException();
	}

	public bool Deserialize(ReadOnlySpan<byte> buffer, out IReadOnlyCollection<T> value)
	{
		throw new NotImplementedException();
	}

	public bool Deserialize(ReadOnlySequence<byte> buffer, out IReadOnlyCollection<T> value)
	{
		throw new NotImplementedException();
	}

	public async Task SerializeAsync(IReadOnlyCollection<T> value, Stream stream, CancellationToken cancellationToken)
	{
		var pipeWriter = PipeWriter.Create(stream, LeaveOpen);
		var writer = new SeparatedValuesWriter(pipeWriter, writerOptions);
		writer.WriteHeader<T>();
		foreach (var item in value)
		{
			writer.WriteValues(item);
		}
		await pipeWriter.CompleteAsync();
		await writer.FlushAsync(cancellationToken);
		await stream.FlushAsync(cancellationToken);
	}

	public async IAsyncEnumerable<T> DeserializeAsyncEnumerable(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
	{
		var reader = new SeparatedValuesReader<T>(readerOptions);
		await foreach (var item in reader.ReadAsync(stream, cancellationToken))
		{
			yield return item;
		}
	}
}
