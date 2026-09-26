using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Caching;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization.Json;

namespace PeterJuhasz.Repositories.Tests.Blobs;

[TestClass]
public class PartitionCollectionOneToManyForeignKeyIndexTests(TestContext testContext)
{
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlobPartition Partition => field ??= new(_time);

	private IOneToManyForeignKeyIndex CreateIndex(IEqualityComparer<string>? comparer = null, string? extension = null) =>
		new PartitionCollectionOneToManyForeignKeyIndex(Partition, JsonSerializerOptionsJsonCollectionSerializer<string>.Web, comparer, extension);

	private Task<List<string>> ListAsync(IOneToManyForeignKeyIndex index, string principalKey) => index.ListAsync(principalKey, CT).ToListAsync(CT).AsTask();

	private async Task<string> ReadBlobAsync(string name)
	{
		var result = await Partition.GetBlob(name).ReadAsync(CT);
		Assert.IsNotNull(result);
		return result.Value.ToString();
	}

	private async Task<string?> GetBlobTokenAsync(string name) => (await Partition.GetBlob(name).GetInfoAsync(CT))?.ConcurrencyToken;

	// Extensions

	[TestMethod]
	public void AsOneToManyForeignKeyIndexInBlob_CreatesIndex()
	{
		Assert.IsInstanceOfType<PartitionCollectionOneToManyForeignKeyIndex>(Partition.AsOneToManyForeignKeyIndexInBlob(JsonSerializerOptionsJsonCollectionSerializer<string>.Web));
	}

	// Empty index

	[TestMethod]
	public async Task EmptyIndex_IsEmpty()
	{
		var index = CreateIndex();

		Assert.IsEmpty(await ListAsync(index, "pk"));
		Assert.IsFalse(await index.ContainsAsync("pk", "fk", CT));
	}

	// AddAsync

