using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;
using System.Text.RegularExpressions;

namespace PeterJuhasz.Repositories.Tests.InMemory;

[TestClass]
public class InMemoryBlobPartitionTests(TestContext testContext)
{
	private static readonly byte[] Data = [1, 2, 3];

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlobPartition CreatePartition() => new(_time);

	private Task WriteAsync(IBlob blob) => blob.WriteAsync(Data, IBlob.AnyOrNoneConcurrencyToken, default, CT);

	/// <summary>
	/// Names of the listed blobs, without the root prefix (<c>memory://{id}/</c>).
	/// </summary>
	private async Task<string[]> GetBlobNamesAsync(IBlobPartition partition) =>
		(await partition.GetBlobs(CT).ToListAsync(CT)).Select(b => WithoutRoot(b.Name)).ToArray();

	private static string WithoutRoot(string name) => Regex.Replace(name, "^memory://[0-9]+/", "");

	// Path

	[TestMethod]
	public void Path_Root_IsNull()
	{
		Assert.IsNull(CreatePartition().Path);
	}

	[TestMethod]
	public void Path_SubPartition_IsJoined()
	{
		var partition = CreatePartition().GetSubPartition("a").GetSubPartition("b");

		Assert.AreEqual("a/b", partition.Path);
	}

	// GetBlob / GetAppendBlob

	[TestMethod]
	public void GetBlob_NameIncludesRootAndPath()
	{
		var partition = CreatePartition();

		var name = partition.GetBlob("item.json").Name;

		Assert.MatchesRegex("^memory://[0-9]+/item.json$", name);
		var root = name[..^"item.json".Length];
		Assert.AreEqual($"{root}a/b/item.json", partition.GetSubPartition("a").GetSubPartition("b").GetBlob("item.json").Name);
	}

	[TestMethod]
	public void GetBlob_SeparateRootPartitions_HaveDifferentNames()
	{
		Assert.AreNotEqual(CreatePartition().GetBlob("item.json").Name, CreatePartition().GetBlob("item.json").Name);
	}

	[TestMethod]
	public void GetBlob_SameName_ReturnsSameInstance()
	{
		var partition = CreatePartition();

		Assert.AreSame(partition.GetBlob("item.json"), partition.GetBlob("item.json"));
	}

	[TestMethod]
	public async Task GetBlob_DataIsSharedAcrossCalls()
	{
		var partition = CreatePartition();

		await WriteAsync(partition.GetBlob("item.json"));

		var result = await partition.GetBlob("item.json").ReadAsync(CT);
		Assert.IsNotNull(result);
		Assert.AreSequenceEqual(Data, result.Value.ToArray());
	}

	[TestMethod]
	public void GetBlob_ViaSubPartitionOrFullName_ReturnsSameInstance()
	{
		var partition = CreatePartition();

		Assert.AreSame(partition.GetBlob("a/item.json"), partition.GetSubPartition("a").GetBlob("item.json"));
	}

	[TestMethod]
	public void GetBlob_SubPartitionsWithSamePath_ShareBlobs()
	{
		var partition = CreatePartition();

		Assert.AreSame(partition.GetSubPartition("a").GetBlob("item.json"), partition.GetSubPartition("a").GetBlob("item.json"));
	}

	[TestMethod]
	public void GetBlob_DifferentPartitions_ReturnsDifferentInstances()
	{
		var partition = CreatePartition();

		Assert.AreNotSame(partition.GetSubPartition("a").GetBlob("item.json"), partition.GetSubPartition("b").GetBlob("item.json"));
	}

	[TestMethod]
	public void GetBlob_SeparateRootPartitions_AreIndependent()
	{
		Assert.AreNotSame(CreatePartition().GetBlob("item.json"), CreatePartition().GetBlob("item.json"));
	}

	[TestMethod]
	public void GetAppendBlob_SameName_ReturnsSameInstance()
	{
		var partition = CreatePartition();

		var blob = partition.GetAppendBlob("log.jsonl");

		Assert.AreSame(blob, partition.GetAppendBlob("log.jsonl"));
		Assert.AreEqual("log.jsonl", WithoutRoot(blob.Name));
	}

	[TestMethod]
	public void GetAppendBlob_WhenBlobExistsWithName_Throws()
	{
		var partition = CreatePartition();
		partition.GetBlob("item");

		Assert.ThrowsExactly<InvalidOperationException>(() => partition.GetAppendBlob("item"));
	}

	[TestMethod]
	public void GetBlob_WhenAppendBlobExistsWithName_Throws()
	{
		var partition = CreatePartition();
		partition.GetAppendBlob("item");

		Assert.ThrowsExactly<InvalidOperationException>(() => partition.GetBlob("item"));
	}

	[TestMethod]
	public void GetBlob_Concurrent_ReturnsSameInstance()
	{
		var partition = CreatePartition();

		var blobs = Enumerable.Range(0, 32).AsParallel().Select(_ => partition.GetBlob("item.json")).ToArray();

		Assert.IsTrue(blobs.All(b => ReferenceEquals(b, blobs[0])));
	}

	// GetBlobs

	[TestMethod]
	public async Task GetBlobs_Empty_ReturnsNothing()
	{
		Assert.IsEmpty(await GetBlobNamesAsync(CreatePartition()));
	}

