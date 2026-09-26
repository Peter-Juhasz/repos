using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Abstractions.Blobs;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Blobs;

[TestClass]
public class BlobPartitionBlobOneToOneForeignKeyIndexTests(TestContext testContext)
{
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlobPartition Partition => field ??= new(_time);

	private IOneToOneForeignKeyIndex CreateIndex() => new BlobPartitionBlobOneToOneForeignKeyIndex(Partition, AsciiStringSerializer.Instance);

	private async Task<string> ReadBlobAsync(string name)
	{
		var result = await Partition.GetBlob(name).ReadAsync(CT);
		Assert.IsNotNull(result);
		return result.Value.ToString();
	}

	private async Task<string?> GetBlobTokenAsync(string name) => (await Partition.GetBlob(name).GetInfoAsync(CT))?.ConcurrencyToken;

	// Extensions

	[TestMethod]
	public void AsOneToOneForeignKeyIndex_CreatesBlobIndex()
	{
		Assert.IsInstanceOfType<BlobPartitionBlobOneToOneForeignKeyIndex>(Partition.AsOneToOneForeignKeyIndex(AsciiStringSerializer.Instance));
	}

	[TestMethod]
	public async Task AsOneToOneForeignKeyIndex_Default_UsesUtf8()
	{
		var index = Partition.AsOneToOneForeignKeyIndex();

		await index.AddAsync("fk", "árvíztűrő", CT);

		Assert.AreEqual("árvíztűrő", await index.GetAsync("fk", CT));
	}

	// Empty index

	[TestMethod]
	public async Task EmptyIndex_KeyDoesNotExist()
	{
		var index = CreateIndex();

		Assert.IsNull(await index.GetOrDefaultAsync("fk", CT));
		Assert.IsFalse(await index.ExistsAsync("fk", CT));
		await Assert.ThrowsExactlyAsync<NotFoundException>(() => index.GetAsync("fk", CT));
	}

	// AddAsync

	[TestMethod]
	public async Task AddAsync_StoresPrincipalKey()
	{
		var index = CreateIndex();

		await index.AddAsync("fk", "pk", CT);

		Assert.AreEqual("pk", await index.GetOrDefaultAsync("fk", CT));
		Assert.AreEqual("pk", await index.GetAsync("fk", CT));
		Assert.IsTrue(await index.ExistsAsync("fk", CT));
	}

	[TestMethod]
	public async Task AddAsync_StoresAsciiBlobWithRefExtension()
	{
		var index = CreateIndex();

		await index.AddAsync("fk", "pk", CT);

		Assert.AreEqual("pk", await ReadBlobAsync("fk.ref"));
		Assert.AreEqual("text/plain", (await Partition.GetBlob("fk.ref").GetInfoAsync(CT))?.MediaType);
		Assert.AreSequenceEqual(new[] { "fk.ref" }, await Partition.GetBlobs(CT).Select(b => b.Name).ToArrayAsync(CT));
	}

	[TestMethod]
	public async Task AddAsync_WhenExists_ThrowsAndKeepsOriginal()
	{
		var index = CreateIndex();
		await index.AddAsync("fk", "pk1", CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => index.AddAsync("fk", "pk2", CT));

		Assert.AreEqual("pk1", await index.GetAsync("fk", CT));
	}

	[TestMethod]
	public async Task AddAsync_MultipleKeys_AreIndependent()
	{
		var index = CreateIndex();

		await index.AddAsync("fk1", "pk1", CT);
		await index.AddAsync("fk2", "pk2", CT);

		Assert.AreEqual("pk1", await index.GetAsync("fk1", CT));
		Assert.AreEqual("pk2", await index.GetAsync("fk2", CT));
	}

	[TestMethod]
	public async Task AddAsync_SamePrincipalKeyForDifferentForeignKeys_Succeeds()
	{
		var index = CreateIndex();

		await index.AddAsync("fk1", "pk", CT);
		await index.AddAsync("fk2", "pk", CT);

		Assert.AreEqual("pk", await index.GetAsync("fk1", CT));
		Assert.AreEqual("pk", await index.GetAsync("fk2", CT));
	}

	[TestMethod]
	public async Task AddAsync_EmptyPrincipalKey_RoundTrips()
	{
		var index = CreateIndex();

		await index.AddAsync("fk", "", CT);

		Assert.AreEqual("", await index.GetOrDefaultAsync("fk", CT));
		Assert.IsTrue(await index.ExistsAsync("fk", CT));
	}

	[TestMethod]
	public async Task AddAsync_NonAsciiPrincipalKey_ThrowsAndDoesNotStore()
	{
		var index = CreateIndex();

		await Assert.ThrowsExactlyAsync<ArgumentException>(() => index.AddAsync("fk", "árvíztűrő", CT));

		Assert.IsFalse(await index.ExistsAsync("fk", CT));
	}

