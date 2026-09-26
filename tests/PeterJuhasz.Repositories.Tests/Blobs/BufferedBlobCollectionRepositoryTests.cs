using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Text;
using System.Text.Json;

namespace PeterJuhasz.Repositories.Tests.Blobs;

[TestClass]
public class BufferedBlobCollectionRepositoryTests(TestContext testContext)
{
	public sealed record class Item(string Name, int Count);

	private static readonly Item A = new("a", 1);
	private static readonly Item B = new("b", 2);
	private static readonly Item C = new("c", 3);

	private const string ABJson = """[{"name":"a","count":1},{"name":"b","count":2}]""";

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlob Blob => field ??= new("test", _time);

	private ICollectionRepository<Item> CreateRepository(IEqualityComparer<Item>? comparer = null) =>
		new BufferedBlobCollectionRepository<Item>(Blob, new JsonSerializerOptionsJsonCollectionSerializer<Item>(JsonSerializerOptions.Web), comparer);

	private Task<string> WriteBlobAsync(string content, IBlob.WriteBlobInfo? options = null) =>
		Blob.WriteAsync(Encoding.UTF8.GetBytes(content), null, options ?? new(MediaType: "application/json"), CT);

	private async Task<string> ReadBlobAsync()
	{
		var result = await Blob.ReadAsync(CT);
		Assert.IsNotNull(result);
		return result.Value.ToString();
	}

	private async Task<string?> GetBlobTokenAsync() => (await Blob.GetInfoAsync(CT))?.ConcurrencyToken;

	// Blob

	[TestMethod]
	public void Blob_ReturnsUnderlyingBlob()
	{
		var repository = new BufferedBlobCollectionRepository<Item>(Blob, new JsonSerializerOptionsJsonCollectionSerializer<Item>(JsonSerializerOptions.Web));

		Assert.AreSame(Blob, repository.Blob);
	}

	[TestMethod]
	public void TryGetBlob_ReturnsUnderlyingBlob()
	{
		var repository = CreateRepository();

		Assert.IsTrue(repository.TryGetBlob(out var blob));
		Assert.AreSame(Blob, blob);
	}

	[TestMethod]
	public void AsJsonCollectionRepository_CreatesBufferedRepository()
	{
		var repository = Blob.AsJsonCollectionRepository<Item>(JsonSerializerOptions.Web);

		Assert.IsInstanceOfType<BufferedBlobCollectionRepository<Item>>(repository);
	}

	// Empty blob

	[TestMethod]
	public async Task EmptyBlob_IsEmpty()
	{
		var repository = CreateRepository();

		var listed = await repository.ListWithVersionAsync(CT);
		Assert.IsEmpty(listed.Value);
		Assert.IsNull(listed.ETag);
		Assert.IsNull(await repository.GetVersionAsync(CT));
		Assert.IsFalse(await repository.AnyAsync(CT));
		Assert.AreEqual(0, await repository.CountAsync(CT));
		Assert.IsEmpty(await repository.AsAsyncEnumerableAsync(CT).ToListAsync(CT));
		Assert.IsNull(await repository.GetOrDefaultAsync(_ => true, CT));
		Assert.IsNull(await repository.RawStreamAsync(CT));
	}

	// Reading

	[TestMethod]
	public async Task ListWithVersionAsync_DeserializesBlobContent()
	{
		var repository = CreateRepository();
		var token = await WriteBlobAsync(ABJson);

		var result = await repository.ListWithVersionAsync(CT);

		Assert.AreSequenceEqual(new[] { A, B }, result.Value);
		Assert.AreEqual(token, result.ETag);
		Assert.AreEqual(token, await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task ListWithVersionAsync_EmptyArray_ReturnsEmptyWithVersion()
	{
		var repository = CreateRepository();
		var token = await WriteBlobAsync("[]");

		var result = await repository.ListWithVersionAsync(CT);

		Assert.IsEmpty(result.Value);
		Assert.AreEqual(token, result.ETag);
	}

	[TestMethod]
	public async Task ListWithVersionAsync_Utf8Bom_Deserializes()
	{
		var repository = CreateRepository();
		await WriteBlobAsync("﻿" + ABJson);

		Assert.AreSequenceEqual(new[] { A, B }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ListWithVersionAsync_InvalidJson_Throws()
	{
		var repository = CreateRepository();
		await WriteBlobAsync("not json");

		await Assert.ThrowsAsync<JsonException>(() => repository.ListWithVersionAsync(CT));
	}

	[TestMethod]
	public async Task AsAsyncEnumerableAsync_YieldsItems()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ABJson);

		Assert.AreSequenceEqual(new[] { A, B }, await repository.AsAsyncEnumerableAsync(CT).ToListAsync(CT));
	}

	[TestMethod]
	public async Task QueryMethods_ReturnMatchingItems()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ABJson);

		Assert.IsTrue(await repository.AnyAsync(CT));
		Assert.IsTrue(await repository.AnyAsync(i => i.Name == "b", CT));
		Assert.IsFalse(await repository.AnyAsync(i => i.Name == "c", CT));
		Assert.AreEqual(2, await repository.CountAsync(CT));
		Assert.AreEqual(1, await repository.CountAsync(i => i.Count > 1, CT));
		Assert.AreEqual(B, await repository.GetOrDefaultAsync(i => i.Name == "b", CT));
		Assert.IsNull(await repository.GetOrDefaultAsync(i => i.Name == "c", CT));
	}