	[TestMethod]
	public async Task GetBlobs_SkipsNonExistentBlobs()
	{
		var partition = CreatePartition();
		partition.GetBlob("a.json");
		await WriteAsync(partition.GetBlob("b.json"));

		Assert.AreSequenceEqual(["b.json"], await GetBlobNamesAsync(partition));
	}

	[TestMethod]
	public async Task GetBlobs_ReturnsBlobsOrderedByName()
	{
		var partition = CreatePartition();
		await WriteAsync(partition.GetBlob("c.json"));
		await WriteAsync(partition.GetBlob("a.json"));
		await WriteAsync(partition.GetBlob("b.json"));

		Assert.AreSequenceEqual(["a.json", "b.json", "c.json"], await GetBlobNamesAsync(partition));
	}

	[TestMethod]
	public async Task GetBlobs_IncludesSubPartitions()
	{
		var partition = CreatePartition();
		await WriteAsync(partition.GetBlob("root.json"));
		await WriteAsync(partition.GetSubPartition("a").GetBlob("item.json"));
		await WriteAsync(partition.GetSubPartition("a").GetSubPartition("b").GetBlob("item.json"));

		Assert.AreSequenceEqual(["a/b/item.json", "a/item.json", "root.json"], await GetBlobNamesAsync(partition));
		Assert.AreSequenceEqual(["a/b/item.json", "a/item.json"], await GetBlobNamesAsync(partition.GetSubPartition("a")));
	}

	[TestMethod]
	public async Task GetBlobs_ExcludesSiblingWithSamePrefix()
	{
		var partition = CreatePartition();
		await WriteAsync(partition.GetSubPartition("a").GetBlob("item.json"));
		await WriteAsync(partition.GetSubPartition("ab").GetBlob("item.json"));
		await WriteAsync(partition.GetBlob("a.json"));

		Assert.AreSequenceEqual(["a/item.json"], await GetBlobNamesAsync(partition.GetSubPartition("a")));
	}

	[TestMethod]
	public async Task GetBlobs_ExcludesAppendBlobs()
	{
		var partition = CreatePartition();
		await partition.GetAppendBlob("log.jsonl").AppendAsync(Data, CT);

		Assert.IsEmpty(await GetBlobNamesAsync(partition));
	}

	[TestMethod]
	public async Task GetBlobs_ReturnsSameInstances()
	{
		var partition = CreatePartition();
		var blob = partition.GetBlob("item.json");
		await WriteAsync(blob);

		Assert.AreSame(blob, await partition.GetBlobs(CT).SingleAsync(CT));
	}

	// ClearAsync

	[TestMethod]
	public async Task ClearAsync_Empty_DoesNothing()
	{
		var partition = CreatePartition();

		await partition.ClearAsync(CT);

		Assert.IsEmpty(await GetBlobNamesAsync(partition));
	}

	[TestMethod]
	public async Task ClearAsync_DeletesBlobsAndAppendBlobs()
	{
		var partition = CreatePartition();
		var blob = partition.GetBlob("item.json");
		var appendBlob = partition.GetAppendBlob("log.jsonl");
		await WriteAsync(blob);
		await appendBlob.AppendAsync(Data, CT);

		await partition.ClearAsync(CT);

		Assert.IsFalse(await blob.ExistsAsync(CT));
		Assert.IsNull(await appendBlob.GetInfoAsync(CT));
		Assert.IsEmpty(await GetBlobNamesAsync(partition));
	}

	[TestMethod]
	public async Task ClearAsync_IncludesSubPartitions()
	{
		var partition = CreatePartition();
		await WriteAsync(partition.GetSubPartition("a").GetBlob("item.json"));

		await partition.ClearAsync(CT);

		Assert.IsFalse(await partition.GetSubPartition("a").GetBlob("item.json").ExistsAsync(CT));
	}

	[TestMethod]
	public async Task ClearAsync_SubPartition_KeepsOtherBlobs()
	{
		var partition = CreatePartition();
		await WriteAsync(partition.GetBlob("root.json"));
		await WriteAsync(partition.GetSubPartition("a").GetBlob("item.json"));
		await WriteAsync(partition.GetSubPartition("ab").GetBlob("item.json"));

		await partition.GetSubPartition("a").ClearAsync(CT);

		Assert.AreSequenceEqual(["ab/item.json", "root.json"], await GetBlobNamesAsync(partition));
	}

	[TestMethod]
	public async Task ClearAsync_ExistingInstancesRemainConnected()
	{
		var partition = CreatePartition();
		var blob = partition.GetBlob("item.json");
		await WriteAsync(blob);
		await partition.ClearAsync(CT);

		await WriteAsync(blob);

		Assert.IsTrue(await partition.GetBlob("item.json").ExistsAsync(CT));
	}

	// Repositories

	[TestMethod]
	public async Task AppendBlob_AsJsonLineCollectionRepository_PersistsAcrossLookups()
	{
		var partition = CreatePartition().GetSubPartition("logs");

		await partition.GetAppendBlob("log.jsonl").AsJsonLineCollectionRepository<string>(System.Text.Json.JsonSerializerOptions.Web).AddAsync("a", CT);
		await partition.GetAppendBlob("log.jsonl").AsJsonLineCollectionRepository<string>(System.Text.Json.JsonSerializerOptions.Web).AddAsync("b", CT);

		var items = await partition.GetAppendBlob("log.jsonl").AsJsonLineCollectionRepository<string>(System.Text.Json.JsonSerializerOptions.Web).ListAsync(CT);
		Assert.AreSequenceEqual(["a", "b"], items);
	}
}
