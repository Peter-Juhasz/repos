using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Blobs;

[TestClass]
public class BlobSortedNameSetRepositoryTests(TestContext testContext)
{
	// sort keys deliberately in a different order than the values
	private static readonly Dictionary<string, string> SortKeys = new()
	{
		["apple"] = "3",
		["banana"] = "1",
		["cherry"] = "2",
	};

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlobPartition Partition => field ??= new(_time);

	private ISetRepository<string> CreateRepository(IBlobPartition? partition = null, Func<string, string>? sortKeySelector = null) =>
		new BlobSortedNameSetRepository<string>(partition ?? Partition, AsciiStringSerializer.Instance, sortKeySelector ?? (v => SortKeys[v]), Encoding.ASCII);

	private Task<List<string>> ListAsync(ISetRepository<string> repository) => repository.ListAsync(CT).ToListAsync(CT).AsTask();

	// Extensions

	[TestMethod]
	public void AsSortedSetRepositoryAsNames_CreatesBlobSortedNameSetRepository()
	{
		var repository = Partition.AsSortedSetRepositoryAsNames(AsciiStringSerializer.Instance, v => v, Encoding.ASCII);

		Assert.IsInstanceOfType<BlobSortedNameSetRepository<string>>(repository);
	}

	// Empty set

	[TestMethod]
	public async Task EmptySet_IsEmpty()
	{
		var repository = CreateRepository();

		Assert.IsEmpty(await ListAsync(repository));
		Assert.IsFalse(await repository.ContainsAsync("apple", CT));
	}

	// AddAsync

	[TestMethod]
	public async Task AddAsync_WhenNotExists_AddsAndReturnsTrue()
	{
		var repository = CreateRepository();

		Assert.IsTrue(await repository.AddAsync("apple", CT));

		Assert.IsTrue(await repository.ContainsAsync("apple", CT));
		Assert.AreSequenceEqual(new[] { "apple" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task AddAsync_StoresEmptyBlobNamedSortKeySeparatorValue()
	{
		var repository = CreateRepository();

		await repository.AddAsync("apple", CT);

		Assert.AreSequenceEqual(new[] { "3|apple" }, await Partition.GetBlobs(CT).Select(b => b.Name).ToListAsync(CT));
		var result = await Partition.GetBlob("3|apple").ReadAsync(CT);
		Assert.IsNotNull(result);
		Assert.IsTrue(result.Value.IsEmpty);
	}

	[TestMethod]
	public async Task AddAsync_CustomSeparator_IsUsedInName()
	{
		var repository = new BlobSortedNameSetRepository<string>(Partition, AsciiStringSerializer.Instance, v => SortKeys[v], Encoding.ASCII, separator: '_');

		await repository.AddAsync("apple", CT);

		Assert.IsTrue(await Partition.GetBlob("3_apple").ExistsAsync(CT));
		Assert.AreSequenceEqual(new[] { "apple" }, await repository.ListAsync(CT).ToListAsync(CT));
	}

	[TestMethod]
	public async Task AddAsync_WhenExists_ReturnsFalse()
	{
		var repository = CreateRepository();
		await repository.AddAsync("apple", CT);

		Assert.IsFalse(await repository.AddAsync("apple", CT));

		Assert.AreSequenceEqual(new[] { "apple" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task AddAsync_SortKeyContainsSeparator_Throws()
	{
		var repository = CreateRepository(sortKeySelector: _ => "a|b");

		await Assert.ThrowsExactlyAsync<ArgumentException>(() => repository.AddAsync("apple", CT));
	}

	[TestMethod]
	public async Task AddAsync_ValueContainsSeparator_RoundTrips()
	{
		var repository = CreateRepository(sortKeySelector: _ => "1");

		await repository.AddAsync("a|b", CT);

		Assert.IsTrue(await repository.ContainsAsync("a|b", CT));
		Assert.AreSequenceEqual(new[] { "a|b" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task AddAsync_EmptySortKey_RoundTrips()
	{
		var repository = CreateRepository(sortKeySelector: _ => "");

		await repository.AddAsync("apple", CT);

		Assert.IsTrue(await Partition.GetBlob("|apple").ExistsAsync(CT));
		Assert.AreSequenceEqual(new[] { "apple" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task AddAsync_LongValues_RoundTrip()
	{
		var repository = CreateRepository(sortKeySelector: v => v[..10]);
		var values = Enumerable.Range(0, 20).Select(i => new string((char)('a' + i), 100 + i)).ToArray();

		await repository.AddRangeAsync(values, CT);

		Assert.AreSequenceEqual(values, await ListAsync(repository));
		foreach (var value in values)
		{
			Assert.IsTrue(await repository.ContainsAsync(value, CT));
		}
	}

	// ListAsync

	[TestMethod]
	public async Task ListAsync_OrdersBySortKey()
	{
		var repository = CreateRepository();
		await repository.AddRangeAsync(["apple", "banana", "cherry"], CT);

		Assert.AreSequenceEqual(new[] { "banana", "cherry", "apple" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task ListAsync_SameSortKey_OrdersByValue()
	{
		var repository = CreateRepository(sortKeySelector: _ => "1");
		await repository.AddRangeAsync(["c", "a", "b"], CT);

		Assert.AreSequenceEqual(new[] { "a", "b", "c" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task ListAsync_InSubPartition_StripsPath()
	{
		var repository = CreateRepository(Partition.GetSubPartition("x").GetSubPartition("y"));
		await repository.AddRangeAsync(["apple", "banana"], CT);

		Assert.AreSequenceEqual(new[] { "banana", "apple" }, await ListAsync(repository));
		Assert.IsTrue(await Partition.GetBlob("x/y/1|banana").ExistsAsync(CT));
	}

	[TestMethod]
	public async Task ListAsync_InvalidBlobName_Throws()
	{
		var repository = CreateRepository();
		await Partition.GetBlob("invalid").WriteAsync(ReadOnlyMemory<byte>.Empty, null, default, CT);

		await Assert.ThrowsExactlyAsync<FormatException>(() => ListAsync(repository));
	}

	// DeleteAsync

	[TestMethod]
	public async Task DeleteAsync_WhenExists_RemovesAndReturnsTrue()
	{
		var repository = CreateRepository();
		await repository.AddRangeAsync(["apple", "banana"], CT);

		Assert.IsTrue(await repository.DeleteAsync("apple", CT));

		Assert.IsFalse(await repository.ContainsAsync("apple", CT));
		Assert.AreSequenceEqual(new[] { "banana" }, await ListAsync(repository));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_ReturnsFalse()
	{
		var repository = CreateRepository();

		Assert.IsFalse(await repository.DeleteAsync("apple", CT));
	}

	// ClearAsync

	[TestMethod]
	public async Task ClearAsync_RemovesAll()
	{
		var repository = CreateRepository();
		await repository.AddRangeAsync(["apple", "banana"], CT);

		await repository.ClearAsync(CT);

		Assert.IsEmpty(await ListAsync(repository));
	}

	[TestMethod]
	public async Task ClearAsync_KeepsOtherPartitions()
	{
		var repository1 = CreateRepository(Partition.GetSubPartition("x"));
		var repository2 = CreateRepository(Partition.GetSubPartition("y"));
		await repository1.AddAsync("apple", CT);
		await repository2.AddAsync("banana", CT);

		await repository1.ClearAsync(CT);

		Assert.IsEmpty(await ListAsync(repository1));
		Assert.AreSequenceEqual(new[] { "banana" }, await ListAsync(repository2));
	}
}
