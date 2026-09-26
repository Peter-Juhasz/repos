using PeterJuhasz.Repositories.Blobs;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace PeterJuhasz.Repositories.InMemory;

public sealed class InMemoryBlobPartition : IBlobPartition
{
	private readonly ConcurrentDictionary<string, object> _blobs;
	private readonly TimeProvider _timeProvider;

	public InMemoryBlobPartition(TimeProvider timeProvider)
		: this(new(StringComparer.Ordinal), timeProvider, path: null)
	{ }

	private InMemoryBlobPartition(ConcurrentDictionary<string, object> blobs, TimeProvider timeProvider, string? path)
	{
		_blobs = blobs;
		_timeProvider = timeProvider;
		Path = path;
	}

	public string? Path { get; }

	public IBlob GetBlob(string name) => GetOrAdd(GetBlobName(name), static (name, timeProvider) => new InMemoryBlob(name, timeProvider));

	public IAppendBlob GetAppendBlob(string name) => GetOrAdd(GetBlobName(name), static (name, timeProvider) => new InMemoryAppendBlob(name, timeProvider));

	public IBlobPartition GetSubPartition(string name) => new InMemoryBlobPartition(_blobs, _timeProvider, GetBlobName(name));

	public async IAsyncEnumerable<IBlob> GetBlobs([EnumeratorCancellation] CancellationToken cancellationToken)
	{
		foreach (var blob in GetEntries().OfType<InMemoryBlob>())
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (await blob.ExistsAsync(cancellationToken))
			{
				yield return blob;
			}
		}
	}

	public async Task ClearAsync(CancellationToken cancellationToken)
	{
		// delete contents instead of removing entries, so previously handed out instances stay connected to the partition
		foreach (var entry in GetEntries())
		{
			cancellationToken.ThrowIfCancellationRequested();

			switch (entry)
			{
				case InMemoryBlob blob:
					await blob.DeleteIfExistsAsync(cancellationToken);
					break;

				case InMemoryAppendBlob appendBlob:
					await appendBlob.DeleteAsync(cancellationToken);
					break;
			}
		}
	}

	private string GetBlobName(string name) => Path == null ? name : $"{Path}/{name}";

	/// <summary>
	/// Blobs of this partition and its sub-partitions, ordered by name.
	/// </summary>
	private IEnumerable<object> GetEntries()
	{
		var prefix = Path == null ? null : $"{Path}/";
		return _blobs
			.Where(e => prefix == null || e.Key.StartsWith(prefix, StringComparison.Ordinal))
			.OrderBy(e => e.Key, StringComparer.Ordinal)
			.Select(e => e.Value);
	}

	private TBlob GetOrAdd<TBlob>(string name, Func<string, TimeProvider, TBlob> factory) where TBlob : class
	{
		var blob = _blobs.GetOrAdd(name, static (name, state) => state.factory(name, state.timeProvider), (factory, timeProvider: _timeProvider));
		return blob as TBlob ?? throw new InvalidOperationException($"Blob '{name}' already exists as a different blob type.");
	}
}