	[TestMethod]
	public async Task AddAsync_Concurrent_OnlyOneSucceeds()
	{
		var index = CreateIndex();

		var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
		{
			try
			{
				await index.AddAsync("fk", $"pk{i}", CT);
				return true;
			}
			catch (ConflictException)
			{
				return false;
			}
		}, CT)));

		Assert.AreEqual(1, results.Count(r => r));
	}

	// AddOrUpdateAsync

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenNotExists_AddsAndReturnsTrue()
	{
		var index = CreateIndex();

		Assert.IsTrue(await index.AddOrUpdateAsync("fk", "pk", CT));

		Assert.AreEqual("pk", await index.GetAsync("fk", CT));
		Assert.AreEqual("text/plain", (await Partition.GetBlob("fk.ref").GetInfoAsync(CT))?.MediaType);
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenExists_UpdatesAndReturnsFalse()
	{
		var index = CreateIndex();
		await index.AddAsync("fk", "pk1", CT);

		Assert.IsFalse(await index.AddOrUpdateAsync("fk", "pk2", CT));

		Assert.AreEqual("pk2", await index.GetAsync("fk", CT));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_SameValue_ReturnsFalseAndDoesNotWrite()
	{
		var index = CreateIndex();
		await index.AddAsync("fk", "pk", CT);
		var token = await GetBlobTokenAsync("fk.ref");

		Assert.IsFalse(await index.AddOrUpdateAsync("fk", "pk", CT));

		Assert.AreEqual(token, await GetBlobTokenAsync("fk.ref"));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_AfterDelete_AddsAndReturnsTrue()
	{
		var index = CreateIndex();
		await index.AddAsync("fk", "pk1", CT);
		await index.DeleteAsync("fk", CT);

		Assert.IsTrue(await index.AddOrUpdateAsync("fk", "pk2", CT));

		Assert.AreEqual("pk2", await index.GetAsync("fk", CT));
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_Concurrent_ExactlyOneAdds()
	{
		var index = CreateIndex();

		var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => index.AddOrUpdateAsync("fk", $"pk{i}", CT), CT)));

		Assert.AreEqual(1, results.Count(r => r));
		Assert.IsTrue(await index.ExistsAsync("fk", CT));
	}

	// DeleteAsync / DeleteIfExistsAsync

	[TestMethod]
	public async Task DeleteAsync_WhenExists_Removes()
	{
		var index = CreateIndex();
		await index.AddAsync("fk", "pk", CT);

		await index.DeleteAsync("fk", CT);

		Assert.IsFalse(await index.ExistsAsync("fk", CT));
		Assert.IsFalse(await Partition.GetBlob("fk.ref").ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_Throws()
	{
		var index = CreateIndex();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => index.DeleteAsync("fk", CT));
	}

	[TestMethod]
	public async Task DeleteAsync_KeepsOtherKeys()
	{
		var index = CreateIndex();
		await index.AddAsync("fk1", "pk1", CT);
		await index.AddAsync("fk2", "pk2", CT);

		await index.DeleteAsync("fk1", CT);

		Assert.IsFalse(await index.ExistsAsync("fk1", CT));
		Assert.AreEqual("pk2", await index.GetAsync("fk2", CT));
	}

	[TestMethod]
	public async Task DeleteAsync_ThenAdd_Succeeds()
	{
		var index = CreateIndex();
		await index.AddAsync("fk", "pk1", CT);
		await index.DeleteAsync("fk", CT);

		await index.AddAsync("fk", "pk2", CT);

		Assert.AreEqual("pk2", await index.GetAsync("fk", CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenExists_RemovesAndReturnsTrue()
	{
		var index = CreateIndex();
		await index.AddAsync("fk", "pk", CT);

		Assert.IsTrue(await index.DeleteIfExistsAsync("fk", CT));

		Assert.IsFalse(await index.ExistsAsync("fk", CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenNotExists_ReturnsFalse()
	{
		var index = CreateIndex();

		Assert.IsFalse(await index.DeleteIfExistsAsync("fk", CT));
	}

	// Partitions

	[TestMethod]
	public async Task SubPartition_StoresBlobUnderPath()
	{
		var index = Partition.GetSubPartition("users").GetSubPartition("by-email").AsOneToOneForeignKeyIndex(AsciiStringSerializer.Instance);

		await index.AddAsync("a@example.com", "user1", CT);

		Assert.AreEqual("user1", await ReadBlobAsync("users/by-email/a@example.com.ref"));
	}

	[TestMethod]
	public async Task SeparatePartitions_AreIndependent()
	{
		var index1 = Partition.GetSubPartition("a").AsOneToOneForeignKeyIndex(AsciiStringSerializer.Instance);
		var index2 = Partition.GetSubPartition("b").AsOneToOneForeignKeyIndex(AsciiStringSerializer.Instance);

		await index1.AddAsync("fk", "pk1", CT);
		await index2.AddAsync("fk", "pk2", CT);

		Assert.AreEqual("pk1", await index1.GetAsync("fk", CT));
		Assert.AreEqual("pk2", await index2.GetAsync("fk", CT));
	}

	[TestMethod]
	public async Task ClearingPartition_RemovesAllKeys()
	{
		var index = CreateIndex();
		await index.AddAsync("fk1", "pk1", CT);
		await index.AddAsync("fk2", "pk2", CT);

		await Partition.ClearAsync(CT);

		Assert.IsFalse(await index.ExistsAsync("fk1", CT));
		Assert.IsFalse(await index.ExistsAsync("fk2", CT));
	}

	// Corrupt data

	[TestMethod]
	public async Task GetOrDefaultAsync_NonAsciiContent_Throws()
	{
		var index = CreateIndex();
		await Partition.GetBlob("fk.ref").WriteAsync(Encoding.UTF8.GetBytes("árvíztűrő"), null, new(MediaType: "text/plain"), CT);

		await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => index.GetOrDefaultAsync("fk", CT));
	}

	[TestMethod]
	public async Task GetOrDefaultAsync_UnexpectedMediaType_Throws()
	{
		var index = CreateIndex();
		await Partition.GetBlob("fk.ref").WriteAsync(Encoding.ASCII.GetBytes("pk"), null, new(MediaType: "application/json"), CT);

		await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => index.GetOrDefaultAsync("fk", CT));
	}
}
