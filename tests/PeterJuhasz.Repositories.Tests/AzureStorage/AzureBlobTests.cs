using PeterJuhasz.Repositories.AzureStorage;
using PeterJuhasz.Repositories.Blobs;

namespace PeterJuhasz.Repositories.Tests.AzureStorage;

[TestClass]
[TestCategory("Azure")]
public sealed class AzureBlobTests(TestContext testContext) : IAsyncDisposable
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

	private IBlob CreateBlob(string name = "test.bin", WriteMode writeMode = WriteMode.BufferStreamUpload) =>
		_container.GetBlobClient(name).AsBlob(writeMode);

	private Task<string> WriteAsync(IBlob blob, byte[] data, string? concurrencyToken, IBlob.WriteBlobInfo options = default) =>
		blob.WriteAsync(data, concurrencyToken, options, CT);

	private async Task<byte[]> ReadBytesAsync(IBlob blob)
	{
		var result = await blob.ReadAsync(CT);
		Assert.IsNotNull(result);
		return result.Value.ToArray();
	}

	private async Task WriteStreamAsync(IBlob blob, byte[] data, string? concurrencyToken, IBlob.WriteBlobInfo options = default)
	{
		await using var stream = await blob.OpenWriteAsync(concurrencyToken, options, CT);
		await stream.WriteAsync(data, CT);
	}

	private static void AssertRecent(DateTimeOffset value)
	{
		var now = DateTimeOffset.UtcNow;
		Assert.IsTrue(value > now.AddMinutes(-5) && value < now.AddMinutes(5), $"Expected '{value:O}' to be close to '{now:O}'.");
	}

	private static void AssertNoMetadata(IBlob.ReadBlobInfo info) =>
		Assert.IsTrue(info.Metadata is null or { Count: 0 }, "Expected no metadata.");

	// Name / Client

	[TestMethod]
	public void Name_ReturnsDecodedUri()
	{
		var client = _container.GetBlobClient("some dir/a b.bin");

		var blob = new AzureBlob(client);

		Assert.EndsWith($"/{_container.Name}/some dir/a b.bin", blob.Name);
	}

	[TestMethod]
	public void AsBlob_WrapsClient()
	{
		var client = _container.GetBlobClient("test.bin");

		var blob = client.AsBlob();

		Assert.IsInstanceOfType<AzureBlob>(blob);
		Assert.AreSame(client, ((AzureBlob)blob).Client);
	}

	// Empty blob

	[TestMethod]
	public async Task NewBlob_DoesNotExist()
	{
		var blob = CreateBlob();

		Assert.IsFalse(await blob.ExistsAsync(CT));
		Assert.IsNull(await blob.GetInfoAsync(CT));
		Assert.IsNull(await blob.ReadAsync(CT));
		Assert.IsNull(await blob.OpenReadAsync(CT));
	}

	// Missing container

	[TestMethod]
	public async Task MissingContainer_DoesNotExist()
	{
		await using var container = await DisposableContainer.CreateAsync(CT, create: false);
		var blob = container.GetBlobClient("test.bin").AsBlob();

		Assert.IsFalse(await blob.ExistsAsync(CT));
		Assert.IsNull(await blob.GetInfoAsync(CT));
		Assert.IsNull(await blob.ReadAsync(CT));
		Assert.IsNull(await blob.OpenReadAsync(CT));
		Assert.IsFalse(await blob.DeleteIfExistsAsync(CT));
		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.DeleteAsync("\"token\"", CT));
		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.SetMetadataAsync("\"token\"", null, CT));
		await Assert.ThrowsExactlyAsync<NotFoundException>(() => blob.DeleteAsync(IBlob.AnyConcurrencyToken, CT));
		await Assert.ThrowsExactlyAsync<NotFoundException>(() => blob.SetMetadataAsync(IBlob.AnyConcurrencyToken, null, CT));
	}

	[TestMethod]
	public async Task MissingContainer_WriteAsync_CreatesContainer()
	{
		await using var container = await DisposableContainer.CreateAsync(CT, create: false);
		var blob = container.GetBlobClient("test.bin").AsBlob();

		await WriteAsync(blob, Data, null);

		Assert.IsTrue((await container.Client.ExistsAsync(CT)).Value);
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task MissingContainer_OpenWriteAsync_CreatesContainer(WriteMode writeMode)
	{
		await using var container = await DisposableContainer.CreateAsync(CT, create: false);
		var blob = container.GetBlobClient("test.bin").AsBlob(writeMode);

		await WriteStreamAsync(blob, Data, null);

		Assert.IsTrue((await container.Client.ExistsAsync(CT)).Value);
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	// WriteAsync

	[TestMethod]
	public async Task WriteAsync_Create_StoresDataAndInfo()
	{
		var blob = CreateBlob();
		var metadata = new Dictionary<string, string> { ["key"] = "value" };

		var token = await WriteAsync(blob, Data, null, new(ContentEncoding: "gzip", MediaType: "application/json", Metadata: metadata));

		Assert.IsFalse(string.IsNullOrEmpty(token));
		Assert.IsTrue(await blob.ExistsAsync(CT));
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));

		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.AreEqual(token, info.ConcurrencyToken);
		AssertRecent(info.LastModified);
		Assert.AreEqual("gzip", info.ContentEncoding);
		Assert.AreEqual("application/json", info.MediaType);
		Assert.IsNotNull(info.Metadata);
		Assert.AreEqual("value", info.Metadata["key"]);
	}

	[TestMethod]
	public async Task WriteAsync_Empty_CreatesEmptyBlob()
	{
		var blob = CreateBlob();

		await WriteAsync(blob, [], null);

		Assert.IsTrue(await blob.ExistsAsync(CT));
		Assert.IsEmpty(await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task WriteAsync_Create_WhenExists_Throws()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteAsync(blob, OtherData, null));

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task WriteAsync_WithToken_WhenNotExists_Throws()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteAsync(blob, Data, "\"token\""));

		Assert.IsFalse(await blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task WriteAsync_WithStaleToken_Throws()
	{
		var blob = CreateBlob();
		var staleToken = await WriteAsync(blob, Data, null);
		await WriteAsync(blob, Data, staleToken);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteAsync(blob, OtherData, staleToken));

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task WriteAsync_WithCurrentToken_Overwrites()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null, new(MediaType: "text/plain", Metadata: new Dictionary<string, string> { ["a"] = "b" }));

		var newToken = await WriteAsync(blob, OtherData, token, new(MediaType: "application/octet-stream"));

		Assert.AreNotEqual(token, newToken);
		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));

		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.AreEqual(newToken, info.ConcurrencyToken);
		Assert.AreEqual("application/octet-stream", info.MediaType);
		AssertNoMetadata(info);
	}

	[TestMethod]
	public async Task WriteAsync_ConcurrentWritersWithSameToken_OnlyOneSucceeds()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
		{
			try
			{
				await WriteAsync(blob, OtherData, token);
				return true;
			}
			catch (ConflictException)
			{
				return false;
			}
		})));

		Assert.AreEqual(1, results.Count(r => r));
	}

	[TestMethod]
	public async Task WriteAsync_AnyOrNoneToken_WhenNotExists_Creates()
	{
		var blob = CreateBlob();

		var token = await WriteAsync(blob, Data, IBlob.AnyOrNoneConcurrencyToken);

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
		Assert.AreEqual(token, (await blob.GetInfoAsync(CT))?.ConcurrencyToken);
	}

	[TestMethod]
	public async Task WriteAsync_AnyOrNoneToken_WhenExists_Overwrites()
	{
		var blob = CreateBlob();
		var oldToken = await WriteAsync(blob, Data, null);

		var token = await WriteAsync(blob, OtherData, IBlob.AnyOrNoneConcurrencyToken);

		Assert.AreNotEqual(oldToken, token);
		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task WriteAsync_AnyToken_WhenNotExists_Throws()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteAsync(blob, Data, IBlob.AnyConcurrencyToken));

		Assert.IsFalse(await blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task WriteAsync_AnyToken_WhenExists_Overwrites()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);

		await WriteAsync(blob, OtherData, IBlob.AnyConcurrencyToken);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	// ReadAsync / OpenReadAsync / GetInfoAsync

	[TestMethod]
	public async Task ReadAsync_ReturnsDataAndInfo()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null, new(MediaType: "text/plain"));

		var result = await blob.ReadAsync(CT);

		Assert.IsNotNull(result);
		Assert.AreSequenceEqual(Data, result.Value.ToArray());
		Assert.AreEqual(token, result.Info.ConcurrencyToken);
		Assert.AreEqual("text/plain", result.Info.MediaType);
	}

	[TestMethod]
	public async Task ReadAsync_DataHasMediaType()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null, new(MediaType: "application/json"));

		var result = await blob.ReadAsync(CT);

		Assert.IsNotNull(result);
		Assert.AreEqual("application/json", result.Value.MediaType);
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task ReadAsync_AfterOpenWrite_DataHasMediaType(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);
		await WriteStreamAsync(blob, Data, null, new(MediaType: "text/plain"));

		var result = await blob.ReadAsync(CT);

		Assert.IsNotNull(result);
		Assert.AreEqual("text/plain", result.Value.MediaType);
	}

	[TestMethod]
	public async Task OpenReadAsync_ReturnsStreamAndInfo()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null, new(ContentEncoding: "gzip", MediaType: "text/plain", Metadata: new Dictionary<string, string> { ["a"] = "b" }));

		var result = await blob.OpenReadAsync(CT);

		Assert.IsNotNull(result);
		Assert.AreEqual(token, result.Info.ConcurrencyToken);
		Assert.AreEqual("gzip", result.Info.ContentEncoding);
		Assert.AreEqual("text/plain", result.Info.MediaType);
		Assert.IsNotNull(result.Info.Metadata);
		Assert.AreEqual("b", result.Info.Metadata["a"]);
		await using var stream = result.Value;
		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, CT);
		Assert.AreSequenceEqual(Data, buffer.ToArray());
	}

	[TestMethod]
	public async Task TransformAsync_PreservesMediaType()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null, new(MediaType: "application/json"));

		await blob.TransformAsync(data => BinaryData.FromBytes(OtherData, data!.MediaType), CT);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.AreEqual("application/json", info.MediaType);
	}

	// SetMetadataAsync

	[TestMethod]
	public async Task SetMetadataAsync_WhenNotExists_ThrowsConflict()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.SetMetadataAsync("\"token\"", new Dictionary<string, string>(), CT));
	}

	[TestMethod]
	public async Task SetMetadataAsync_WithStaleToken_Throws()
	{
		var blob = CreateBlob();
		var staleToken = await WriteAsync(blob, Data, null);
		await WriteAsync(blob, Data, staleToken);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.SetMetadataAsync(staleToken, new Dictionary<string, string> { ["a"] = "b" }, CT));

		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		AssertNoMetadata(info);
	}

	[TestMethod]
	public async Task SetMetadataAsync_ReplacesMetadataAndKeepsContent()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null, new(ContentEncoding: "gzip", MediaType: "text/plain", Metadata: new Dictionary<string, string> { ["old"] = "1" }));

		var newToken = await blob.SetMetadataAsync(token, new Dictionary<string, string> { ["new"] = "2" }, CT);

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.AreEqual(newToken, info.ConcurrencyToken);
		Assert.AreEqual("gzip", info.ContentEncoding);
		Assert.AreEqual("text/plain", info.MediaType);
		Assert.IsNotNull(info.Metadata);
		Assert.HasCount(1, info.Metadata);
		Assert.AreEqual("2", info.Metadata["new"]);
	}

	[TestMethod]
	public async Task SetMetadataAsync_Null_ClearsMetadata()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null, new(Metadata: new Dictionary<string, string> { ["a"] = "b" }));

		await blob.SetMetadataAsync(token, null, CT);

		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		AssertNoMetadata(info);
	}

	[TestMethod]
	public async Task SetMetadataAsync_ChangesTokenAndOldTokenIsStale()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		var newToken = await blob.SetMetadataAsync(token, new Dictionary<string, string> { ["a"] = "b" }, CT);

		Assert.AreNotEqual(token, newToken);
		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteAsync(blob, OtherData, token));
		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.SetMetadataAsync(token, null, CT));
		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.DeleteAsync(token, CT));
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task SetMetadataAsync_ReturnedTokenCanBeUsedForWrite()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		var newToken = await blob.SetMetadataAsync(token, new Dictionary<string, string> { ["a"] = "b" }, CT);
		await WriteAsync(blob, OtherData, newToken);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task SetMetadataAsync_KeepsDataMediaType()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null, new(MediaType: "application/json"));

		await blob.SetMetadataAsync(token, new Dictionary<string, string> { ["a"] = "b" }, CT);

		var result = await blob.ReadAsync(CT);
		Assert.IsNotNull(result);
		Assert.AreEqual("application/json", result.Value.MediaType);
	}

	[TestMethod]
	public async Task SetMetadataAsync_AnyToken_WhenExists_Updates()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);

		await blob.SetMetadataAsync(IBlob.AnyConcurrencyToken, new Dictionary<string, string> { ["a"] = "b" }, CT);

		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.IsNotNull(info.Metadata);
		Assert.AreEqual("b", info.Metadata["a"]);
	}

	[TestMethod]
	public async Task SetMetadataAsync_AnyToken_WhenNotExists_Throws()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => blob.SetMetadataAsync(IBlob.AnyConcurrencyToken, null, CT));
	}

	// OpenWriteAsync

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_Create_CommitsOnDisposeAsync(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);
		var metadata = new Dictionary<string, string> { ["key"] = "value" };

		await WriteStreamAsync(blob, Data, null, new(ContentEncoding: "gzip", MediaType: "text/plain", Metadata: metadata));

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.AreEqual("gzip", info.ContentEncoding);
		Assert.AreEqual("text/plain", info.MediaType);
		Assert.IsNotNull(info.Metadata);
		Assert.AreEqual("value", info.Metadata["key"]);
	}

	[TestMethod]
	public async Task OpenWriteAsync_OpenWrite_CommitsOnDispose()
	{
		var blob = CreateBlob(writeMode: WriteMode.OpenWrite);

		var stream = await blob.OpenWriteAsync(null, default, CT);
		await stream.WriteAsync(Data, CT);
		stream.Dispose();

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_Buffered_Dispose_ThrowsNotSupported(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);

		var stream = await blob.OpenWriteAsync(null, default, CT);
		await stream.WriteAsync(Data, CT);

		Assert.ThrowsExactly<NotSupportedException>(stream.Dispose);
		Assert.IsFalse(await blob.ExistsAsync(CT));

		await stream.DisposeAsync();
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_DisposeTwice_CommitsOnce(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);

		var stream = await blob.OpenWriteAsync(null, default, CT);
		await stream.WriteAsync(Data, CT);
		await stream.DisposeAsync();
		var info = await blob.GetInfoAsync(CT);
		stream.Dispose();

		Assert.AreEqual(info?.ConcurrencyToken, (await blob.GetInfoAsync(CT))?.ConcurrencyToken);
	}

	[TestMethod]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_Buffered_NotVisibleBeforeCommit(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);

		var stream = await blob.OpenWriteAsync(null, default, CT);
		await stream.WriteAsync(Data, CT);
		Assert.IsFalse(await blob.ExistsAsync(CT));
		await stream.DisposeAsync();

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_LargeData_RoundTrips(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);
		var data = new byte[5 * 1024 * 1024 + 123];
		Random.Shared.NextBytes(data);

		await WriteStreamAsync(blob, data, null);

		Assert.AreSequenceEqual(data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_Create_WhenExists_Throws(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);
		await WriteAsync(blob, Data, null);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteStreamAsync(blob, OtherData, null));

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_WithToken_WhenNotExists_Throws(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteStreamAsync(blob, Data, "\"token\""));

		Assert.IsFalse(await blob.ExistsAsync(CT));
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_WithStaleToken_Throws(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);
		var staleToken = await WriteAsync(blob, Data, null);
		await WriteAsync(blob, Data, staleToken);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteStreamAsync(blob, OtherData, staleToken));

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_WithCurrentToken_Overwrites(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);
		var token = await WriteAsync(blob, Data, null);

		await WriteStreamAsync(blob, OtherData, token);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
		Assert.AreNotEqual(token, (await blob.GetInfoAsync(CT))?.ConcurrencyToken);
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_AnyOrNoneToken_WhenNotExists_Creates(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);

		await WriteStreamAsync(blob, Data, IBlob.AnyOrNoneConcurrencyToken);

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_AnyOrNoneToken_WhenExists_Overwrites(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);
		await WriteAsync(blob, Data, null);

		await WriteStreamAsync(blob, OtherData, IBlob.AnyOrNoneConcurrencyToken);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_AnyToken_WhenNotExists_Throws(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteStreamAsync(blob, Data, IBlob.AnyConcurrencyToken));

		Assert.IsFalse(await blob.ExistsAsync(CT));
	}

	[TestMethod]
	[DataRow(WriteMode.OpenWrite)]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_AnyToken_WhenExists_Overwrites(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);
		await WriteAsync(blob, Data, null);

		await WriteStreamAsync(blob, OtherData, IBlob.AnyConcurrencyToken);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_BlobChangedBeforeCommit_ThrowsOnDispose(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);
		var token = await WriteAsync(blob, Data, null);

		var stream = await blob.OpenWriteAsync(token, default, CT);
		await stream.WriteAsync(OtherData, CT);
		await WriteAsync(blob, Data, token);

		await Assert.ThrowsExactlyAsync<ConflictException>(async () => await stream.DisposeAsync());
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	[DataRow(WriteMode.BufferStreamUpload)]
	[DataRow(WriteMode.BufferStreamStageBlock)]
	public async Task OpenWriteAsync_BlobCreatedBeforeCommit_ThrowsOnDispose(WriteMode writeMode)
	{
		var blob = CreateBlob(writeMode: writeMode);

		var stream = await blob.OpenWriteAsync(null, default, CT);
		await stream.WriteAsync(OtherData, CT);
		await WriteAsync(blob, Data, null);

		await Assert.ThrowsExactlyAsync<ConflictException>(async () => await stream.DisposeAsync());
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	// DeleteAsync

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_ThrowsConflict()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.DeleteAsync("\"token\"", CT));
	}

	[TestMethod]
	public async Task DeleteAsync_OldTokenAfterDelete_ThrowsConflict()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);
		await blob.DeleteAsync(token, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.DeleteAsync(token, CT));
		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteAsync(blob, Data, token));
	}

	[TestMethod]
	public async Task DeleteAsync_WithCurrentToken_RemovesBlob()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		await blob.DeleteAsync(token, CT);

		Assert.IsFalse(await blob.ExistsAsync(CT));
		Assert.IsNull(await blob.GetInfoAsync(CT));
		Assert.IsNull(await blob.ReadAsync(CT));
		Assert.IsNull(await blob.OpenReadAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WithStaleToken_Throws()
	{
		var blob = CreateBlob();
		var staleToken = await WriteAsync(blob, Data, null);
		await WriteAsync(blob, OtherData, staleToken);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.DeleteAsync(staleToken, CT));

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task DeleteAsync_ThenCreate_Succeeds()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);
		await blob.DeleteAsync(token, CT);

		await WriteAsync(blob, OtherData, null);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task DeleteAsync_AnyToken_WhenExists_RemovesBlob()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);

		await blob.DeleteAsync(IBlob.AnyConcurrencyToken, CT);

		Assert.IsFalse(await blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_AnyToken_WhenNotExists_Throws()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => blob.DeleteAsync(IBlob.AnyConcurrencyToken, CT));
	}

	// DeleteIfExistsAsync

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenNotExists_ReturnsFalse()
	{
		var blob = CreateBlob();

		Assert.IsFalse(await blob.DeleteIfExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenExists_RemovesAndReturnsTrue()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);

		Assert.IsTrue(await blob.DeleteIfExistsAsync(CT));

		Assert.IsFalse(await blob.ExistsAsync(CT));
		Assert.IsFalse(await blob.DeleteIfExistsAsync(CT));
	}

	// Cancellation

	[TestMethod]
	public async Task Operations_WithCanceledToken_Throw()
	{
		var blob = CreateBlob();
		using var cts = new CancellationTokenSource();
		cts.Cancel();

		await Assert.ThrowsAsync<OperationCanceledException>(() => blob.ExistsAsync(cts.Token));
		await Assert.ThrowsAsync<OperationCanceledException>(() => blob.GetInfoAsync(cts.Token));
		await Assert.ThrowsAsync<OperationCanceledException>(() => blob.ReadAsync(cts.Token));
		await Assert.ThrowsAsync<OperationCanceledException>(() => blob.WriteAsync(Data, null, default, cts.Token));
		await Assert.ThrowsAsync<OperationCanceledException>(() => blob.DeleteIfExistsAsync(cts.Token));
		Assert.IsFalse(await blob.ExistsAsync(CT));
	}

	// TransformAsync under concurrent delete

	[TestMethod]
	public async Task TransformAsync_DeleteWhenDeletedConcurrently_Retries()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);
		var calls = 0;

		var result = await blob.TransformAsync(async (data, ct) =>
		{
			if (calls++ == 0)
			{
				await blob.DeleteIfExistsAsync(ct);
			}

			return null;
		}, CT);

		Assert.IsNull(result);
		Assert.AreEqual(2, calls);
		Assert.IsFalse(await blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task TransformAsync_UpdateWhenDeletedConcurrently_Retries()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);
		var calls = 0;

		await blob.TransformAsync(async (data, ct) =>
		{
			if (calls++ == 0)
			{
				await blob.DeleteIfExistsAsync(ct);
			}

			return BinaryData.FromBytes(OtherData);
		}, CT);

		Assert.AreEqual(2, calls);
		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}
}