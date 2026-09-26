using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;

namespace PeterJuhasz.Repositories.Tests.Blobs;

[TestClass]
public class BlobNameOneToManyForeignKeyIndexTests(TestContext testContext)
{
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlobPartition Partition => field ??= new(_time);

	private IOneToManyForeignKeyIndex CreateIndex(IBlobPartition? partition = null) => new BlobNameOneToManyForeignKeyIndex(partition ?? Partition);

	private Task<List<string>> ListAsync(IOneToManyForeignKeyIndex index, string principalKey) => index.ListAsync(principalKey, CT).ToListAsync(CT).AsTask();

	// Extensions

	[TestMethod]
	public void AsOneToManyForeignKeyIndexInBlobNames_CreatesBlobNameIndex()
	{
		Assert.IsInstanceOfType<BlobNameOneToManyForeignKeyIndex>(Partition.AsOneToManyForeignKeyIndexInBlobNames());
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
	public async Task AddAsync_StoresEmptyBlobUnderPrincipalKey()
	{
		var index = CreateIndex();

		await index.AddAsync("pk", "fk", CT);

		var result = await Partition.GetBlob("pk/fk").ReadAsync(CT);
		Assert.IsNotNull(result);
		Assert.IsTrue(result.Value.IsEmpty);
	}

	[TestMethod]
	public async Task AddAsync_WhenExists_Throws()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "fk", CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => index.AddAsync("pk", "fk", CT));
	}

	[TestMethod]
	public async Task AddAsync_MultipleForeignKeys_ListedInOrder()
	{
		var index = CreateIndex();

		await index.AddAsync("pk", "c", CT);
		await index.AddAsync("pk", "a", CT);
		await index.AddAsync("pk", "b", CT);

		Assert.AreSequenceEqual(new[] { "a", "b", "c" }, await ListAsync(index, "pk"));
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
	public async Task AddAsync_PrincipalKeyIsPrefixOfAnother_AreIndependent()
	{
		var index = CreateIndex();

		await index.AddAsync("pk", "a", CT);
		await index.AddAsync("pk2", "b", CT);

		Assert.AreSequenceEqual(new[] { "a" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task AddAsync_SameForeignKeyUnderDifferentPrincipals_Succeeds()
	{
		var index = CreateIndex();

		await index.AddAsync("pk1", "fk", CT);
		await index.AddAsync("pk2", "fk", CT);

		Assert.IsTrue(await index.ContainsAsync("pk1", "fk", CT));
		Assert.IsTrue(await index.ContainsAsync("pk2", "fk", CT));
	}

	// AddOrUpdateAsync

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenNotExists_AddsAndReturnsTrue()
	{
		var index = CreateIndex();

		Assert.IsTrue(await index.AddOrUpdateAsync("pk", "fk", CT));

		Assert.IsTrue(await index.ContainsAsync("pk", "fk", CT));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenExists_ReturnsFalse()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "fk", CT);

		Assert.IsFalse(await index.AddOrUpdateAsync("pk", "fk", CT));

		Assert.AreSequenceEqual(new[] { "fk" }, await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_Concurrent_ExactlyOneAdds()
	{
		var index = CreateIndex();

		var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => index.AddOrUpdateAsync("pk", "fk", CT), CT)));

		Assert.AreEqual(1, results.Count(r => r));
	}

	// ListAsync

	[TestMethod]
	public async Task ListAsync_InSubPartition_ReturnsForeignKeys()
	{
		var index = CreateIndex(Partition.GetSubPartition("users").GetSubPartition("by-group"));

		await index.AddAsync("group1", "user1", CT);
		await index.AddAsync("group1", "user2", CT);

		Assert.AreSequenceEqual(new[] { "user1", "user2" }, await ListAsync(index, "group1"));
		Assert.IsTrue(await Partition.GetBlob("users/by-group/group1/user1").ExistsAsync(CT));
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
	public async Task DeleteAsync_WhenNotExists_Throws()
	{
		var index = CreateIndex();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => index.DeleteAsync("pk", "fk", CT));
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
		Assert.AreSequenceEqual(new[] { "c" }, await ListAsync(index, "pk2"));
	}

	[TestMethod]
	public async Task DeleteAsync_PrincipalKey_WhenNotExists_DoesNothing()
	{
		var index = CreateIndex();

		await index.DeleteAsync("pk", CT);

		Assert.IsEmpty(await ListAsync(index, "pk"));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenExists_RemovesAndReturnsTrue()
	{
		var index = CreateIndex();
		await index.AddAsync("pk", "fk", CT);

		Assert.IsTrue(await index.DeleteIfExistsAsync("pk", "fk", CT));

		Assert.IsFalse(await index.ContainsAsync("pk", "fk", CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenNotExists_ReturnsFalse()
	{
		var index = CreateIndex();

		Assert.IsFalse(await index.DeleteIfExistsAsync("pk", "fk", CT));
	}
}
