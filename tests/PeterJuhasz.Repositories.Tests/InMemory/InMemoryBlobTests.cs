using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;

namespace PeterJuhasz.Repositories.Tests.InMemory;

[TestClass]
public class InMemoryBlobTests(TestContext testContext)
{
	private static readonly byte[] Data = [1, 2, 3];
	private static readonly byte[] OtherData = [4, 5, 6, 7];

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlob CreateBlob() => new("test", _time);

	private Task<string> WriteAsync(IBlob blob, byte[] data, string? concurrencyToken, IBlob.WriteBlobInfo options = default) =>
		blob.WriteAsync(data, concurrencyToken, options, CT);

	private async Task<byte[]> ReadBytesAsync(IBlob blob)
	{
		var result = await blob.ReadAsync(CT);
		Assert.IsNotNull(result);
		return result.Value.ToArray();
	}

	// Name

	[TestMethod]
	public void Name_ReturnsConstructorValue()
	{
		var blob = new InMemoryBlob("some/name", _time);

		Assert.AreEqual("some/name", blob.Name);
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
		Assert.AreEqual(_time.GetUtcNow(), info.LastModified);
		Assert.AreEqual("gzip", info.ContentEncoding);
		Assert.AreEqual("application/json", info.MediaType);
		Assert.IsNotNull(info.Metadata);
		Assert.AreEqual("value", info.Metadata["key"]);
	}

	[TestMethod]
	public async Task WriteAsync_CopiesInputBuffer()
	{
		var blob = CreateBlob();
		var buffer = Data.ToArray();

		await WriteAsync(blob, buffer, null);
		buffer[0] = 99;

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
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

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteAsync(blob, Data, "token"));

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
		_time.Advance(TimeSpan.FromMinutes(1));

		var newToken = await WriteAsync(blob, OtherData, token, new(MediaType: "application/octet-stream"));

		Assert.AreNotEqual(token, newToken);
		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));

		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.AreEqual(newToken, info.ConcurrencyToken);
		Assert.AreEqual(_time.GetUtcNow(), info.LastModified);
		Assert.AreEqual("application/octet-stream", info.MediaType);
		Assert.IsNull(info.Metadata);
	}

	[TestMethod]
	public async Task WriteAsync_ConcurrentWritersWithSameToken_OnlyOneSucceeds()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
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
	public async Task OpenReadAsync_ReturnsStreamAndInfo()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		var result = await blob.OpenReadAsync(CT);

		Assert.IsNotNull(result);
		Assert.AreEqual(token, result.Info.ConcurrencyToken);
		await using var stream = result.Value;
		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, CT);
		Assert.AreSequenceEqual(Data, buffer.ToArray());
	}

	[TestMethod]
	public async Task OpenReadAsync_StreamIsSnapshot()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		var result = await blob.OpenReadAsync(CT);
		Assert.IsNotNull(result);
		await WriteAsync(blob, OtherData, token);

		await using var stream = result.Value;
		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, CT);
		Assert.AreSequenceEqual(Data, buffer.ToArray());
	}

	// SetMetadataAsync

	[TestMethod]
	public async Task SetMetadataAsync_WhenNotExists_Throws()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => blob.SetMetadataAsync("token", new Dictionary<string, string>(), CT));
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
		Assert.IsNull(info.Metadata);
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
		Assert.IsNull(info.Metadata);
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

	// OpenWriteAsync

	[TestMethod]
	public async Task OpenWriteAsync_Create_CommitsOnDispose()
	{
		var blob = CreateBlob();

		var stream = await blob.OpenWriteAsync(null, new(MediaType: "text/plain"), CT);
		await stream.WriteAsync(Data, CT);
		Assert.IsFalse(await blob.ExistsAsync(CT));
		stream.Dispose();

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.AreEqual("text/plain", info.MediaType);
		Assert.AreEqual(_time.GetUtcNow(), info.LastModified);
	}

	[TestMethod]
	public async Task OpenWriteAsync_Create_CommitsOnDisposeAsync()
	{
		var blob = CreateBlob();

		await using (var stream = await blob.OpenWriteAsync(null, default, CT))
		{
			await stream.WriteAsync(Data, CT);
		}

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task OpenWriteAsync_DisposeTwice_CommitsOnce()
	{
		var blob = CreateBlob();

		var stream = await blob.OpenWriteAsync(null, default, CT);
		await stream.WriteAsync(Data, CT);
		await stream.DisposeAsync();
		var info = await blob.GetInfoAsync(CT);
		stream.Dispose();

		Assert.AreEqual(info?.ConcurrencyToken, (await blob.GetInfoAsync(CT))?.ConcurrencyToken);
	}

	[TestMethod]
	public async Task OpenWriteAsync_Create_WhenExists_Throws()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.OpenWriteAsync(null, default, CT));
	}

	[TestMethod]
	public async Task OpenWriteAsync_WithToken_WhenNotExists_Throws()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.OpenWriteAsync("token", default, CT));
	}

	[TestMethod]
	public async Task OpenWriteAsync_WithStaleToken_Throws()
	{
		var blob = CreateBlob();
		var staleToken = await WriteAsync(blob, Data, null);
		await WriteAsync(blob, Data, staleToken);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.OpenWriteAsync(staleToken, default, CT));
	}

	[TestMethod]
	public async Task OpenWriteAsync_WithCurrentToken_Overwrites()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		await using (var stream = await blob.OpenWriteAsync(token, default, CT))
		{
			await stream.WriteAsync(OtherData, CT);
		}

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
		Assert.AreNotEqual(token, (await blob.GetInfoAsync(CT))?.ConcurrencyToken);
	}

	[TestMethod]
	public async Task OpenWriteAsync_BlobChangedBeforeCommit_ThrowsOnDispose()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		var stream = await blob.OpenWriteAsync(token, default, CT);
		await stream.WriteAsync(OtherData, CT);
		await WriteAsync(blob, Data, token);

		await Assert.ThrowsExactlyAsync<ConflictException>(async () => await stream.DisposeAsync());
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task OpenWriteAsync_BlobCreatedBeforeCommit_ThrowsOnDispose()
	{
		var blob = CreateBlob();

		var stream = await blob.OpenWriteAsync(null, default, CT);
		await stream.WriteAsync(OtherData, CT);
		await WriteAsync(blob, Data, null);

		Assert.ThrowsExactly<ConflictException>(stream.Dispose);
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	// DeleteAsync

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_Throws()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => blob.DeleteAsync("token", CT));
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
	public async Task DeleteAsync_OldTokenAfterDelete_Throws()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);
		await blob.DeleteAsync(token, CT);

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => blob.DeleteAsync(token, CT));
		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteAsync(blob, Data, token));
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
}
