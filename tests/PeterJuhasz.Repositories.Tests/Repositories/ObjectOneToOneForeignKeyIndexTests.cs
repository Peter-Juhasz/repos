using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;
using System.Collections.Immutable;
using System.Text.Json;

namespace PeterJuhasz.Repositories.Tests.Repositories;

[TestClass]
public class ObjectOneToOneForeignKeyIndexTests(TestContext testContext)
{
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlob Blob => field ??= new("test", _time);

	private IOneToOneForeignKeyIndex CreateIndex() =>
		new ObjectOneToOneForeignKeyIndex(Blob.AsJsonObjectRepository<IImmutableDictionary<string, string>>(JsonSerializerOptions.Web));

	private async Task<string> ReadBlobAsync()
	{
		var result = await Blob.ReadAsync(CT);
		Assert.IsNotNull(result);
		return result.Value.ToString();
	}

	private async Task<string?> GetBlobTokenAsync() => (await Blob.GetInfoAsync(CT))?.ConcurrencyToken;

	// Extensions

	[TestMethod]
	public void AsOneToOneForeignKeyIndex_CreatesIndex()
	{
		var repository = Blob.AsJsonObjectRepository<IImmutableDictionary<string, string>>(JsonSerializerOptions.Web);

		Assert.IsInstanceOfType<ObjectOneToOneForeignKeyIndex>(repository.AsOneToOneForeignKeyIndex());
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
	public async Task AddAsync_StoresJsonDictionaryInBlob()
	{
		var index = CreateIndex();

		await index.AddAsync("fk", "pk", CT);

		Assert.AreEqual("""{"fk":"pk"}""", await ReadBlobAsync());
	}

	[TestMethod]
	public async Task AddAsync_WhenExists_ThrowsAndKeepsOriginal()
	{
		var index = CreateIndex();
		await index.AddAsync("fk", "pk1", CT);
		var token = await GetBlobTokenAsync();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => index.AddAsync("fk", "pk2", CT));

		Assert.AreEqual("pk1", await index.GetAsync("fk", CT));
		Assert.AreEqual(token, await GetBlobTokenAsync());
	}

	[TestMethod]
	public async Task AddAsync_WhenExistsWithSameValue_Throws()
	{
		var index = CreateIndex();
		await index.AddAsync("fk", "pk", CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => index.AddAsync("fk", "pk", CT));
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
	public async Task AddAsync_ConcurrentSameKey_OnlyOneSucceeds()
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

	[TestMethod]
	public async Task AddAsync_ConcurrentDifferentKeys_AllAdded()
	{
		var index = CreateIndex();

		await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => index.AddAsync($"fk{i}", $"pk{i}", CT), CT)));

		for (var i = 0; i < 32; i++)
		{
			Assert.AreEqual($"pk{i}", await index.GetAsync($"fk{i}", CT));
		}
	}

	// AddOrUpdateAsync

	[TestMethod]
	public async Task AddOrUpdateAsync_WhenNotExists_AddsAndReturnsTrue()
	{
		var index = CreateIndex();

		Assert.IsTrue(await index.AddOrUpdateAsync("fk", "pk", CT));

		Assert.AreEqual("pk", await index.GetAsync("fk", CT));
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
		var token = await GetBlobTokenAsync();

		Assert.IsFalse(await index.AddOrUpdateAsync("fk", "pk", CT));

		Assert.AreEqual(token, await GetBlobTokenAsync());
	}

	[TestMethod]
	public async Task AddOrUpdateAsync_Concurrent_ExactlyOneAdds()
	{
		var index = CreateIndex();

		var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => index.AddOrUpdateAsync("fk", $"pk{i}", CT), CT)));

		Assert.AreEqual(1, results.Count(r => r));
		Assert.IsTrue(await index.ExistsAsync("fk", CT));
	}

	// DeleteAsync

	[TestMethod]
	public async Task DeleteAsync_WhenExists_Removes()
	{
		var index = CreateIndex();
		await index.AddAsync("fk1", "pk1", CT);
		await index.AddAsync("fk2", "pk2", CT);

		await index.DeleteAsync("fk1", CT);

		Assert.IsFalse(await index.ExistsAsync("fk1", CT));
		Assert.AreEqual("pk2", await index.GetAsync("fk2", CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_Throws()
	{
		var index = CreateIndex();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => index.DeleteAsync("fk", CT));

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenKeyNotExists_ThrowsAndDoesNotWrite()
	{
		var index = CreateIndex();
		await index.AddAsync("fk1", "pk1", CT);
		var token = await GetBlobTokenAsync();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => index.DeleteAsync("fk2", CT));

		Assert.AreEqual(token, await GetBlobTokenAsync());
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

	// DeleteIfExistsAsync

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenExists_RemovesAndReturnsTrue()
	{
		var index = CreateIndex();
		await index.AddAsync("fk", "pk", CT);

		Assert.IsTrue(await index.DeleteIfExistsAsync("fk", CT));

		Assert.IsFalse(await index.ExistsAsync("fk", CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenNotExists_ReturnsFalseAndDoesNotCreateBlob()
	{
		var index = CreateIndex();

		Assert.IsFalse(await index.DeleteIfExistsAsync("fk", CT));

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenKeyNotExists_ReturnsFalseAndDoesNotWrite()
	{
		var index = CreateIndex();
		await index.AddAsync("fk1", "pk1", CT);
		var token = await GetBlobTokenAsync();

		Assert.IsFalse(await index.DeleteIfExistsAsync("fk2", CT));

		Assert.AreEqual(token, await GetBlobTokenAsync());
	}
}
