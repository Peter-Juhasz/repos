using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Serialization;
using System.Buffers;
using System.Text;

namespace PeterJuhasz.Repositories.Blobs;

public class BlobNameSetRepository<T>(
	IBlobPartition partition,
	ISerializer<T> serializer,
	Encoding encoding
)
	: ISetRepository<T>
{
	private string Encode(T value)
	{
		using var _ = ArrayBufferWriterPool<byte>.GetPooledObject(out var writer);
		serializer.Serialize(value, writer);
		return encoding.GetString(writer.WrittenSpan);
	}

	private T Decode(ReadOnlySpan<char> value)
	{
		Span<byte> buffer = stackalloc byte[encoding.GetByteCount(value)];
		encoding.GetBytes(value, buffer);
		return serializer.Deserialize(buffer);
	}

	private IBlob GetBlob(T value) => partition.GetBlob(Encode(value));

	public async Task<bool> AddAsync(T value, CancellationToken cancellationToken)
	{
		var blob = GetBlob(value);
		try
		{
			await blob.WriteAsync(ReadOnlyMemory<byte>.Empty, null, default, cancellationToken);
		}
		catch (ConflictException)
		{
			return false;
		}
		return true;
	}

	public async Task<bool> ContainsAsync(T value, CancellationToken cancellationToken)
	{
		var blob = GetBlob(value);
		return await blob.ExistsAsync(cancellationToken);
	}

	public async Task ClearAsync(CancellationToken cancellationToken)
	{
		await partition.ClearAsync(cancellationToken);
	}

	public async Task<bool> DeleteAsync(T value, CancellationToken cancellationToken)
	{
		var blob = GetBlob(value);
		return await blob.DeleteIfExistsAsync(cancellationToken);
	}

	public IAsyncEnumerable<T> ListAsync(CancellationToken cancellationToken)
	{
		return partition.GetBlobs(cancellationToken).Select(item =>
		{
			var separatorIndex = item.Name.LastIndexOf('/');
			if (separatorIndex == -1)
			{
				return Decode(item.Name);
			}

			return Decode(item.Name.AsSpan(separatorIndex + 1));
		});
	}
}

public sealed class BlobNameStringSet(
	IBlobPartition partition
)
	: ISetRepository<string>
{
	private IBlob GetBlob(string value) => partition.GetBlob(value);

	public async Task<bool> AddAsync(string value, CancellationToken cancellationToken)
	{
		var blob = GetBlob(value);
		try
		{
			await blob.WriteAsync(ReadOnlyMemory<byte>.Empty, null, default, cancellationToken);
		}
		catch (ConflictException)
		{
			return false;
		}
		return true;
	}

	public async Task<bool> ContainsAsync(string value, CancellationToken cancellationToken)
	{
		var blob = GetBlob(value);
		return await blob.ExistsAsync(cancellationToken);
	}

	public async Task ClearAsync(CancellationToken cancellationToken)
	{
		await partition.ClearAsync(cancellationToken);
	}

	public async Task<bool> DeleteAsync(string value, CancellationToken cancellationToken)
	{
		var blob = GetBlob(value);
		return await blob.DeleteIfExistsAsync(cancellationToken);
	}

	public IAsyncEnumerable<string> ListAsync(CancellationToken cancellationToken)
	{
		return partition.GetBlobs(cancellationToken).Select(item =>
		{
			var separatorIndex = item.Name.LastIndexOf('/');
			if (separatorIndex == -1)
			{
				return item.Name;
			}

			return item.Name[(separatorIndex + 1)..];
		});
	}
}

public static partial class Extensions
{
	public static ISetRepository<string> AsStringSetRepositoryAsNames(this IBlobPartition partition) =>
		new BlobNameStringSet(partition);

	public static ISetRepository<T> AsSetRepositoryAsNames<T>(this IBlobPartition partition, ISerializer<T> serializer, Encoding encoding) =>
		new BlobNameSetRepository<T>(partition, serializer, encoding);
}