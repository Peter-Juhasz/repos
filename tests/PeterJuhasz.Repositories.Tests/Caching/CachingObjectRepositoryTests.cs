using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.Caching;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Text;
using System.Text.Json;

namespace PeterJuhasz.Repositories.Tests.Caching;

[TestClass]
public class CachingObjectRepositoryTests(TestContext testContext)
{
	public sealed record class Item(string Name);

	private static readonly Item Value = new("value");
	private static readonly Item OtherValue = new("other");

	private const string ValueJson = """{"name":"value"}""";
	private const string OtherValueJson = """{"name":"other"}""";

	private static readonly TimeSpan Expiration = TimeSpan.FromMinutes(5);

	private static readonly CacheOptions Expiring = new(SlidingExpiration: Expiration);

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

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

	private IObjectRepository<Item> CreateRepository(CacheOptions? cacheOptions = null) =>
		new CachingObjectRepository<Item>(Inner, cacheOptions ?? CacheOptions.Immutable, _time);

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

	// Construction

	[TestMethod]
	public void Inner_ReturnsWrappedRepository()
	{
		var repository = new CachingObjectRepository<Item>(Source, CacheOptions.Immutable, _time);

		Assert.AreSame(Source, repository.Inner);
	}

	[TestMethod]
	public void WithCaching_WrapsRepository()
	{
		IObjectRepository<Item> source = new InMemoryObjectRepository<Item>();

		var repository = source.WithCaching(CacheOptions.Immutable);

		Assert.IsInstanceOfType<CachingObjectRepository<Item>>(repository);
		Assert.AreSame(source, ((CachingObjectRepository<Item>)repository).Inner);
	}

	[TestMethod]
	public void TryGetBlob_ReturnsUnderlyingBlob()
	{
		IObjectRepository<Item> repository = new CachingObjectRepository<Item>(Source, CacheOptions.Immutable, _time);

		Assert.IsTrue(repository.TryGetBlob(out var blob));
		Assert.AreSame(Blob, blob);
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

		var result = await repository.ApplyAsync(_ => Value, CT);

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

		await repository.ApplyAsync(_ => OtherValue, CT);

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

		var result = await repository.ApplyAsync(current => new Item(current!.Name + "!"), CT);

		Assert.AreEqual(new Item("b!"), result);
		Assert.AreEqual(new Item("b!"), await Source.GetAsync(CT));
		Assert.AreEqual(new Item("b!"), await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_UsesInnerComparer()
	{
		IObjectRepository<Item> repository = new CachingObjectRepository<Item>(CreateBlobRepository(new NameIgnoreCaseComparer()), CacheOptions.Immutable, _time);
		var created = await Source.CreateAsync(Value, CT);

		var result = await repository.ApplyAsync(_ => new Item("VALUE"), CT);

		Assert.AreEqual(Value, result);
		Assert.AreEqual(created.ETag, await Source.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_PreservesBlobMetadata()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ValueJson, new Dictionary<string, string> { ["a"] = "b" });

		await repository.ApplyAsync(_ => OtherValue, CT);

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

		await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => repository.ApplyAsync(c => new Item(c!.Name + "x"), CT), CT)));

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

		public ValueTask<Versioned<T>?> GetOrDefaultWithVersionAsync(CancellationToken cancellationToken)
		{
			GetCalls++;
			return inner.GetOrDefaultWithVersionAsync(cancellationToken);
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