	[TestMethod]
	public async Task AddAsync_AddsForeignKey()
	{
		var index = CreateIndex();

		await index.AddAsync("pk", "fk", CT);

		Assert.IsTrue(await index.ContainsAsync("pk", "fk", CT));
		Assert.AreSequenceEqual(new[] { "fk" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task AddAsync_StoresJsonArrayInBlobNamedAfterPrincipalKey()
	{
		var index = CreateIndex();

		await index.AddAsync("pk", "a", CT);
		await index.AddAsync("pk", "b", CT);

		Assert.AreEqual("""["a","b"]""", await ReadBlobAsync("pk"));
		Assert.AreEqual("application/json", (await Partition.GetBlob("pk").GetInfoAsync(CT))?.MediaType);
	}

	[TestMethod]
	public async Task AddAsync_WithExtension_AppendsExtensionToBlobName()
	{
		var index = CreateIndex(extension: ".json");

		await index.AddAsync("pk", "a", CT);

		Assert.AreEqual("""["a"]""", await ReadBlobAsync("pk.json"));
		Assert.IsFalse(await Partition.GetBlob("pk").ExistsAsync(CT));
		Assert.AreSequenceEqual(new[] { "a" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task AddAsync_WhenExists_ThrowsAndDoesNotWrite()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "fk", CT);
		var token = await GetBlobTokenAsync("pk");

		await Assert.ThrowsExactlyAsync<ConflictException>(() => index.AddAsync("pk", "fk", CT));

		Assert.AreEqual(token, await GetBlobTokenAsync("pk"));
		Assert.AreSequenceEqual(new[] { "fk" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task AddAsync_WhenExistsByComparer_Throws()
	{
		var index = CreateIndex(StringComparer.OrdinalIgnoreCase);
		await index.AddAsync("pk", "fk", CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => index.AddAsync("pk", "FK", CT));
	}

	[TestMethod]
	public async Task AddAsync_KeepsInsertionOrder()
	{
		var index = CreateIndex();

		await index.AddAsync("pk", "c", CT);
		await index.AddAsync("pk", "a", CT);
		await index.AddAsync("pk", "b", CT);

		Assert.AreSequenceEqual(new[] { "c", "a", "b" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task AddAsync_PrincipalKeysAreIndependent()
	{
		var index = CreateIndex();

		await index.AddAsync("pk1", "a", CT);
		await index.AddAsync("pk2", "b", CT);

		Assert.AreSequenceEqual(new[] { "a" }, await ListAsync(index, "pk1"));
		Assert.AreSequenceEqual(new[] { "b" }, await ListAsync(index, "pk2"));
		Assert.IsFalse(await index.ContainsAsync("pk1", "b", CT));
	}

	[TestMethod]
	public async Task AddAsync_Concurrent_AllAdded()
	{
		var index = CreateIndex();

		await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => index.AddAsync("pk", $"fk{i:00}", CT), CT)));

		Assert.AreSequenceEqual(Enumerable.Range(0, 32).Select(i => $"fk{i:00}"), (await ListAsync(index, "pk")).Order());
	}

	// AddOrUpdateAsync

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenNotExists_AddsAndReturnsTrue()
	{
		var index = CreateIndex();

		Assert.IsTrue(await index.AddOrUpdateAsync("pk", "fk", CT));

		Assert.AreSequenceEqual(new[] { "fk" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenExists_ReturnsFalseAndDoesNotWrite()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "fk", CT);
		var token = await GetBlobTokenAsync("pk");

		Assert.IsFalse(await index.AddOrUpdateAsync("pk", "fk", CT));

		Assert.AreEqual(token, await GetBlobTokenAsync("pk"));
		Assert.AreSequenceEqual(new[] { "fk" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenExistsByComparer_ReturnsFalse()
	{
		var index = CreateIndex(StringComparer.OrdinalIgnoreCase);
		await index.AddAsync("pk", "fk", CT);

		Assert.IsFalse(await index.AddOrUpdateAsync("pk", "FK", CT));

		Assert.AreSequenceEqual(new[] { "fk" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_Concurrent_ExactlyOneAdds()
	{
		var index = CreateIndex();

		var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => index.AddOrUpdateAsync("pk", "fk", CT), CT)));

		Assert.AreEqual(1, results.Count(r => r));
		Assert.AreSequenceEqual(new[] { "fk" }, await ListAsync(index, "pk"));
	}

	// ContainsAsync

	[TestMethod]
	public async Task ContainsAsync_UsesComparer()
	{
		var index = CreateIndex(StringComparer.OrdinalIgnoreCase);
		await index.AddAsync("pk", "fk", CT);

		Assert.IsTrue(await index.ContainsAsync("pk", "FK", CT));
	}

	// DeleteAsync

	[TestMethod]
	public async Task DeleteAsync_WhenExists_Removes()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "a", CT);
		await index.AddAsync("pk", "b", CT);

		await index.DeleteAsync("pk", "a", CT);

		Assert.IsFalse(await index.ContainsAsync("pk", "a", CT));
		Assert.AreSequenceEqual(new[] { "b" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task DeleteAsync_LastForeignKey_DeletesBlob()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "fk", CT);

		await index.DeleteAsync("pk", "fk", CT);

		Assert.IsFalse(await Partition.GetBlob("pk").ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_UsesComparer()
	{
		var index = CreateIndex(StringComparer.OrdinalIgnoreCase);
		await index.AddAsync("pk", "fk", CT);

		await index.DeleteAsync("pk", "FK", CT);

		Assert.IsFalse(await index.ContainsAsync("pk", "fk", CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_Throws()
	{
		var index = CreateIndex();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => index.DeleteAsync("pk", "fk", CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenForeignKeyNotExists_ThrowsAndDoesNotWrite()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "a", CT);
		var token = await GetBlobTokenAsync("pk");

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => index.DeleteAsync("pk", "b", CT));

		Assert.AreEqual(token, await GetBlobTokenAsync("pk"));
	}

	[TestMethod]
	public async Task DeleteAsync_ThenAdd_Succeeds()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "fk", CT);
		await index.DeleteAsync("pk", "fk", CT);

		await index.AddAsync("pk", "fk", CT);

		Assert.IsTrue(await index.ContainsAsync("pk", "fk", CT));
	}

	[TestMethod]
	public async Task DeleteAsync_PrincipalKey_RemovesAllForeignKeys()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "a", CT);
		await index.AddAsync("pk", "b", CT);
		await index.AddAsync("pk2", "c", CT);

		await index.DeleteAsync("pk", CT);

		Assert.IsEmpty(await ListAsync(index, "pk"));
		Assert.IsFalse(await Partition.GetBlob("pk").ExistsAsync(CT));
		Assert.AreSequenceEqual(new[] { "c" }, await ListAsync(index, "pk2"));
	}

	[TestMethod]
	public async Task DeleteAsync_PrincipalKey_WhenNotExists_DoesNothing()
	{
		var index = CreateIndex();

		await index.DeleteAsync("pk", CT);

		Assert.IsEmpty(await ListAsync(index, "pk"));
	}

	// DeleteIfExistsAsync

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenExists_RemovesAndReturnsTrue()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "a", CT);
		await index.AddAsync("pk", "b", CT);

		Assert.IsTrue(await index.DeleteIfExistsAsync("pk", "a", CT));

		Assert.AreSequenceEqual(new[] { "b" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_LastForeignKey_DeletesBlob()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "fk", CT);

		Assert.IsTrue(await index.DeleteIfExistsAsync("pk", "fk", CT));

		Assert.IsFalse(await Partition.GetBlob("pk").ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenNotExists_ReturnsFalse()
	{
		var index = CreateIndex();

		Assert.IsFalse(await index.DeleteIfExistsAsync("pk", "fk", CT));

		Assert.IsFalse(await Partition.GetBlob("pk").ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenForeignKeyNotExists_ReturnsFalseAndDoesNotWrite()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "a", CT);
		var token = await GetBlobTokenAsync("pk");

		Assert.IsFalse(await index.DeleteIfExistsAsync("pk", "b", CT));

		Assert.AreEqual(token, await GetBlobTokenAsync("pk"));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_UsesComparer()
	{
		var index = CreateIndex(StringComparer.OrdinalIgnoreCase);
		await index.AddAsync("pk", "fk", CT);

		Assert.IsTrue(await index.DeleteIfExistsAsync("pk", "FK", CT));

		Assert.IsFalse(await index.ContainsAsync("pk", "fk", CT));
	}
}
