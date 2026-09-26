using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;

namespace PeterJuhasz.Repositories.Tests.InMemory;

[TestClass]
public class InMemoryAppendBlobTests(TestContext testContext)
{
	private static readonly byte[] Data = [1, 2, 3];
	private static readonly byte[] OtherData = [4, 5, 6, 7];

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryAppendBlob CreateBlob() => new("test", _time);

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
	public void Name_ReturnsConstructorValue()
	{
		var blob = new InMemoryAppendBlob("some/name", _time);

		Assert.AreEqual("some/name", blob.Name);
	}

	// Empty blob

	[TestMethod]
	public async Task NewBlob_DoesNotExist()
	{
		var blob = CreateBlob();

		Assert.IsNull(await blob.GetInfoAsync(CT));
		Assert.IsNull(await blob.OpenReadAsync(CT));
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
		Assert.AreEqual(_time.GetUtcNow(), info.LastModified);
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
	public async Task AppendAsync_Empty_CreatesEmptyBlob()
	{
		var blob = CreateBlob();

		await AppendAsync(blob, []);

		Assert.IsNotNull(await blob.GetInfoAsync(CT));
		Assert.IsEmpty(await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task AppendAsync_CopiesInputBuffer()
	{
		var blob = CreateBlob();
		var buffer = Data.ToArray();

		await AppendAsync(blob, buffer);
		buffer[0] = 99;

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task AppendAsync_ChangesTokenAndLastModified()
	{
		var blob = CreateBlob();
		await AppendAsync(blob, Data);
		var info = await blob.GetInfoAsync(CT);
		_time.Advance(TimeSpan.FromMinutes(1));

		await AppendAsync(blob, OtherData);

		var newInfo = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.IsNotNull(newInfo);
		Assert.AreNotEqual(info.ConcurrencyToken, newInfo.ConcurrencyToken);
		Assert.AreEqual(_time.GetUtcNow(), newInfo.LastModified);
	}

	[TestMethod]
	public async Task AppendAsync_Concurrent_AllAppended()
	{
		var blob = CreateBlob();

		await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => AppendAsync(blob, [(byte)i]), CT)));

		var data = await ReadBytesAsync(blob);
		Assert.AreSequenceEqual(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(), data.Order().ToArray());
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
		Assert.AreEqual(info, result.Info);
		await result.Value.DisposeAsync();
	}

	[TestMethod]
	public async Task OpenReadAsync_StreamIsSnapshot()
	{
		var blob = CreateBlob();
		await AppendAsync(blob, Data);

		var result = await blob.OpenReadAsync(CT);
		Assert.IsNotNull(result);
		await AppendAsync(blob, OtherData);

		await using var stream = result.Value;
		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, CT);
		Assert.AreSequenceEqual(Data, buffer.ToArray());
	}

	[TestMethod]
	public async Task GetInfoAsync_NoMediaType_ReturnsNull()
	{
		var blob = CreateBlob();
		await AppendAsync(blob, Data);

		var info = await blob.GetInfoAsync(CT);

		Assert.IsNotNull(info);
		Assert.IsNull(info.MediaType);
	}

	[TestMethod]
	public async Task GetInfoAsync_ReturnsConstructorMediaType()
	{
		var blob = new InMemoryAppendBlob("test", _time, "application/jsonl");
		await AppendAsync(blob, Data);

		var info = await blob.GetInfoAsync(CT);

		Assert.IsNotNull(info);
		Assert.AreEqual("application/jsonl", info.MediaType);
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
