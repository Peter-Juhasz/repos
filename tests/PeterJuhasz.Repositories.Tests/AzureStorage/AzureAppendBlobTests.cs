using Azure.Storage.Blobs.Specialized;
using PeterJuhasz.Repositories.AzureStorage;
using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.Tests.AzureStorage;

[TestClass]
[TestCategory("Azure")]
public sealed class AzureAppendBlobTests(TestContext testContext) : IAsyncDisposable
{
	private static readonly byte[] Data = [1, 2, 3];
	private static readonly byte[] OtherData = [4, 5, 6, 7];

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

	private AppendBlobClient GetClient(string name = "test.bin") => _container.Client.GetAppendBlobClient(name);

	private IAppendBlob CreateBlob(string name = "test.bin") => GetClient(name).AsBlob();

	private Task AppendAsync(IAppendBlob blob, byte[] data) => blob.AppendAsync(data, CT);

	private async Task<byte[]> ReadBytesAsync(IAppendBlob blob)
	{
		var result = await blob.OpenReadAsync(CT);
		Assert.IsNotNull(result);
		await using var stream = result.Value;
		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, CT);
		return buffer.ToArray();
	}

	// Name

	[TestMethod]
	public void Name_ReturnsUri()
	{
		var client = GetClient();

		var blob = new AzureAppendBlob(client);

		Assert.AreEqual(client.Uri.ToString(), blob.Name);
	}

	// Empty blob

	[TestMethod]
	public async Task NewBlob_DoesNotExist()
	{
		var blob = CreateBlob();

		Assert.IsNull(await blob.GetInfoAsync(CT));
		Assert.IsNull(await blob.OpenReadAsync(CT));
	}

	// Missing container

	[TestMethod]
	public async Task MissingContainer_DoesNotExist()
	{
		await using var container = await DisposableContainer.CreateAsync(CT, create: false);
		var blob = container.Client.GetAppendBlobClient("test.bin").AsBlob();

		Assert.IsNull(await blob.GetInfoAsync(CT));
		Assert.IsNull(await blob.OpenReadAsync(CT));
		await blob.DeleteAsync(CT);
	}

	[TestMethod]
	public async Task MissingContainer_AppendAsync_CreatesContainer()
	{
		await using var container = await DisposableContainer.CreateAsync(CT, create: false);
		var blob = new AzureAppendBlob(container.Client.GetAppendBlobClient("test.bin"), "application/jsonl");

		await AppendAsync(blob, Data);

		Assert.IsTrue((await container.Client.ExistsAsync(CT)).Value);
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
		Assert.AreEqual("application/jsonl", (await blob.GetInfoAsync(CT))?.MediaType);
	}

	// AppendAsync

	[TestMethod]
	public async Task AppendAsync_WhenNotExists_Creates()
	{
		var blob = CreateBlob();

		await AppendAsync(blob, Data);

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.IsFalse(string.IsNullOrEmpty(info.ConcurrencyToken));
		var now = DateTimeOffset.UtcNow;
		Assert.IsTrue(info.LastModified > now.AddMinutes(-5) && info.LastModified < now.AddMinutes(5));
	}

	[TestMethod]
	public async Task AppendAsync_WhenExists_Appends()
	{
		var blob = CreateBlob();
		await AppendAsync(blob, Data);

		await AppendAsync(blob, OtherData);

		Assert.AreSequenceEqual(Data.Concat(OtherData).ToArray(), await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task AppendAsync_ChangesToken()
	{
		var blob = CreateBlob();
		await AppendAsync(blob, Data);
		var info = await blob.GetInfoAsync(CT);

		await AppendAsync(blob, OtherData);

		var newInfo = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.IsNotNull(newInfo);
		Assert.AreNotEqual(info.ConcurrencyToken, newInfo.ConcurrencyToken);
	}

	[TestMethod]
	public async Task AppendAsync_Concurrent_AllAppended()
	{
		var blob = CreateBlob();

		await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => AppendAsync(blob, [(byte)i]), CT)));

		var data = await ReadBytesAsync(blob);
		Assert.AreSequenceEqual(Enumerable.Range(0, 8).Select(i => (byte)i).ToArray(), data.Order().ToArray());
	}

	[TestMethod]
	public async Task AppendAsync_WhenBlobDeletedExternally_Recreates()
	{
		var blob = CreateBlob();
		await AppendAsync(blob, Data);
		await GetClient().DeleteAsync(cancellationToken: CT);

		await AppendAsync(blob, OtherData);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	// OpenReadAsync / GetInfoAsync

	[TestMethod]
	public async Task OpenReadAsync_ReturnsStreamAndInfo()
	{
		var blob = CreateBlob();
		await AppendAsync(blob, Data);
		var info = await blob.GetInfoAsync(CT);

		var result = await blob.OpenReadAsync(CT);

		Assert.IsNotNull(result);
		Assert.IsNotNull(info);
		Assert.AreEqual(info.ConcurrencyToken, result.Info.ConcurrencyToken);
		Assert.AreEqual(info.MediaType, result.Info.MediaType);
		await using var stream = result.Value;
		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, CT);
		Assert.AreSequenceEqual(Data, buffer.ToArray());
	}

	[TestMethod]
	public async Task GetInfoAsync_ReturnsDefaultMediaType()
	{
		var blob = CreateBlob();
		await AppendAsync(blob, Data);

		var info = await blob.GetInfoAsync(CT);

		Assert.IsNotNull(info);
		Assert.AreEqual("application/octet-stream", info.MediaType);
	}

	[TestMethod]
	[DataRow("application/jsonl")]
	[DataRow("text/plain")]
	public async Task GetInfoAsync_ReturnsConstructorMediaType(string contentType)
	{
		var blob = new AzureAppendBlob(GetClient(), contentType);
		await AppendAsync(blob, Data);

		var info = await blob.GetInfoAsync(CT);

		Assert.IsNotNull(info);
		Assert.AreEqual(contentType, info.MediaType);
	}

	// DeleteAsync

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_DoesNotThrow()
	{
		var blob = CreateBlob();

		await blob.DeleteAsync(CT);

		Assert.IsNull(await blob.GetInfoAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenExists_RemovesBlob()
	{
		var blob = CreateBlob();
		await AppendAsync(blob, Data);

		await blob.DeleteAsync(CT);

		Assert.IsNull(await blob.GetInfoAsync(CT));
		Assert.IsNull(await blob.OpenReadAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_ThenAppend_StartsFresh()
	{
		var blob = CreateBlob();
		await AppendAsync(blob, Data);
		await blob.DeleteAsync(CT);

		await AppendAsync(blob, OtherData);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	// Line collection repository

	[TestMethod]
	public async Task AsJsonLineCollectionRepository_RoundTrips()
	{
		var blob = CreateBlob();
		var repository = blob.AsJsonLineCollectionRepository<Item>(System.Text.Json.JsonSerializerOptions.Web);

		await repository.AddAsync(new Item("a"), CT);
		await repository.AddAsync(new Item("b"), CT);

		var result = await repository.ListWithVersionAsync(CT);
		Assert.AreSequenceEqual(new[] { new Item("a"), new Item("b") }, result.Value.ToArray());
		Assert.AreEqual((await blob.GetInfoAsync(CT))?.ConcurrencyToken, result.ETag);
	}

	private sealed record Item(string Name);
}
