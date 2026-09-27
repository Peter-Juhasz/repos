using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.Caching;
using PeterJuhasz.Repositories.FileSystem;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Text;
using System.Text.Json;

namespace PeterJuhasz.Repositories.Tests.Caching;

[TestClass]
public class GlobalCachingBlobSingleObjectRepositoryTests(TestContext testContext)
{
	public sealed record class Item(string Name);

	private static readonly Item Value = new("value");
	private static readonly Item OtherValue = new("other");

	private const string ValueJson = """{"name":"value"}""";
	private const string OtherValueJson = """{"name":"other"}""";

	private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);

	private static readonly CacheOptions Expiring = new(SlidingExpiration: Expiration);

	/// <summary>
	/// Optimistic concurrency retries are unbounded, so an apply that keeps reading a stale version would spin forever; this makes it fail instead.
	/// </summary>
	private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(10);

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	/// <summary>
	/// The cache is static, so each test uses its own key to not observe entries of other tests.
	/// </summary>
	private readonly string _cacheKey = Guid.NewGuid().ToString();

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlob Blob => field ??= new("test", _time);

	/// <summary>
	/// The uncached repository, used to arrange and inspect the underlying data without going through the cache.
	/// </summary>
	private IObjectRepository<Item> Source => field ??= CreateBlobRepository();

	/// <summary>
	/// The repository wrapped by the cache, counting the calls that reach it.
	/// </summary>
	private SpyObjectRepository<Item> Inner => field ??= new(Source);

	private IObjectRepository<Item> CreateBlobRepository(IEqualityComparer<Item>? comparer = null) =>
		new BlobObjectRepository<Item>(Blob, new JsonSerializerOptionsJsonSerializer<Item>(JsonSerializerOptions.Web), comparer);

	private IObjectRepository<Item> CreateRepository(CacheOptions? cacheOptions = null, IObjectRepository<Item>? inner = null, string? cacheKey = null) =>
		new GlobalCachingBlobSingleObjectRepository<Item>(inner ?? Inner, cacheKey ?? _cacheKey, cacheOptions ?? CacheOptions.Immutable, _time);

	private Task<string> WriteBlobAsync(string content, IReadOnlyDictionary<string, string>? metadata = null) =>
		Blob.WriteAsync(Encoding.UTF8.GetBytes(content), IBlob.AnyOrNoneConcurrencyToken, new(ContentEncoding: "identity", MediaType: "application/json", Metadata: metadata), CT);

	private async Task<Versioned<Item>> GetWithVersionAsync(IObjectRepository<Item> repository)
	{
		var result = await repository.GetOrDefaultWithVersionAsync(CT);
		Assert.IsNotNull(result);
		return result.Value;
	}

	private async Task<(string Content, RawStreamResult Result)> ReadRawStreamAsync(IObjectRepository<Item> repository)
	{
		var result = await repository.RawStreamAsync(CT);
		Assert.IsNotNull(result);
		await using var raw = result.Value;
		using var reader = new StreamReader(raw.Stream);
		return (await reader.ReadToEndAsync(CT), raw);
	}

	private async Task<Item?> ApplyAsync(IObjectRepository<Item> repository, Func<Item?, Item?> factory)
	{
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(CT);
		timeout.CancelAfter(ApplyTimeout);
		return await Task.Run(() => repository.ApplyAsync(factory, timeout.Token), timeout.Token);
	}

	// Construction

	[TestMethod]
	public void Inner_ReturnsWrappedRepository()
	{
		var repository = new GlobalCachingBlobSingleObjectRepository<Item>(Source, _cacheKey, CacheOptions.Immutable, _time);

		Assert.AreSame(Source, repository.Inner);
	}

	[TestMethod]
	public void WithCaching_BlobRepository_UsesGlobalCache()
	{
		var source = CreateBlobRepository();

		var repository = source.WithCaching(CacheOptions.Immutable);

		Assert.IsInstanceOfType<GlobalCachingBlobSingleObjectRepository<Item>>(repository);
		Assert.AreSame(source, ((GlobalCachingBlobSingleObjectRepository<Item>)repository).Inner);
	}

	[TestMethod]
	public void TryGetBlob_ReturnsUnderlyingBlob()
	{
		IObjectRepository<Item> repository = new GlobalCachingBlobSingleObjectRepository<Item>(Source, _cacheKey, CacheOptions.Immutable, _time);

		Assert.IsTrue(repository.TryGetBlob(out var blob));
		Assert.AreSame(Blob, blob);
	}

	// Shared cache

	[TestMethod]
	public async Task SharedCache_SameKey_ServesValueCachedByOtherInstance()
	{
		var first = CreateRepository();
		var otherInner = new SpyObjectRepository<Item>(Source);
		var second = CreateRepository(inner: otherInner);
		await Source.CreateAsync(Value, CT);
		var cached = await first.GetAsync(CT);

		Assert.AreSame(cached, await second.GetAsync(CT));
		Assert.AreEqual(0, otherInner.GetCalls);
	}

	[TestMethod]
	public async Task SharedCache_DifferentKey_IsNotShared()
	{
		var first = CreateRepository();
		var otherInner = new SpyObjectRepository<Item>(Source);
		var second = CreateRepository(inner: otherInner, cacheKey: Guid.NewGuid().ToString());
		await Source.CreateAsync(Value, CT);
		await first.GetAsync(CT);

		await second.GetAsync(CT);

		Assert.AreEqual(1, otherInner.GetCalls);
	}

	[TestMethod]
	public async Task SharedCache_WriteThroughOtherInstance_InvalidatesCache()
	{
		var first = CreateRepository();
		var second = CreateRepository(inner: new SpyObjectRepository<Item>(Source));
		var created = await Source.CreateAsync(Value, CT);
		await first.GetAsync(CT);
		await ReadRawStreamAsync(first);

		await second.UpdateAsync(OtherValue, created.ETag, CT);

		Assert.AreEqual(OtherValue, await first.GetAsync(CT));
		Assert.AreEqual(OtherValueJson, (await ReadRawStreamAsync(first)).Content);
	}

	[TestMethod]
	[Ignore("Known issue: the expiration of a shared entry is set by the instance that cached it.")]
	public async Task SharedCache_EntryCachedByImmutableInstance_ExpiresForExpiringInstance()
	{
		var immutable = CreateRepository(CacheOptions.Immutable);
		var expiring = CreateRepository(Expiring, new SpyObjectRepository<Item>(Source));
		await Source.CreateAsync(Value, CT);
		await immutable.GetAsync(CT);
		await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		_time.Advance(Expiration);

		Assert.AreEqual(OtherValue, await expiring.GetAsync(CT));
	}

	[TestMethod]
	[Ignore("Known issue: the cache key is the blob name, which is only the file name for files.")]
	public async Task WithCaching_FilesWithSameNameInDifferentDirectories_AreNotShared()
	{
		using var directory = new TemporaryDirectory();
		var name = $"{Guid.NewGuid():N}.json";
		var first = new FileInfoBlob(directory.GetFile(Path.Combine("a", name))).AsJsonObjectRepository<Item>(JsonSerializerOptions.Web).WithCaching(CacheOptions.Immutable);
		var second = new FileInfoBlob(directory.GetFile(Path.Combine("b", name))).AsJsonObjectRepository<Item>(JsonSerializerOptions.Web).WithCaching(CacheOptions.Immutable);
		await first.CreateAsync(Value, CT);
		await second.CreateAsync(OtherValue, CT);

		Assert.AreEqual(Value, await first.GetAsync(CT));
		Assert.AreEqual(OtherValue, await second.GetAsync(CT));
	}

	// Not exists

	[TestMethod]
	public async Task NotExists_ReturnsNull()
	{
		var repository = CreateRepository();

		Assert.IsNull(await repository.GetOrDefaultWithVersionAsync(CT));
		Assert.IsNull(await repository.GetOrDefaultAsync(CT));
		Assert.IsNull(await repository.GetVersionAsync(CT));
		Assert.IsFalse(await repository.ExistsAsync(CT));
		Assert.IsNull(await repository.RawStreamAsync(CT));
		await Assert.ThrowsExactlyAsync<NotFoundException>(async () => await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_NotExists_IsNotCached()
	{
		var repository = CreateRepository();
		Assert.IsNull(await repository.GetOrDefaultWithVersionAsync(CT));

		await Source.CreateAsync(Value, CT);

		Assert.AreEqual(Value, await repository.GetOrDefaultAsync(CT));
		Assert.AreEqual(2, Inner.GetCalls);
	}

	[TestMethod]
	public async Task GetVersionAsync_NotExists_IsNotCached()
	{
		var repository = CreateRepository();
		Assert.IsNull(await repository.GetVersionAsync(CT));

		var created = await Source.CreateAsync(Value, CT);

		Assert.AreEqual(created.ETag, await repository.GetVersionAsync(CT));
		Assert.AreEqual(2, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task RawStreamAsync_NotExists_IsNotCached()
	{
		var repository = CreateRepository();
		Assert.IsNull(await repository.RawStreamAsync(CT));

		await WriteBlobAsync(ValueJson);

		Assert.AreEqual(ValueJson, (await ReadRawStreamAsync(repository)).Content);
		Assert.AreEqual(2, Inner.RawStreamCalls);
	}

	// Immutable

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_Miss_ReadsInner()
	{
		var repository = CreateRepository();
		var created = await Source.CreateAsync(Value, CT);

		var result = await GetWithVersionAsync(repository);

		Assert.AreEqual(Value, result.Value);
		Assert.AreEqual(created.ETag, result.ETag);
		Assert.AreEqual(1, Inner.GetCalls);
	}

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_Hit_DoesNotCallInner()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		var first = await GetWithVersionAsync(repository);

		var second = await GetWithVersionAsync(repository);

		Assert.AreSame(first.Value, second.Value);
		Assert.AreEqual(first.ETag, second.ETag);
		Assert.AreEqual(1, Inner.GetCalls);
		Assert.AreEqual(0, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_Hit_IgnoresExternalChanges()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);

		await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		Assert.AreEqual(Value, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_AfterGetVersion_ReadsInnerOnce()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		await repository.GetVersionAsync(CT);

		await repository.GetAsync(CT);

		Assert.AreEqual(Value, await repository.GetAsync(CT));
		Assert.AreEqual(1, Inner.GetCalls);
	}

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_SameVersion_KeepsCachedRawStream()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ValueJson);
		await ReadRawStreamAsync(repository);

		await repository.GetAsync(CT);

		Assert.AreEqual(ValueJson, (await ReadRawStreamAsync(repository)).Content);
		Assert.AreEqual(1, Inner.RawStreamCalls);
	}

	[TestMethod]
	[Ignore("Known issue: a read that started before a write caches its result after the write invalidated the cache.")]
	public async Task GetOrDefaultWithVersionAsync_WriteDuringMiss_DoesNotCacheStaleValue()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		Inner.ReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var read = repository.GetOrDefaultWithVersionAsync(CT).AsTask();
		await Inner.ReadReached.Task.WaitAsync(CT);

		await CreateRepository(inner: new SpyObjectRepository<Item>(Source)).StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);
		Inner.ReadGate.SetResult();
		await read;

		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task GetVersionAsync_Hit_DoesNotCallInner()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		var version = await repository.GetVersionAsync(CT);

		Assert.AreEqual(version, await repository.GetVersionAsync(CT));
		Assert.AreEqual(1, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task GetVersionAsync_AfterGet_ReturnsCachedVersion()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		var result = await GetWithVersionAsync(repository);

		Assert.AreEqual(result.ETag, await repository.GetVersionAsync(CT));
		Assert.AreEqual(0, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task GetVersionAsync_AfterRawStream_ReturnsCachedVersion()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ValueJson);
		var (_, result) = await ReadRawStreamAsync(repository);

		Assert.AreEqual(result.ETag, await repository.GetVersionAsync(CT));
		Assert.AreEqual(0, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task ExistsAsync_Miss_DoesNotReadValue()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);

		Assert.IsTrue(await repository.ExistsAsync(CT));
		Assert.AreEqual(1, Inner.VersionCalls);
		Assert.AreEqual(0, Inner.GetCalls);
	}

	[TestMethod]
	public async Task ExistsAsync_Hit_DoesNotCallInner()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);

		Assert.IsTrue(await repository.ExistsAsync(CT));
		Assert.AreEqual(1, Inner.GetCalls);
		Assert.AreEqual(0, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task RawStreamAsync_Miss_ReturnsContentAndInfo()
	{
		var repository = CreateRepository();
		var version = await WriteBlobAsync(ValueJson);

		var (content, result) = await ReadRawStreamAsync(repository);

		Assert.AreEqual(version, result.ETag);
		Assert.AreEqual(_time.GetUtcNow(), result.LastModified);
		Assert.AreEqual("identity", result.Encoding);
		Assert.AreEqual(ValueJson, content);
	}

	[TestMethod]
	public async Task RawStreamAsync_Hit_ReturnsCachedContentAndInfo()
	{
		var repository = CreateRepository();
		var version = await WriteBlobAsync(ValueJson);
		var lastModified = _time.GetUtcNow();
		await ReadRawStreamAsync(repository);
		_time.Advance(TimeSpan.FromMinutes(1));
		await WriteBlobAsync(OtherValueJson);

		var (content, result) = await ReadRawStreamAsync(repository);

		Assert.AreEqual(ValueJson, content);
		Assert.AreEqual(version, result.ETag);
		Assert.AreEqual(lastModified, result.LastModified);
		Assert.AreEqual("identity", result.Encoding);
		Assert.AreEqual(1, Inner.RawStreamCalls);
	}

	[TestMethod]
	public async Task RawStreamAsync_Hit_ReturnsNewStreamEachTime()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ValueJson);
		await ReadRawStreamAsync(repository);

		Assert.AreEqual(ValueJson, (await ReadRawStreamAsync(repository)).Content);
		Assert.AreEqual(ValueJson, (await ReadRawStreamAsync(repository)).Content);
	}

	[TestMethod]
	public async Task RawStreamAsync_SameVersion_KeepsCachedValue()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		var cached = await repository.GetAsync(CT);

		await ReadRawStreamAsync(repository);

		Assert.AreSame(cached, await repository.GetAsync(CT));
		Assert.AreEqual(1, Inner.GetCalls);
	}

	[TestMethod]
	public async Task GetVersionAsync_AfterRawStream_KeepsCachedRawStream()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ValueJson);
		await ReadRawStreamAsync(repository);

		await repository.GetVersionAsync(CT);

		Assert.AreEqual(ValueJson, (await ReadRawStreamAsync(repository)).Content);
		Assert.AreEqual(1, Inner.RawStreamCalls);
	}

	// MustRevalidate

	[TestMethod]
	public async Task Revalidate_GetOrDefaultWithVersionAsync_Unchanged_ServesCachedValue()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.CreateAsync(Value, CT);
		var first = await GetWithVersionAsync(repository);

		var second = await GetWithVersionAsync(repository);

		Assert.AreSame(first.Value, second.Value);
		Assert.AreEqual(1, Inner.GetCalls);
		Assert.AreEqual(1, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task Revalidate_GetOrDefaultWithVersionAsync_Changed_RefetchesAndCaches()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);
		var updated = await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		var result = await GetWithVersionAsync(repository);

		Assert.AreEqual(OtherValue, result.Value);
		Assert.AreEqual(updated.ETag, result.ETag);
		Assert.AreEqual(2, Inner.GetCalls);

		await repository.GetAsync(CT);
		Assert.AreEqual(2, Inner.GetCalls);
	}

	[TestMethod]
	public async Task Revalidate_GetOrDefaultWithVersionAsync_Deleted_ReturnsNull()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);

		await Source.DeleteAsync(CT);

		Assert.IsNull(await repository.GetOrDefaultWithVersionAsync(CT));
		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task Revalidate_GetVersionAsync_AlwaysCallsInner()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.CreateAsync(Value, CT);
		await repository.GetVersionAsync(CT);

		var updated = await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		Assert.AreEqual(updated.ETag, await repository.GetVersionAsync(CT));
		Assert.AreEqual(2, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task Revalidate_GetVersionAsync_Unchanged_KeepsCachedValue()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);

		await repository.GetVersionAsync(CT);

		Assert.AreEqual(Value, await repository.GetAsync(CT));
		Assert.AreEqual(1, Inner.GetCalls);
	}

	[TestMethod]
	public async Task Revalidate_GetVersionAsync_Changed_DropsCachedValue()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);
		await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		await repository.GetVersionAsync(CT);

		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
		Assert.AreEqual(2, Inner.GetCalls);
	}

	[TestMethod]
	public async Task Revalidate_RawStreamAsync_Unchanged_ServesCachedContent()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await WriteBlobAsync(ValueJson);
		await ReadRawStreamAsync(repository);

		Assert.AreEqual(ValueJson, (await ReadRawStreamAsync(repository)).Content);
		Assert.AreEqual(1, Inner.RawStreamCalls);
		Assert.AreEqual(1, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task Revalidate_RawStreamAsync_Changed_RefetchesAndCaches()
	{
		var repository = CreateRepository(CacheOptions.AlwaysRevalidate);
		await WriteBlobAsync(ValueJson);
		await ReadRawStreamAsync(repository);
		var version = await WriteBlobAsync(OtherValueJson);

		var (content, result) = await ReadRawStreamAsync(repository);

		Assert.AreEqual(OtherValueJson, content);
		Assert.AreEqual(version, result.ETag);
		Assert.AreEqual(2, Inner.RawStreamCalls);

		Assert.AreEqual(OtherValueJson, (await ReadRawStreamAsync(repository)).Content);
		Assert.AreEqual(2, Inner.RawStreamCalls);
	}

	// SlidingExpiration

	[TestMethod]
	public async Task Expiration_BeforeExpiry_ServesCachedValue()
	{
		var repository = CreateRepository(Expiring);
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);
		await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		_time.Advance(Expiration - TimeSpan.FromTicks(1));

		Assert.AreEqual(Value, await repository.GetAsync(CT));
		Assert.AreEqual(1, Inner.GetCalls);
	}

	[TestMethod]
	public async Task Expiration_AtExpiry_Refetches()
	{
		var repository = CreateRepository(Expiring);
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);
		await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		_time.Advance(Expiration);

		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
		Assert.AreEqual(2, Inner.GetCalls);
	}

	[TestMethod]
	[Ignore("Known issue: an entry refetched exactly at its expiration keeps the old expiration.")]
	public async Task Expiration_AfterRefetch_CachesAgain()
	{
		var repository = CreateRepository(Expiring);
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);
		_time.Advance(Expiration);
		await repository.GetAsync(CT);

		_time.Advance(Expiration - TimeSpan.FromTicks(1));

		Assert.AreEqual(Value, await repository.GetAsync(CT));
		Assert.AreEqual(2, Inner.GetCalls);
	}

	[TestMethod]
	public async Task Expiration_GetVersionAsync_BeforeExpiry_ServesCachedVersion()
	{
		var repository = CreateRepository(Expiring);
		var created = await Source.CreateAsync(Value, CT);
		await repository.GetVersionAsync(CT);
		await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		_time.Advance(Expiration - TimeSpan.FromTicks(1));

		Assert.AreEqual(created.ETag, await repository.GetVersionAsync(CT));
		Assert.AreEqual(1, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task Expiration_GetVersionAsync_AfterExpiry_CallsInner()
	{
		var repository = CreateRepository(Expiring);
		await Source.CreateAsync(Value, CT);
		await repository.GetVersionAsync(CT);
		var updated = await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		_time.Advance(Expiration);

		Assert.AreEqual(updated.ETag, await repository.GetVersionAsync(CT));
		Assert.AreEqual(2, Inner.VersionCalls);
	}

	[TestMethod]
	public async Task Expiration_RawStreamAsync_AfterExpiry_Refetches()
	{
		var repository = CreateRepository(Expiring);
		await WriteBlobAsync(ValueJson);
		await ReadRawStreamAsync(repository);
		var version = await WriteBlobAsync(OtherValueJson);

		_time.Advance(Expiration);

		var (_, result) = await ReadRawStreamAsync(repository);
		Assert.AreEqual(version, result.ETag);
		Assert.AreEqual(2, Inner.RawStreamCalls);
	}

	// Writes

	[TestMethod]
	public async Task StoreAsync_DelegatesToInnerAndInvalidatesCache()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);
		await repository.GetAsync(CT);
		await ReadRawStreamAsync(repository);

		var updated = await repository.UpdateAsync(OtherValue, created.ETag, CT);

		Assert.AreEqual(OtherValue, await Source.GetAsync(CT));
		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
		Assert.AreEqual(updated.ETag, await repository.GetVersionAsync(CT));
		Assert.AreEqual(OtherValueJson, (await ReadRawStreamAsync(repository)).Content);
	}

	[TestMethod]
	public async Task StoreAsync_Conflict_InvalidatesCache()
	{
		var repository = CreateRepository();
		var created = await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);
		await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.UpdateAsync(new("new"), created.ETag, CT));

		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task DeleteWithVersionAsync_InvalidatesCache()
	{
		var repository = CreateRepository();
		var created = await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);
		await ReadRawStreamAsync(repository);

		await repository.DeleteWithVersionAsync(created.ETag, CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
		Assert.IsNull(await repository.GetOrDefaultAsync(CT));
		Assert.IsNull(await repository.GetVersionAsync(CT));
		Assert.IsNull(await repository.RawStreamAsync(CT));
	}

	[TestMethod]
	public async Task DeleteWithVersionAsync_Conflict_InvalidatesCache()
	{
		var repository = CreateRepository();
		var created = await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);
		await Source.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.DeleteWithVersionAsync(created.ETag, CT));

		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_NotFound_InvalidatesCache()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);
		await Source.DeleteAsync(CT);

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => repository.DeleteAsync(CT));

		Assert.IsNull(await repository.GetOrDefaultAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_InvalidatesCache()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);

		await repository.DeleteAsync(CT);

		Assert.IsFalse(await repository.ExistsAsync(CT));
		Assert.IsNull(await repository.GetOrDefaultAsync(CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_InvalidatesCache()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);

		Assert.IsTrue(await repository.DeleteIfExistsAsync(CT));

		Assert.IsNull(await repository.GetOrDefaultAsync(CT));
		Assert.IsFalse(await repository.DeleteIfExistsAsync(CT));
	}

	// ApplyAsync

	[TestMethod]
	public async Task ApplyAsync_DelegatesToInner()
	{
		var repository = CreateRepository();

		var result = await ApplyAsync(repository, _ => Value);

		Assert.AreEqual(Value, result);
		Assert.AreEqual(1, Inner.ApplyCalls);
		Assert.AreEqual(Value, await Source.GetAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_InvalidatesCache()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(Value, CT);
		await repository.GetAsync(CT);

		await ApplyAsync(repository, _ => OtherValue);

		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
		Assert.AreEqual(await Source.GetVersionAsync(CT), await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_StaleCache_AppliesToCurrentValue()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(new("a"), CT);
		await repository.GetAsync(CT);
		await Source.StoreAsync(new("b"), IBlob.AnyConcurrencyToken, CT);

		var result = await ApplyAsync(repository, current => new Item(current!.Name + "!"));

		Assert.AreEqual(new Item("b!"), result);
		Assert.AreEqual(new Item("b!"), await Source.GetAsync(CT));
		Assert.AreEqual(new Item("b!"), await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_UsesInnerComparer()
	{
		var repository = CreateRepository(inner: CreateBlobRepository(new NameIgnoreCaseComparer()));
		var created = await Source.CreateAsync(Value, CT);

		var result = await ApplyAsync(repository, _ => new Item("VALUE"));

		Assert.AreEqual(Value, result);
		Assert.AreEqual(created.ETag, await Source.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_PreservesBlobMetadata()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ValueJson, new Dictionary<string, string> { ["a"] = "b" });

		await ApplyAsync(repository, _ => OtherValue);

		var info = await Blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.IsNotNull(info.Metadata);
		Assert.AreEqual("b", info.Metadata["a"]);
		Assert.AreEqual(OtherValue, await Source.GetAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ConcurrentAppliers_AllApplied()
	{
		var repository = CreateRepository();
		await Source.CreateAsync(new(""), CT);
		await repository.GetAsync(CT);

		await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => ApplyAsync(repository, c => new Item(c!.Name + "x"))));

		Assert.AreEqual(new Item(new string('x', 32)), await repository.GetAsync(CT));
	}


	private sealed class NameIgnoreCaseComparer : IEqualityComparer<Item>
	{
		public bool Equals(Item? x, Item? y) => StringComparer.OrdinalIgnoreCase.Equals(x?.Name, y?.Name);

		public int GetHashCode(Item obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
	}

	private sealed class SpyObjectRepository<T>(IObjectRepository<T> inner) : IObjectRepository<T>
	{
		public int GetCalls { get; private set; }

		public int VersionCalls { get; private set; }

		public int RawStreamCalls { get; private set; }

		public int ApplyCalls { get; private set; }

		/// <summary>
		/// When set, reads of the value wait for it after the inner repository returned, so a write can happen in between.
		/// </summary>
		public TaskCompletionSource? ReadGate { get; set; }

		public TaskCompletionSource ReadReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public async ValueTask<Versioned<T>?> GetOrDefaultWithVersionAsync(CancellationToken cancellationToken)
		{
			GetCalls++;
			var result = await inner.GetOrDefaultWithVersionAsync(cancellationToken);
			if (ReadGate is { } gate)
			{
				ReadReached.TrySetResult();
				await gate.Task.WaitAsync(cancellationToken);
			}
			return result;
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

		// forwarded, so the inner implementation is used instead of the default one (e.g. comparer, metadata)
		public Task<T?> ApplyAsync(Func<T?, CancellationToken, ValueTask<T?>> factory, CancellationToken cancellationToken)
		{
			ApplyCalls++;
			return inner.ApplyAsync(factory, cancellationToken);
		}

		public Task<Versioned<T>> StoreAsync(T value, string? concurrencyToken, CancellationToken cancellationToken) =>
			inner.StoreAsync(value, concurrencyToken, cancellationToken);

		public Task DeleteWithVersionAsync(string concurrencyToken, CancellationToken cancellationToken) =>
			inner.DeleteWithVersionAsync(concurrencyToken, cancellationToken);
	}
}
