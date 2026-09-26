using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;

namespace PeterJuhasz.Repositories.Tests.InMemory;

[TestClass]
public class InMemoryObjectRepositoryTests(TestContext testContext)
{
	public sealed record class Item(string Name);

	private static readonly Item Value = new("value");
	private static readonly Item OtherValue = new("other");

	private CancellationToken CT => testContext.CancellationToken;

	private static IObjectRepository<Item> CreateRepository() => new InMemoryObjectRepository<Item>();

	// Empty repository

	[TestMethod]
	public async Task NewRepository_DoesNotExist()
	{
		var repository = CreateRepository();

		Assert.IsFalse(await repository.ExistsAsync(CT));
		Assert.IsNull(await repository.GetVersionAsync(CT));
		Assert.IsNull(await repository.GetOrDefaultAsync(CT));
		Assert.IsNull(await repository.GetOrDefaultWithVersionAsync(CT));
	}

	[TestMethod]
	public async Task GetAsync_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<NotFoundException>(async () => await repository.GetAsync(CT));
	}

	// CreateAsync

	[TestMethod]
	public async Task CreateAsync_StoresValueAndVersion()
	{
		var repository = CreateRepository();

		var result = await repository.CreateAsync(Value, CT);

		Assert.AreSame(Value, result.Value);
		Assert.IsFalse(string.IsNullOrEmpty(result.ETag));
		Assert.IsTrue(await repository.ExistsAsync(CT));
		Assert.AreEqual(result.ETag, await repository.GetVersionAsync(CT));
		Assert.AreSame(Value, await repository.GetAsync(CT));
		Assert.AreSame(Value, await repository.GetOrDefaultAsync(CT));

		var versioned = await repository.GetOrDefaultWithVersionAsync(CT);
		Assert.IsNotNull(versioned);
		Assert.AreSame(Value, versioned.Value.Value);
		Assert.AreEqual(result.ETag, versioned.Value.ETag);
	}

	[TestMethod]
	public async Task CreateAsync_WhenExists_Throws()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.CreateAsync(OtherValue, CT));

		Assert.AreSame(Value, await repository.GetAsync(CT));
		Assert.AreEqual(created.ETag, await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task CreateAsync_ConcurrentCreators_OnlyOneSucceeds()
	{
		var repository = CreateRepository();

		var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(async () =>
		{
			try
			{
				await repository.CreateAsync(new Item(i.ToString()), CT);
				return true;
			}
			catch (ConflictException)
			{
				return false;
			}
		}, CT)));

		Assert.AreEqual(1, results.Count(r => r));
	}

	// CreateIfNotExistsAsync

	[TestMethod]
	public async Task CreateIfNotExistsAsync_WhenNotExists_CreatesAndReturnsTrue()
	{
		var repository = CreateRepository();

		Assert.IsTrue(await repository.CreateIfNotExistsAsync(Value, CT));

		Assert.AreSame(Value, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task CreateIfNotExistsAsync_WhenExists_ReturnsFalse()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		Assert.IsFalse(await repository.CreateIfNotExistsAsync(OtherValue, CT));

		Assert.AreSame(Value, await repository.GetAsync(CT));
	}

	// UpdateAsync / StoreAsync

	[TestMethod]
	public async Task UpdateAsync_WithCurrentVersion_Overwrites()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		var updated = await repository.UpdateAsync(OtherValue, created.ETag, CT);

		Assert.AreSame(OtherValue, updated.Value);
		Assert.AreNotEqual(created.ETag, updated.ETag);
		Assert.AreSame(OtherValue, await repository.GetAsync(CT));
		Assert.AreEqual(updated.ETag, await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task UpdateAsync_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.UpdateAsync(Value, "version", CT));

		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task UpdateAsync_WithStaleVersion_Throws()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);
		await repository.UpdateAsync(Value, created.ETag, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.UpdateAsync(OtherValue, created.ETag, CT));

		Assert.AreSame(Value, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task UpdateAsync_ConcurrentUpdatersWithSameVersion_OnlyOneSucceeds()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
		{
			try
			{
				await repository.UpdateAsync(OtherValue, created.ETag, CT);
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
	public async Task StoreAsync_AnyToken_WhenExists_Overwrites()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		await repository.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		Assert.AreSame(OtherValue, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task StoreAsync_AnyToken_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.StoreAsync(Value, IBlob.AnyConcurrencyToken, CT));

		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task StoreAsync_AnyOrNoneToken_WhenNotExists_Creates()
	{
		var repository = CreateRepository();

		var result = await repository.StoreAsync(Value, IBlob.AnyOrNoneConcurrencyToken, CT);

		Assert.AreSame(Value, await repository.GetAsync(CT));
		Assert.AreEqual(result.ETag, await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task StoreAsync_AnyOrNoneToken_WhenExists_Overwrites()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		await repository.StoreAsync(OtherValue, IBlob.AnyOrNoneConcurrencyToken, CT);

		Assert.AreSame(OtherValue, await repository.GetAsync(CT));
	}

	// DeleteWithVersionAsync / DeleteAsync

	[TestMethod]
	public async Task DeleteWithVersionAsync_WithCurrentVersion_Removes()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		await repository.DeleteWithVersionAsync(created.ETag, CT);

		Assert.IsFalse(await repository.ExistsAsync(CT));
		Assert.IsNull(await repository.GetVersionAsync(CT));
		Assert.IsNull(await repository.GetOrDefaultWithVersionAsync(CT));
	}

	[TestMethod]
	public async Task DeleteWithVersionAsync_WithStaleVersion_Throws()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);
		await repository.UpdateAsync(OtherValue, created.ETag, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.DeleteWithVersionAsync(created.ETag, CT));

		Assert.AreSame(OtherValue, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task DeleteWithVersionAsync_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => repository.DeleteWithVersionAsync("version", CT));
	}

	[TestMethod]
	public async Task DeleteWithVersionAsync_OldVersionAfterDelete_Throws()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);
		await repository.DeleteWithVersionAsync(created.ETag, CT);

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => repository.DeleteWithVersionAsync(created.ETag, CT));
		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.UpdateAsync(Value, created.ETag, CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenExists_Removes()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		await repository.DeleteAsync(CT);

		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => repository.DeleteAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_ThenCreate_Succeeds()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);
		await repository.DeleteAsync(CT);

		await repository.CreateAsync(OtherValue, CT);

		Assert.AreSame(OtherValue, await repository.GetAsync(CT));
	}

	// DeleteIfExistsAsync

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenNotExists_ReturnsFalse()
	{
		var repository = CreateRepository();

		Assert.IsFalse(await repository.DeleteIfExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenExists_RemovesAndReturnsTrue()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		Assert.IsTrue(await repository.DeleteIfExistsAsync(CT));

		Assert.IsFalse(await repository.ExistsAsync(CT));
		Assert.IsFalse(await repository.DeleteIfExistsAsync(CT));
	}

	// ApplyAsync

	[TestMethod]
	public async Task ApplyAsync_WhenNotExists_Creates()
	{
		var repository = CreateRepository();

		var result = await repository.ApplyAsync(current =>
		{
			Assert.IsNull(current);
			return Value;
		}, CT);

		Assert.AreSame(Value, result);
		Assert.AreSame(Value, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_WhenExists_Updates()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		var result = await repository.ApplyAsync(current =>
		{
			Assert.AreSame(Value, current);
			return OtherValue;
		}, CT);

		Assert.AreSame(OtherValue, result);
		Assert.AreSame(OtherValue, await repository.GetAsync(CT));
		Assert.AreNotEqual(created.ETag, await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ReturningNull_WhenExists_Deletes()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		var result = await repository.ApplyAsync(_ => null, CT);

		Assert.IsNull(result);
		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ReturningNull_WhenNotExists_DoesNothing()
	{
		var repository = CreateRepository();

		var result = await repository.ApplyAsync(_ => null, CT);

		Assert.IsNull(result);
		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ConflictingWrite_Retries()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(new Item("0"), CT);
		var calls = 0;

		var result = await repository.ApplyAsync(async (current, ct) =>
		{
			if (calls++ == 0)
			{
				// simulate a concurrent writer between read and write
				await repository.ApplyAsync(c => new Item(c!.Name + "a"), ct);
			}

			return new Item(current!.Name + "b");
		}, CT);

		Assert.AreEqual(2, calls);
		Assert.AreEqual(new Item("0ab"), result);
		Assert.AreEqual(new Item("0ab"), await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ConcurrentAppliers_AllApplied()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(new Item(""), CT);

		await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => repository.ApplyAsync(c => new Item(c!.Name + "x"), CT), CT)));

		Assert.AreEqual(new Item(new string('x', 32)), await repository.GetAsync(CT));
	}
}