	// RawStreamAsync

	[TestMethod]
	public async Task RawStreamAsync_ReturnsBlobContentAndInfo()
	{
		var repository = CreateRepository();
		var token = await WriteBlobAsync(ABJson, new(ContentEncoding: "identity", MediaType: "application/json"));

		var result = await repository.RawStreamAsync(CT);

		Assert.IsNotNull(result);
		await using var raw = result.Value;
		Assert.AreEqual(token, raw.ETag);
		Assert.AreEqual(_time.GetUtcNow(), raw.LastModified);
		Assert.AreEqual("identity", raw.Encoding);
		using var reader = new StreamReader(raw.Stream);
		Assert.AreEqual(ABJson, await reader.ReadToEndAsync(CT));
	}

	// AddAsync / AddRangeAsync

	[TestMethod]
	public async Task AddAsync_WhenNotExists_CreatesBlobWithMediaType()
	{
		var repository = CreateRepository();

		Assert.IsTrue(await repository.AddAsync(A, CT));

		Assert.AreEqual("""[{"name":"a","count":1}]""", await ReadBlobAsync());
		Assert.AreEqual("application/json", (await Blob.GetInfoAsync(CT))?.MediaType);
	}

	[TestMethod]
	public async Task AddAsync_WhenExists_Appends()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);

		Assert.IsTrue(await repository.AddAsync(B, CT));

		Assert.AreEqual(ABJson, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task AddAsync_AllowsDuplicates()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);

		await repository.AddAsync(A, CT);

		Assert.AreSequenceEqual(new[] { A, A }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task AddRangeAsync_WhenNotExists_CreatesBlob()
	{
		var repository = CreateRepository();

		await repository.AddRangeAsync([A, B], CT);

		Assert.AreEqual(ABJson, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task AddRangeAsync_WhenExists_Appends()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);

		await repository.AddRangeAsync([B, C], CT);

		Assert.AreSequenceEqual(new[] { A, B, C }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task AddRangeAsync_Empty_DoesNotWrite()
	{
		var repository = CreateRepository();

		await repository.AddRangeAsync([], CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	// StoreAsync

	[TestMethod]
	public async Task StoreAsync_WhenNotExists_CreatesBlob()
	{
		var repository = CreateRepository();

		await repository.StoreAsync([A, B], CT);

		Assert.AreEqual(ABJson, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task StoreAsync_WhenExists_Replaces()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);

		await repository.StoreAsync([C], CT);

		Assert.AreSequenceEqual(new[] { C }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task StoreAsync_Empty_WritesEmptyArray()
	{
		var repository = CreateRepository();

		await repository.StoreAsync([], CT);

		Assert.AreEqual("[]", await ReadBlobAsync());
	}

	// UpdateAsync / AddOrUpdateAsync

	[TestMethod]
	public async Task UpdateAsync_WhenMatches_UpdatesAndReturnsTrue()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B], CT);

		Assert.IsTrue(await repository.UpdateAsync(i => i.Name == "b", i => i with { Count = 20 }, CT));

		Assert.AreSequenceEqual(new[] { A, B with { Count = 20 } }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task UpdateAsync_WhenNoMatch_ReturnsFalseAndDoesNotWrite()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B], CT);
		var token = await GetBlobTokenAsync();

		Assert.IsFalse(await repository.UpdateAsync(i => i.Name == "c", i => i with { Count = 30 }, CT));

		Assert.AreEqual(token, await GetBlobTokenAsync());
	}

	[TestMethod]
	public async Task UpdateAsync_ReturningEqualItem_ReturnsTrueAndDoesNotWrite()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B], CT);
		var token = await GetBlobTokenAsync();

		Assert.IsTrue(await repository.UpdateAsync(i => i.Name == "a", i => i with { }, CT));

		Assert.AreEqual(token, await GetBlobTokenAsync());
	}

	[TestMethod]
	public async Task UpdateAsync_WhenNotExists_ReturnsFalse()
	{
		var repository = CreateRepository();

		Assert.IsFalse(await repository.UpdateAsync(_ => true, i => i, CT));

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenMatches_Updates()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B], CT);

		await repository.AddOrUpdateAsync(A, i => i with { Count = 10 }, CT);

		Assert.AreSequenceEqual(new[] { A with { Count = 10 }, B }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenNotExists_Adds()
	{
		var repository = CreateRepository();

		await repository.AddOrUpdateAsync(A, i => i with { Count = 10 }, CT);

		Assert.AreSequenceEqual(new[] { A }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenNoMatch_Appends()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B], CT);

		await repository.AddOrUpdateAsync(C, i => i with { Count = 30 }, CT);

		Assert.AreSequenceEqual(new[] { A, B, C }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_ReturningEqualItem_DoesNotWrite()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B], CT);
		var token = await GetBlobTokenAsync();

		await repository.AddOrUpdateAsync(A, i => i with { }, CT);

		Assert.AreEqual(token, await GetBlobTokenAsync());
	}

	// DeleteAsync

	[TestMethod]
	public async Task DeleteAsync_Value_RemovesAllMatchingAndReturnsTrue()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B, A], CT);

		Assert.IsTrue(await repository.DeleteAsync(A, CT));

		Assert.AreSequenceEqual(new[] { B }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_Value_WhenNoMatch_ReturnsFalseAndDoesNotWrite()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B], CT);
		var token = await GetBlobTokenAsync();

		Assert.IsFalse(await repository.DeleteAsync(C, CT));

		Assert.AreEqual(token, await GetBlobTokenAsync());
	}

	[TestMethod]
	public async Task DeleteAsync_Value_UsesCustomComparer()
	{
		var repository = CreateRepository(new NameComparer());
		await repository.StoreAsync([A, B], CT);

		Assert.IsTrue(await repository.DeleteAsync(new Item("a", 99), CT));

		Assert.AreSequenceEqual(new[] { B }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_Predicate_RemovesMatching()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B, C], CT);

		Assert.IsTrue(await repository.DeleteAsync(i => i.Count >= 2, CT));

		Assert.AreSequenceEqual(new[] { A }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_LastItem_DeletesBlob()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);

		Assert.IsTrue(await repository.DeleteAsync(A, CT));

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_ReturnsFalse()
	{
		var repository = CreateRepository();

		Assert.IsFalse(await repository.DeleteAsync(A, CT));

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	// ClearAsync

	[TestMethod]
	public async Task ClearAsync_WhenExists_DeletesBlob()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B], CT);

		await repository.ClearAsync(CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task ClearAsync_WhenNotExists_DoesNothing()
	{
		var repository = CreateRepository();

		await repository.ClearAsync(CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	// ApplyAsync

	[TestMethod]
	public async Task ApplyAsync_WhenNotExists_PassesNull()
	{
		var repository = CreateRepository();

		await repository.ApplyAsync(current =>
		{
			Assert.IsNull(current);
			return [A];
		}, CT);

		Assert.AreSequenceEqual(new[] { A }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_WhenExists_PassesCurrentItems()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ABJson);

		await repository.ApplyAsync(current =>
		{
			Assert.IsNotNull(current);
			Assert.AreSequenceEqual(new[] { A, B }, current);
			return current.Add(C);
		}, CT);

		Assert.AreSequenceEqual(new[] { A, B, C }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ReturningSameInstance_DoesNotWrite()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A, B], CT);
		var token = await GetBlobTokenAsync();

		await repository.ApplyAsync(current => current, CT);

		Assert.AreEqual(token, await GetBlobTokenAsync());
	}

	[TestMethod]
	public async Task ApplyAsync_ReturningNull_WhenExists_Deletes()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A], CT);

		await repository.ApplyAsync(_ => null, CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ReturningNull_WhenNotExists_DoesNothing()
	{
		var repository = CreateRepository();

		await repository.ApplyAsync(_ => null, CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_WhenExists_PreservesBlobMetadata()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ABJson, new(MediaType: "application/json", Metadata: new Dictionary<string, string> { ["a"] = "b" }));

		await repository.AddAsync(C, CT);

		var info = await Blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.IsNotNull(info.Metadata);
		Assert.AreEqual("b", info.Metadata["a"]);
		Assert.AreSequenceEqual(new[] { A, B, C }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ConflictingWrite_Retries()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A], CT);
		var calls = 0;

		await repository.ApplyAsync(async (current, ct) =>
		{
			if (calls++ == 0)
			{
				// simulate a concurrent writer between read and write
				await repository.AddAsync(B, ct);
			}

			return current!.Add(C);
		}, CT);

		Assert.AreEqual(2, calls);
		Assert.AreSequenceEqual(new[] { A, B, C }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ConflictingDelete_Retries()
	{
		var repository = CreateRepository();
		await repository.StoreAsync([A], CT);
		var calls = 0;

		await repository.ApplyAsync(async (current, ct) =>
		{
			if (calls++ == 0)
			{
				await Blob.DeleteAsync(IBlob.AnyConcurrencyToken, ct);
			}

			return current is null ? [C] : current.Add(B);
		}, CT);

		Assert.AreEqual(2, calls);
		Assert.AreSequenceEqual(new[] { C }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task AddAsync_Concurrent_AllAdded()
	{
		var repository = CreateRepository();

		await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => repository.AddAsync(new Item("x", i), CT), CT)));

		var items = await repository.ListAsync(CT);
		Assert.AreSequenceEqual(Enumerable.Range(0, 32), items.Select(i => i.Count).Order());
	}

	private sealed class NameComparer : IEqualityComparer<Item>
	{
		public bool Equals(Item? x, Item? y) => x?.Name == y?.Name;

		public int GetHashCode(Item obj) => obj.Name.GetHashCode();
	}
}
