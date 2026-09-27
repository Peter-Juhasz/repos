using PeterJuhasz.Repositories.AzureStorage;
using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.Tests.AzureStorage;

[TestClass]
[TestCategory("Azure")]
public sealed class AzureBlobPartitionTests(TestContext testContext) : IAsyncDisposable
{
	private static readonly byte[] Data = [1, 2, 3];

	/// <summary>
	/// Names with characters that have a meaning in URIs, so they only survive if they are encoded exactly once.
	/// </summary>
	private static readonly string[] SpecialNames = ["a b.bin", "a%20b.bin", "a#b.bin", "a?b.bin"];

	private DisposableContainer _container = null!;

	private CancellationToken CT => testContext.CancellationToken;

	[TestInitialize]
	public async Task InitializeAsync()
	{
		_container = await DisposableContainer.CreateAsync(CT);
	}

	public async ValueTask DisposeAsync()
	{
		if (_container != null)
		{
			await _container.DisposeAsync();
		}
	}

	private Task WriteAsync(IBlob blob) => blob.WriteAsync(Data, IBlob.AnyOrNoneConcurrencyToken, default, CT);

	private async Task<string[]> GetBlobNamesAsync(IBlobPartition partition) =>
		(await partition.GetBlobs(CT).ToListAsync(CT)).Select(b => b.Name).ToArray();

	// GetBlobs

	[TestMethod]
	public async Task GetBlobs_ReturnsSameNamesAsGetBlob()
	{
		var partition = _container.Client.GetPartition("some dir");
		foreach (var name in SpecialNames)
		{
			await WriteAsync(partition.GetBlob(name));
		}

		Assert.AreSequenceEqual(SpecialNames.Select(n => partition.GetBlob(n).Name).Order(StringComparer.Ordinal).ToArray(), (await GetBlobNamesAsync(partition)).Order(StringComparer.Ordinal).ToArray());
	}

	// ClearAsync

	[TestMethod]
	public async Task ClearAsync_DeletesBlobsWithSpecialNames()
	{
		var partition = _container.Client.GetPartition("some dir");
		foreach (var name in SpecialNames)
		{
			await WriteAsync(partition.GetBlob(name));
		}

		await partition.ClearAsync(CT);

		Assert.IsEmpty(await GetBlobNamesAsync(partition));
	}

	[TestMethod]
	public async Task ClearAsync_SubPartition_KeepsOtherBlobs()
	{
		var root = _container.Client.GetPartition();
		await WriteAsync(root.GetBlob("root.bin"));
		await WriteAsync(root.GetSubPartition("a").GetBlob("item.bin"));
		await WriteAsync(root.GetSubPartition("ab").GetBlob("item.bin"));

		await root.GetSubPartition("a").ClearAsync(CT);

		Assert.AreSequenceEqual([root.GetBlob("ab/item.bin").Name, root.GetBlob("root.bin").Name], await GetBlobNamesAsync(root));
	}
}
