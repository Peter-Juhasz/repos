using PeterJuhasz.Repositories.Blobs;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace PeterJuhasz.Repositories.InMemory;

public sealed class InMemoryBlobPartition : IBlobPartition
{
	private static int _lastRootId;

	private readonly ConcurrentDictionary<string, object> _blobs;
	private readonly TimeProvider _timeProvider;

	/// <summary>
	/// Prefix of the full names of blobs in this partition, e.g. <c>memory://1/a/b/</c>.
	/// Starts with an id unique to the root partition, so blobs of separate roots don't share names.
	/// </summary>
	private readonly string _prefix;

	public InMemoryBlobPartition(TimeProvider timeProvider)
		: this(new(StringComparer.Ordinal), timeProvider, $"memory://{Interlocked.Increment(ref _lastRootId)}/", path: null)
	{ }

	private InMemoryBlobPartition(ConcurrentDictionary<string, object> blobs, TimeProvider timeProvider, string prefix, string? path)
	{
		_blobs = blobs;
		_timeProvider = timeProvider;
		_prefix = prefix;
		Path = path;
	}

	public string? Path { get; }

	public IBlob GetBlob(string name) => GetOrAdd(GetBlobName(name), static (name, timeProvider) => new InMemoryBlob(name, timeProvider));

	public IAppendBlob GetAppendBlob(string name) => GetOrAdd(GetBlobName(name), static (name, timeProvider) => new InMemoryAppendBlob(name, timeProvider));

	public IBlobPartition GetSubPartition(string name) => new InMemoryBlobPartition(_blobs, _timeProvider, $"{_prefix}{name}/", Path == null ? name : $"{Path}/{name}");

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

	private string GetBlobName(string name) => _prefix + name;

	/// <summary>
	/// Blobs of this partition and its sub-partitions, ordered by name.
	/// </summary>
	private IEnumerable<object> GetEntries()
	{
		// every name in the root shares its prefix, so filtering is only needed for sub-partitions
		var prefix = Path == null ? null : _prefix;
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
