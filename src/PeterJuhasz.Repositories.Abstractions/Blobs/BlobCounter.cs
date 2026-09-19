using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Serialization;
using System.Buffers;

namespace PeterJuhasz.Repositories.Blobs;

public sealed class BlobCounter(IBlob blob, ISerializer<int> serializer) : IDistributedCounter
{
	public IBlob Blob => blob;

	public async ValueTask<int> GetAsync(CancellationToken cancellationToken)
	{
		var result = await blob.ReadAsync(cancellationToken);
		if (result is null)
		{
			return 0;
		}

		return serializer.Deserialize(result.Value.ToMemory().Span);
	}

	public async ValueTask<int> ApplyAsync(Func<int, int> transform, CancellationToken cancellationToken)
	{
		var result = 0;

		await blob.TransformAsync(data =>
		{
			var oldValue = default(int);

			if (data != null)
			{
				oldValue = serializer.Deserialize(data.ToMemory().Span);
			}

			result = transform(oldValue);

			if (result == default)
			{
				return null;
			}

			if (result == oldValue)
			{
				return data;
			}

			if (serializer.TryGetMaximumSerializedLength(result, out var maximumLength))
			{
				var buffer = new byte[maximumLength];
				serializer.Serialize(result, buffer, out var bytesWritten);
				return new(buffer.AsMemory(0, bytesWritten), serializer.MediaType);
			}
			else
			{
				using var _ = ArrayBufferWriterPool<byte>.GetPooledObject(out var writer);
				serializer.Serialize(result, writer);
				return new(writer.WrittenMemory.ToArray(), serializer.MediaType);
			}
		}, cancellationToken);

		return result;
	}
}

public static partial class Extensions
{
	extension(IBlob blob)
	{
		public IDistributedCounter AsCounter(ISerializer<int> serializer) => new BlobCounter(blob, serializer);

		public IDistributedCounter AsCounter() => new BlobCounter(blob, Utf8FormattableSerializer<int>.Int32Serializer);
	}
}