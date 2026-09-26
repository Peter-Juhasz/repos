using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Blobs;

[TestClass]
public class BlobNameSetRepositoryTests(TestContext testContext)
{
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlobPartition Partition => field ??= new(_time);

	private ISetRepository<string> CreateRepository(IBlobPartition? partition = null) =>
		new BlobNameSetRepository<string>(partition ?? Partition, AsciiStringSerializer.Instance, Encoding.ASCII);

	private Task<List<string>> ListAsync(ISetRepository<string> repository) => repository.ListAsync(CT).ToListAsync(CT).AsTask();

	// Extensions

	[TestMethod]
	public void AsSetRepositoryAsNames_CreatesBlobNameSetRepository()
	{
		Assert.IsInstanceOfType<BlobNameSetRepository<string>>(Partition.AsSetRepositoryAsNames(AsciiStringSerializer.Instance, Encoding.ASCII));
	}

	// Empty set

	[TestMethod]
	public async Task EmptySet_IsEmpty()
	{
		var repository = CreateRepository();

		Assert.IsEmpty(await ListAsync(repository));
		Assert.IsFalse(await repository.ContainsAsync("a", CT));
	}

	// AddAsync

	[TestMethod]
	public async Task AddAsync_WhenNotExists_AddsAndReturnsTrue()
	{
		var repository = CreateRepository();

		Assert.IsTrue(await repository.AddAsync("a", CT));

		Assert.IsTrue(await repository.ContainsAsync("a", CT));
		Assert.AreSequenceEqual(new[] { "a" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task AddAsync_StoresEmptyBlobNamedAfterValue()
	{
		var repository = CreateRepository();

		await repository.AddAsync("a", CT);

		var result = await Partition.GetBlob("a").ReadAsync(CT);
		Assert.IsNotNull(result);
		Assert.IsTrue(result.Value.IsEmpty);
	}

	[TestMethod]
	public async Task AddAsync_WhenExists_ReturnsFalse()
	{
		var repository = CreateRepository();
		await repository.AddAsync("a", CT);

		Assert.IsFalse(await repository.AddAsync("a", CT));

		Assert.AreSequenceEqual(new[] { "a" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task AddAsync_NonAscii_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<ArgumentException>(() => repository.AddAsync("á", CT));
	}

	[TestMethod]
	public async Task AddAsync_Concurrent_OnlyOneReturnsTrue()
	{
		var repository = CreateRepository();

		var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => repository.AddAsync("a", CT), CT)));

		Assert.AreEqual(1, results.Count(r => r));
	}

	[TestMethod]
	public async Task AddRangeAsync_ReturnsNewlyAdded()
	{
		var repository = CreateRepository();
		await repository.AddAsync("a", CT);

		var added = await repository.AddRangeAsync(["a", "b", "c"], CT);

		Assert.IsTrue(added.SetEquals(["b", "c"]));
		Assert.AreSequenceEqual(new[] { "a", "b", "c" }, await ListAsync(repository));
	}

	// ContainsAsync

	[TestMethod]
	public async Task ContainsRangeAsync_ReturnsContained()
	{
		var repository = CreateRepository();
		await repository.AddRangeAsync(["a", "b"], CT);

		var contained = await repository.ContainsRangeAsync(["a", "b", "c"], CT);

		Assert.IsTrue(contained.SetEquals(["a", "b"]));
	}

	// ListAsync

	[TestMethod]
	public async Task ListAsync_ReturnsValuesOrderedByName()
	{
		var repository = CreateRepository();
		await repository.AddRangeAsync(["c", "a", "b"], CT);

		Assert.AreSequenceEqual(new[] { "a", "b", "c" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task ListAsync_InSubPartition_StripsPath()
	{
		var partition = Partition.GetSubPartition("x").GetSubPartition("y");
		var repository = CreateRepository(partition);
		await repository.AddRangeAsync(["a", "b"], CT);

		Assert.AreSequenceEqual(new[] { "a", "b" }, await ListAsync(repository));
		Assert.IsTrue(await Partition.GetBlob("x/y/a").ExistsAsync(CT));
	}

	[TestMethod]
	public async Task SeparatePartitions_AreIndependent()
	{
		var repository1 = CreateRepository(Partition.GetSubPartition("x"));
		var repository2 = CreateRepository(Partition.GetSubPartition("y"));

		await repository1.AddAsync("a", CT);
		await repository2.AddAsync("b", CT);

		Assert.AreSequenceEqual(new[] { "a" }, await ListAsync(repository1));
		Assert.AreSequenceEqual(new[] { "b" }, await ListAsync(repository2));
		Assert.IsFalse(await repository1.ContainsAsync("b", CT));
	}

	// DeleteAsync

	[TestMethod]
	public async Task DeleteAsync_WhenExists_RemovesAndReturnsTrue()
	{
		var repository = CreateRepository();
		await repository.AddRangeAsync(["a", "b"], CT);

		Assert.IsTrue(await repository.DeleteAsync("a", CT));

		Assert.IsFalse(await repository.ContainsAsync("a", CT));
		Assert.AreSequenceEqual(new[] { "b" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_ReturnsFalse()
	{
		var repository = CreateRepository();

		Assert.IsFalse(await repository.DeleteAsync("a", CT));
	}

	[TestMethod]
	public async Task DeleteAsync_ThenAdd_ReturnsTrue()
	{
		var repository = CreateRepository();
		await repository.AddAsync("a", CT);
		await repository.DeleteAsync("a", CT);

		Assert.IsTrue(await repository.AddAsync("a", CT));
	}

	// ClearAsync

	[TestMethod]
	public async Task ClearAsync_RemovesAll()
	{
		var repository = CreateRepository();
		await repository.AddRangeAsync(["a", "b"], CT);

		await repository.ClearAsync(CT);

		Assert.IsEmpty(await ListAsync(repository));
		Assert.IsFalse(await repository.ContainsAsync("a", CT));
	}

	[TestMethod]
	public async Task ClearAsync_KeepsOtherPartitions()
	{
		var repository1 = CreateRepository(Partition.GetSubPartition("x"));
		var repository2 = CreateRepository(Partition.GetSubPartition("y"));
		await repository1.AddAsync("a", CT);
		await repository2.AddAsync("b", CT);

		await repository1.ClearAsync(CT);

		Assert.IsEmpty(await ListAsync(repository1));
		Assert.AreSequenceEqual(new[] { "b" }, await ListAsync(repository2));
	}
}

[TestClass]
public class BlobNameStringSetTests(TestContext testContext)
{
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlobPartition Partition => field ??= new(_time);

	[TestMethod]
	public void AsStringSetRepositoryAsNames_CreatesBlobNameStringSet()
	{
		Assert.IsInstanceOfType<BlobNameStringSet>(Partition.AsStringSetRepositoryAsNames());
	}

	[TestMethod]
	public async Task AddContainsListDelete_RoundTrips()
	{
		var repository = Partition.GetSubPartition("x").AsStringSetRepositoryAsNames();

		Assert.IsTrue(await repository.AddAsync("b", CT));
		Assert.IsTrue(await repository.AddAsync("a", CT));
		Assert.IsFalse(await repository.AddAsync("a", CT));
		Assert.IsTrue(await repository.ContainsAsync("a", CT));
		Assert.AreSequenceEqual(new[] { "a", "b" }, await repository.ListAsync(CT).ToListAsync(CT));

		Assert.IsTrue(await repository.DeleteAsync("a", CT));
		Assert.IsFalse(await repository.DeleteAsync("a", CT));
		Assert.AreSequenceEqual(new[] { "b" }, await repository.ListAsync(CT).ToListAsync(CT));

		await repository.ClearAsync(CT);
		Assert.IsEmpty(await repository.ListAsync(CT).ToListAsync(CT));
	}

	[TestMethod]
	public async Task NonAscii_RoundTrips()
	{
		var repository = Partition.AsStringSetRepositoryAsNames();

		await repository.AddAsync("árvíztűrő", CT);

		Assert.IsTrue(await repository.ContainsAsync("árvíztűrő", CT));
		Assert.AreSequenceEqual(new[] { "árvíztűrő" }, await repository.ListAsync(CT).ToListAsync(CT));
	}
}
