using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.Caching;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace PeterJuhasz.Repositories.Tests.Caching;

[TestClass]
public class CachingCollectionRepositoryTests(TestContext testContext)
{
	public sealed record class Item(string Name, int Count);

	private static readonly Item A = new("a", 1);
	private static readonly Item B = new("b", 2);
	private static readonly Item C = new("c", 3);

	private const string ABJson = """[{"name":"a","count":1},{"name":"b","count":2}]""";

	private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);

	private static readonly CacheOptions Expiring = new(SlidingExpiration: Expiration);

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlob Blob => field ??= new("test", _time);

	/// <summary>
	/// The uncached repository, used to arrange and inspect the underlying data without going through the cache.
	/// </summary>
	private ICollectionRepository<Item> Source => field ??= CreateBlobRepository();

	/// <summary>
	/// The repository wrapped by the cache, counting the calls that reach it.
	/// </summary>
	private SpyCollectionRepository<Item> Inner => field ??= new(Source);

	private ICollectionRepository<Item> CreateBlobRepository(IEqualityComparer<Item>? comparer = null) =>
		new BufferedBlobCollectionRepository<Item>(Blob, new JsonSerializerOptionsJsonCollectionSerializer<Item>(JsonSerializerOptions.Web), comparer);

	private ICollectionRepository<Item> CreateRepository(CacheOptions? cacheOptions = null) =>
		new CachingCollectionRepository<Item>(Inner, cacheOptions ?? CacheOptions.Immutable, _time);

	private Task<string> WriteBlobAsync(string content) =>
		Blob.WriteAsync(Encoding.UTF8.GetBytes(content), IBlob.AnyOrNoneConcurrencyToken, new(ContentEncoding: "identity", MediaType: "application/json"), CT);

	private async Task<(string Content, RawStreamResult Result)> ReadRawStreamAsync(ICollectionRepository<Item> repository)
	{
		var result = await repository.RawStreamAsync(CT);
		Assert.IsNotNull(result);
		await using var raw = result.Value;
		using var reader = new StreamReader(raw.Stream);
		return (await reader.ReadToEndAsync(CT), raw);
	}

	// Construction

	[TestMethod]
	public void Inner_ReturnsWrappedRepository()
	{
		var repository = new CachingCollectionRepository<Item>(Source, CacheOptions.Immutable, _time);

		Assert.AreSame(Source, repository.Inner);
	}

	[TestMethod]
	public void WithCaching_WrapsRepository()
	{
		var repository = Source.WithCaching(CacheOptions.Immutable);

		Assert.IsInstanceOfType<CachingCollectionRepository<Item>>(repository);
		Assert.AreSame(Source, ((CachingCollectionRepository<Item>)repository).Inner);
	}

	[TestMethod]
	public void TryGetBlob_ReturnsUnderlyingBlob()
	{
		var repository = Source.WithCaching(CacheOptions.Immutable);

		Assert.IsTrue(repository.TryGetBlob(out var blob));
		Assert.AreSame(Blob, blob);
	}

	// Not exists

	[TestMethod]
	public async Task NotExists_IsEmpty()
	{
		var repository = CreateRepository();

		var listed = await repository.ListWithVersionAsync(CT);
		Assert.IsEmpty(listed.Value);
		Assert.IsNull(listed.ETag);
		Assert.IsNull(await repository.GetVersionAsync(CT));
		Assert.AreEqual(0, await repository.CountAsync(CT));
		Assert.IsFalse(await repository.AnyAsync(CT));
		Assert.IsEmpty(await repository.AsAsyncEnumerableAsync(CT).ToListAsync(CT));
		Assert.IsNull(await repository.RawStreamAsync(CT));
	}

	[TestMethod]
	public async Task ListWithVersionAsync_NotExists_IsNotCached()
	{
		var repository = CreateRepository();
		await repository.ListWithVersionAsync(CT);

		await Source.StoreAsync([A], CT);

		Assert.AreSequenceEqual([A], await repository.ListAsync(CT));
		Assert.AreEqual(2, Inner.ListCalls);
	}

	[TestMethod]
	public async Task GetVersionAsync_NotExists_IsNotCached()
	{
		var repository = CreateRepository();
		Assert.IsNull(await repository.GetVersionAsync(CT));

		await Source.StoreAsync([A], CT);

		Assert.AreEqual(await Source.GetVersionAsync(CT), await repository.GetVersionAsync(CT));
		Assert.AreEqual(2, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task RawStreamAsync_NotExists_IsNotCached()
	{
		var repository = CreateRepository();
		Assert.IsNull(await repository.RawStreamAsync(CT));

		await WriteBlobAsync(ABJson);

		Assert.IsNotNull(await repository.RawStreamAsync(CT));
		Assert.AreEqual(2, Inner.RawStreamCalls);
	}

	// Immutable

	[TestMethod]
	public async Task ListWithVersionAsync_Miss_ReadsInner()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A, B], CT);

		var result = await repository.ListWithVersionAsync(CT);

		Assert.AreSequenceEqual([A, B], result.Value);
		Assert.AreEqual(await Source.GetVersionAsync(CT), result.ETag);
		Assert.AreEqual(1, Inner.ListCalls);
	}

	[TestMethod]
	public async Task ListWithVersionAsync_Hit_DoesNotCallInner()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A, B], CT);
		var first = await repository.ListWithVersionAsync(CT);

		var second = await repository.ListWithVersionAsync(CT);

		Assert.AreSame(first.Value, second.Value);
		Assert.AreEqual(first.ETag, second.ETag);
		Assert.AreEqual(1, Inner.ListCalls);
		Assert.AreEqual(0, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task ListWithVersionAsync_Hit_IgnoresExternalChanges()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A], CT);
		await repository.ListAsync(CT);

		await Source.StoreAsync([B], CT);

		Assert.AreSequenceEqual([A], await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ListWithVersionAsync_AfterGetVersion_ListsInnerOnce()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A, B], CT);
		await repository.GetVersionAsync(CT);

		await repository.ListAsync(CT);

		Assert.AreSequenceEqual([A, B], await repository.ListAsync(CT));
		Assert.AreEqual(1, Inner.ListCalls);
	}

	[TestMethod]
	public async Task GetVersionAsync_Hit_DoesNotCallInner()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A], CT);
		var version = await repository.GetVersionAsync(CT);

		Assert.AreEqual(version, await repository.GetVersionAsync(CT));
		Assert.AreEqual(1, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task GetVersionAsync_AfterList_ReturnsCachedVersion()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A], CT);
		var listed = await repository.ListWithVersionAsync(CT);

		Assert.AreEqual(listed.ETag, await repository.GetVersionAsync(CT));
		Assert.AreEqual(0, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task CountAsync_Hit_UsesCachedItems()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A, B], CT);
		await repository.ListAsync(CT);

		Assert.AreEqual(2, await repository.CountAsync(CT));
		Assert.AreEqual(1, Inner.ListCalls);
	}

	[TestMethod]
	public async Task CountAsync_Miss_PopulatesCache()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A, B, C], CT);

		Assert.AreEqual(3, await repository.CountAsync(CT));

		Assert.AreSequenceEqual([A, B, C], await repository.ListAsync(CT));
		Assert.AreEqual(1, Inner.ListCalls);
	}

	[TestMethod]
	public async Task QueryMethods_UseCachedItems()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A, B], CT);

		Assert.AreSequenceEqual([A, B], await repository.AsAsyncEnumerableAsync(CT).ToListAsync(CT));
		Assert.IsTrue(await repository.AnyAsync(i => i.Name == "b", CT));
		Assert.AreEqual(1, await repository.CountAsync(i => i.Count > 1, CT));
		Assert.AreEqual(B, await repository.GetOrDefaultAsync(i => i.Name == "b", CT));

		Assert.AreEqual(1, Inner.ListCalls);
		Assert.AreEqual(0, Inner.EnumerateCalls);
	}

	[TestMethod]
	public async Task RawStreamAsync_Miss_ReturnsContentAndInfo()
	{
		var repository = CreateRepository();
		var version = await WriteBlobAsync(ABJson);

		var (content, result) = await ReadRawStreamAsync(repository);

		Assert.AreEqual(version, result.ETag);
		Assert.AreEqual(_time.GetUtcNow(), result.LastModified);
		Assert.AreEqual("identity", result.Encoding);
		Assert.AreEqual(ABJson, content);
	}

	[TestMethod]
	public async Task RawStreamAsync_Hit_ReturnsCachedContentAndInfo()
	{
		var repository = CreateRepository();
		var version = await WriteBlobAsync(ABJson);
		var lastModified = _time.GetUtcNow();
		await ReadRawStreamAsync(repository);
		_time.Advance(TimeSpan.FromMinutes(1));
		await WriteBlobAsync("[]");

		var (content, result) = await ReadRawStreamAsync(repository);

		Assert.AreEqual(ABJson, content);
		Assert.AreEqual(version, result.ETag);
		Assert.AreEqual(lastModified, result.LastModified);
		Assert.AreEqual("identity", result.Encoding);
		Assert.AreEqual(1, Inner.RawStreamCalls);
	}

	[TestMethod]
	public async Task RawStreamAsync_Hit_ReturnsNewStreamEachTime()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ABJson);
		await ReadRawStreamAsync(repository);

		Assert.AreEqual(ABJson, (await ReadRawStreamAsync(repository)).Content);
		Assert.AreEqual(ABJson, (await ReadRawStreamAsync(repository)).Content);
	}

	[TestMethod]
	public async Task RawStreamAsync_SameVersion_KeepsCachedItems()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A, B], CT);
		await repository.ListAsync(CT);

		await ReadRawStreamAsync(repository);

		Assert.AreSequenceEqual([A, B], await repository.ListAsync(CT));
		Assert.AreEqual(1, Inner.ListCalls);
	}

	[TestMethod]
	public async Task ListWithVersionAsync_SameVersion_KeepsCachedRawStream()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ABJson);
		await ReadRawStreamAsync(repository);

		await repository.ListAsync(CT);

		Assert.AreEqual(ABJson, (await ReadRawStreamAsync(repository)).Content);
		Assert.AreEqual(1, Inner.RawStreamCalls);
	}

	// MustRevalidate

	[TestMethod]
	public async Task Revalidate_ListWithVersionAsync_Unchanged_ServesCachedItems()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.StoreAsync([A, B], CT);
		var first = await repository.ListWithVersionAsync(CT);

		var second = await repository.ListWithVersionAsync(CT);

		Assert.AreSame(first.Value, second.Value);
		Assert.AreEqual(1, Inner.ListCalls);
		Assert.AreEqual(1, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task Revalidate_ListWithVersionAsync_Changed_RefetchesAndCaches()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.StoreAsync([A], CT);
		await repository.ListAsync(CT);
		await Source.StoreAsync([B, C], CT);

		var result = await repository.ListWithVersionAsync(CT);

		Assert.AreSequenceEqual([B, C], result.Value);
		Assert.AreEqual(await Source.GetVersionAsync(CT), result.ETag);
		Assert.AreEqual(2, Inner.ListCalls);

		await repository.ListAsync(CT);
		Assert.AreEqual(2, Inner.ListCalls);
	}

	[TestMethod]
	public async Task Revalidate_ListWithVersionAsync_Deleted_ReturnsEmpty()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.StoreAsync([A], CT);
		await repository.ListAsync(CT);

		await Source.ClearAsync(CT);

		var result = await repository.ListWithVersionAsync(CT);
		Assert.IsEmpty(result.Value);
		Assert.IsNull(result.ETag);
	}

	[TestMethod]
	public async Task Revalidate_GetVersionAsync_AlwaysCallsInner()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.StoreAsync([A], CT);
		await repository.GetVersionAsync(CT);

		await Source.StoreAsync([B], CT);

		Assert.AreEqual(await Source.GetVersionAsync(CT), await repository.GetVersionAsync(CT));
		Assert.AreEqual(2, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task Revalidate_GetVersionAsync_Unchanged_KeepsCachedItems()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.StoreAsync([A], CT);
		await repository.ListAsync(CT);

		await repository.GetVersionAsync(CT);

		Assert.AreSequenceEqual([A], await repository.ListAsync(CT));
		Assert.AreEqual(1, Inner.ListCalls);
	}

	[TestMethod]
	public async Task Revalidate_GetVersionAsync_Changed_DropsCachedItems()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.StoreAsync([A], CT);
		await repository.ListAsync(CT);
		await Source.StoreAsync([B], CT);

		await repository.GetVersionAsync(CT);

		Assert.AreSequenceEqual([B], await repository.ListAsync(CT));
		Assert.AreEqual(2, Inner.ListCalls);
	}

	[TestMethod]
	public async Task Revalidate_CountAsync_Unchanged_UsesCachedItems()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.StoreAsync([A, B], CT);
		await repository.ListAsync(CT);

		Assert.AreEqual(2, await repository.CountAsync(CT));
		Assert.AreEqual(1, Inner.ListCalls);
	}

	[TestMethod]
	public async Task Revalidate_CountAsync_Changed_ReturnsNewCount()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.StoreAsync([A], CT);
		Assert.AreEqual(1, await repository.CountAsync(CT));

		await Source.StoreAsync([A, B, C], CT);

		Assert.AreEqual(3, await repository.CountAsync(CT));
		Assert.AreEqual(1, Inner.VersionCalls);
		Assert.AreEqual(2, Inner.ListCalls);
	}

	[TestMethod]
	public async Task Revalidate_RawStreamAsync_Unchanged_ServesCachedContent()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await WriteBlobAsync(ABJson);
		await ReadRawStreamAsync(repository);

		Assert.AreEqual(ABJson, (await ReadRawStreamAsync(repository)).Content);
		Assert.AreEqual(1, Inner.RawStreamCalls);
		Assert.AreEqual(1, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task Revalidate_RawStreamAsync_Changed_RefetchesAndCaches()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await WriteBlobAsync(ABJson);
		await ReadRawStreamAsync(repository);
		var version = await WriteBlobAsync("[]");

		var (_, result) = await ReadRawStreamAsync(repository);

		Assert.AreEqual(version, result.ETag);
		Assert.AreEqual(2, Inner.RawStreamCalls);

		Assert.AreEqual("[]", (await ReadRawStreamAsync(repository)).Content);
		Assert.AreEqual(2, Inner.RawStreamCalls);
	}

	// SlidingExpiration

	[TestMethod]
	public async Task Expiration_BeforeExpiry_ServesCachedItems()
	{
		var repository = CreateRepository(Expiring);
		await Source.StoreAsync([A], CT);
		await repository.ListAsync(CT);
		await Source.StoreAsync([B], CT);

		_time.Advance(Expiration - TimeSpan.FromTicks(1));

		Assert.AreSequenceEqual([A], await repository.ListAsync(CT));
		Assert.AreEqual(1, Inner.ListCalls);
	}

	[TestMethod]
	public async Task Expiration_AtExpiry_Refetches()
	{
		var repository = CreateRepository(Expiring);
		await Source.StoreAsync([A], CT);
		await repository.ListAsync(CT);
		await Source.StoreAsync([B], CT);

		_time.Advance(Expiration);

		Assert.AreSequenceEqual([B], await repository.ListAsync(CT));
		Assert.AreEqual(2, Inner.ListCalls);
	}

	[TestMethod]
	public async Task Expiration_AfterRefetch_CachesAgain()
	{
		var repository = CreateRepository(Expiring);
		await Source.StoreAsync([A], CT);
		await repository.ListAsync(CT);
		_time.Advance(Expiration);
		await repository.ListAsync(CT);

		_time.Advance(Expiration - TimeSpan.FromTicks(1));

		Assert.AreSequenceEqual([A], await repository.ListAsync(CT));
		Assert.AreEqual(2, Inner.ListCalls);
	}

	[TestMethod]
	public async Task Expiration_GetVersionAsync_AfterExpiry_CallsInner()
	{
		var repository = CreateRepository(Expiring);
		await Source.StoreAsync([A], CT);
		await repository.GetVersionAsync(CT);
		await Source.StoreAsync([B], CT);

		_time.Advance(Expiration);

		Assert.AreEqual(await Source.GetVersionAsync(CT), await repository.GetVersionAsync(CT));
		Assert.AreEqual(2, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task Expiration_RawStreamAsync_AfterExpiry_Refetches()
	{
		var repository = CreateRepository(Expiring);
		await WriteBlobAsync(ABJson);
		await ReadRawStreamAsync(repository);
		var version = await WriteBlobAsync("[]");

		_time.Advance(Expiration);

		var (_, result) = await ReadRawStreamAsync(repository);
		Assert.AreEqual(version, result.ETag);
		Assert.AreEqual(2, Inner.RawStreamCalls);
	}

	// Writes

	[TestMethod]
	public async Task ApplyAsync_DelegatesToInner()
	{
		var repository = CreateRepository();

		await repository.AddAsync(A, CT);

		Assert.AreEqual(1, Inner.ApplyCalls);
		Assert.AreSequenceEqual([A], await Source.ListAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_InvalidatesCache()
	{
		var repository = CreateRepository();
		await Source.StoreAsync([A], CT);
		await repository.ListAsync(CT);

		await repository.AddAsync(B, CT);

		Assert.AreSequenceEqual([A, B], await repository.ListAsync(CT));
		Assert.AreEqual(await Source.GetVersionAsync(CT), await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task Revalidate_ApplyAsync_ReadsOwnWrites()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.StoreAsync([A], CT);
		await repository.ListAsync(CT);

		await repository.AddAsync(B, CT);

		Assert.AreSequenceEqual([A, B], await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ClearAsync_ClearsInnerAndInvalidatesCache()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ABJson);
		await repository.ListAsync(CT);
		await ReadRawStreamAsync(repository);

		await repository.ClearAsync(CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
		Assert.IsEmpty(await repository.ListAsync(CT));
		Assert.IsNull(await repository.GetVersionAsync(CT));
		Assert.IsNull(await repository.RawStreamAsync(CT));
	}

	// Inner overrides

	[TestMethod]
	public async Task DeleteAsync_Value_UsesInnerComparer()
	{
		ICollectionRepository<Item> repository = new CachingCollectionRepository<Item>(CreateBlobRepository(new NameComparer()), CacheOptions.Immutable, _time);
		await Source.StoreAsync([A, B], CT);

		Assert.IsTrue(await repository.DeleteAsync(new Item("a", 99), CT));

		Assert.AreSequenceEqual([B], await Source.ListAsync(CT));
	}

	[TestMethod]
	public async Task AddAsync_UsesInnerImplementation()
	{
		var repository = new InMemoryAppendBlob("test", _time)
			.AsJsonLineCollectionRepository<Item>(JsonSerializerOptions.Web)
			.WithCaching(CacheOptions.AlwaysRevalidate);

		await repository.AddAsync(A, CT);
		await repository.AddRangeAsync([B, C], CT);

		Assert.AreSequenceEqual([A, B, C], await repository.ListAsync(CT));
	}


	private sealed class NameComparer : IEqualityComparer<Item>
	{
		public bool Equals(Item? x, Item? y) => x?.Name == y?.Name;

		public int GetHashCode(Item obj) => obj.Name.GetHashCode();
	}

	private sealed class SpyCollectionRepository<T>(ICollectionRepository<T> inner) : ICollectionRepository<T>
	{
		public int ApplyCalls { get; private set; }

		public int ListCalls { get; private set; }

		public int VersionCalls { get; private set; }

		public int RawStreamCalls { get; private set; }

		public int EnumerateCalls { get; private set; }

		public Task ApplyAsync(Func<IImmutableList<T>?, CancellationToken, ValueTask<IReadOnlyCollection<T>?>> update, CancellationToken cancellationToken)
		{
			ApplyCalls++;
			return inner.ApplyAsync(update, cancellationToken);
		}

		public Task<Versioned<IReadOnlyCollection<T>>> ListWithVersionAsync(CancellationToken cancellationToken)
		{
			ListCalls++;
			return inner.ListWithVersionAsync(cancellationToken);
		}

		public Task<string?> GetVersionAsync(CancellationToken cancellationToken)
		{
			VersionCalls++;
			return inner.GetVersionAsync(cancellationToken);
		}

		public Task<RawStreamResult?> RawStreamAsync(CancellationToken cancellationToken)
		{
			RawStreamCalls++;
			return inner.RawStreamAsync(cancellationToken);
		}

		public IAsyncEnumerable<T> AsAsyncEnumerableAsync(CancellationToken cancellationToken)
		{
			EnumerateCalls++;
			return inner.AsAsyncEnumerableAsync(cancellationToken);
		}

		public Task ClearAsync(CancellationToken cancellationToken) => inner.ClearAsync(cancellationToken);
	}
}
