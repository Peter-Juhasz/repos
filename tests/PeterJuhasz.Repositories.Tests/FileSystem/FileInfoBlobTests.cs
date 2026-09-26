using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.FileSystem;

namespace PeterJuhasz.Repositories.Tests.FileSystem;

[TestClass]
public sealed class FileInfoBlobTests(TestContext testContext) : IDisposable
{
	private static readonly byte[] Data = [1, 2, 3];
	private static readonly byte[] OtherData = [4, 5, 6, 7];

	private readonly TemporaryDirectory _directory = new();

	private CancellationToken CT => testContext.CancellationToken;

	public void Dispose() => _directory.Dispose();

	private FileInfo GetFile(string name = "test.bin") => _directory.GetFile(name);

	private FileInfoBlob CreateBlob(string name = "test.bin") => new(GetFile(name));

	private Task<string> WriteAsync(IBlob blob, byte[] data, string? concurrencyToken, IBlob.WriteBlobInfo options = default) =>
		blob.WriteAsync(data, concurrencyToken, options, CT);

	private async Task<byte[]> ReadBytesAsync(IBlob blob)
	{
		var result = await blob.ReadAsync(CT);
		Assert.IsNotNull(result);
		return result.Value.ToArray();
	}

	/// <summary>
	/// Changes the last write time of the file, invalidating previously issued concurrency tokens.
	/// Consecutive writes may share a timestamp, so tests don't rely on them to produce a stale token.
	/// </summary>
	private void SimulateExternalChange(string name = "test.bin") =>
		File.SetLastWriteTimeUtc(GetFile(name).FullName, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

	// Name / File

	[TestMethod]
	public void Name_ReturnsFileName()
	{
		var blob = CreateBlob("some.bin");

		Assert.AreEqual("some.bin", blob.Name);
	}

	[TestMethod]
	public void File_ReturnsConstructorValue()
	{
		var file = GetFile();
		var blob = new FileInfoBlob(file);

		Assert.AreSame(file, blob.File);
	}

	[TestMethod]
	public void AsBlob_WrapsFile()
	{
		var file = GetFile();

		var blob = file.AsBlob();

		Assert.IsInstanceOfType<FileInfoBlob>(blob);
		Assert.AreSame(file, ((FileInfoBlob)blob).File);
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

	[TestMethod]
	public async Task NewBlob_InMissingDirectory_DoesNotExist()
	{
		var blob = CreateBlob(Path.Combine("missing", "test.bin"));

		Assert.IsFalse(await blob.ExistsAsync(CT));
		Assert.IsNull(await blob.GetInfoAsync(CT));
		Assert.IsNull(await blob.ReadAsync(CT));
		Assert.IsNull(await blob.OpenReadAsync(CT));
	}

	[TestMethod]
	public async Task ExistingFile_Exists()
	{
		await File.WriteAllBytesAsync(GetFile().FullName, Data, CT);
		var blob = CreateBlob();

		Assert.IsTrue(await blob.ExistsAsync(CT));
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	// WriteAsync

	[TestMethod]
	public async Task WriteAsync_Create_StoresDataAndInfo()
	{
		var blob = CreateBlob();

		var token = await WriteAsync(blob, Data, null);

		Assert.IsFalse(string.IsNullOrEmpty(token));
		Assert.IsTrue(await blob.ExistsAsync(CT));
		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
		Assert.AreSequenceEqual(Data, await File.ReadAllBytesAsync(GetFile().FullName, CT));

		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.AreEqual(token, info.ConcurrencyToken);
		Assert.AreEqual(new DateTimeOffset(GetFile().LastWriteTimeUtc, TimeSpan.Zero), info.LastModified);
	}

	[TestMethod]
	public async Task WriteAsync_CreatesMissingDirectories()
	{
		var blob = CreateBlob(Path.Combine("a", "b", "test.bin"));

		await WriteAsync(blob, Data, null);

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
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
	public async Task WriteAsync_Empty_CreatesEmptyFile()
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

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteAsync(blob, Data, "token"));

		Assert.IsFalse(await blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task WriteAsync_WithStaleToken_Throws()
	{
		var blob = CreateBlob();
		var staleToken = await WriteAsync(blob, Data, null);
		SimulateExternalChange();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => WriteAsync(blob, OtherData, staleToken));

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task WriteAsync_WithCurrentToken_Overwrites()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		var newToken = await WriteAsync(blob, OtherData, token);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
		Assert.AreEqual(newToken, (await blob.GetInfoAsync(CT))?.ConcurrencyToken);
	}

	[TestMethod]
	public async Task WriteAsync_WithCurrentToken_ShorterData_Truncates()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, OtherData, null);

		await WriteAsync(blob, Data, token);

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
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
		await WriteAsync(blob, Data, null);

		await WriteAsync(blob, OtherData, IBlob.AnyOrNoneConcurrencyToken);

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
		SimulateExternalChange();

		await WriteAsync(blob, OtherData, IBlob.AnyConcurrencyToken);

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	// ReadAsync / OpenReadAsync / GetInfoAsync

	[TestMethod]
	public async Task ReadAsync_ReturnsDataAndInfo()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		var result = await blob.ReadAsync(CT);

		Assert.IsNotNull(result);
		Assert.AreSequenceEqual(Data, result.Value.ToArray());
		Assert.AreEqual(token, result.Info.ConcurrencyToken);
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
	public async Task GetInfoAsync_AfterExternalChange_ReturnsFreshInfo()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		SimulateExternalChange();

		var info = await blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.AreNotEqual(token, info.ConcurrencyToken);
		Assert.AreEqual(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), info.LastModified);
	}

	// SetMetadataAsync

	[TestMethod]
	public async Task SetMetadataAsync_ThrowsNotSupported()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => blob.SetMetadataAsync(token, new Dictionary<string, string> { ["a"] = "b" }, CT));

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	// OpenWriteAsync

	[TestMethod]
	public async Task OpenWriteAsync_Create_WritesData()
	{
		var blob = CreateBlob();

		await using (var stream = await blob.OpenWriteAsync(null, default, CT))
		{
			await stream.WriteAsync(Data, CT);
		}

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task OpenWriteAsync_CreatesMissingDirectories()
	{
		var blob = CreateBlob(Path.Combine("a", "b", "test.bin"));

		await using (var stream = await blob.OpenWriteAsync(null, default, CT))
		{
			await stream.WriteAsync(Data, CT);
		}

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task OpenWriteAsync_Create_WhenExists_Throws()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.OpenWriteAsync(null, default, CT));

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task OpenWriteAsync_WithToken_WhenNotExists_Throws()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.OpenWriteAsync("token", default, CT));

		Assert.IsFalse(await blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task OpenWriteAsync_WithStaleToken_Throws()
	{
		var blob = CreateBlob();
		var staleToken = await WriteAsync(blob, Data, null);
		SimulateExternalChange();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.OpenWriteAsync(staleToken, default, CT));

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
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
	}

	[TestMethod]
	public async Task OpenWriteAsync_WithCurrentToken_ShorterData_Truncates()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, OtherData, null);

		await using (var stream = await blob.OpenWriteAsync(token, default, CT))
		{
			await stream.WriteAsync(Data, CT);
		}

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task OpenWriteAsync_AnyOrNoneToken_WhenNotExists_Creates()
	{
		var blob = CreateBlob();

		await using (var stream = await blob.OpenWriteAsync(IBlob.AnyOrNoneConcurrencyToken, default, CT))
		{
			await stream.WriteAsync(Data, CT);
		}

		Assert.AreSequenceEqual(Data, await ReadBytesAsync(blob));
	}

	[TestMethod]
	public async Task OpenWriteAsync_AnyToken_WhenNotExists_Throws()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.OpenWriteAsync(IBlob.AnyConcurrencyToken, default, CT));

		Assert.IsFalse(await blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task OpenWriteAsync_AnyToken_WhenExists_Overwrites()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);

		await using (var stream = await blob.OpenWriteAsync(IBlob.AnyConcurrencyToken, default, CT))
		{
			await stream.WriteAsync(OtherData, CT);
		}

		Assert.AreSequenceEqual(OtherData, await ReadBytesAsync(blob));
	}

	// DeleteAsync

	[TestMethod]
	public async Task DeleteAsync_WithCurrentToken_RemovesFile()
	{
		var blob = CreateBlob();
		var token = await WriteAsync(blob, Data, null);

		await blob.DeleteAsync(token, CT);

		Assert.IsFalse(GetFile().Exists);
		Assert.IsFalse(await blob.ExistsAsync(CT));
		Assert.IsNull(await blob.GetInfoAsync(CT));
		Assert.IsNull(await blob.ReadAsync(CT));
		Assert.IsNull(await blob.OpenReadAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_ThrowsConflict()
	{
		var blob = CreateBlob();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.DeleteAsync("token", CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WithStaleToken_Throws()
	{
		var blob = CreateBlob();
		var staleToken = await WriteAsync(blob, Data, null);
		SimulateExternalChange();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.DeleteAsync(staleToken, CT));

		Assert.IsTrue(await blob.ExistsAsync(CT));
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
	public async Task DeleteAsync_AnyToken_WhenExists_RemovesFile()
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

	[TestMethod]
	public async Task DeleteAsync_AnyOrNoneToken_Throws()
	{
		var blob = CreateBlob();
		await WriteAsync(blob, Data, null);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => blob.DeleteAsync(IBlob.AnyOrNoneConcurrencyToken, CT));

		Assert.IsTrue(await blob.ExistsAsync(CT));
	}

	// DeleteIfExistsAsync

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenNotExists_ReturnsFalse()
	{
		var blob = CreateBlob();

		Assert.IsFalse(await blob.DeleteIfExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_InMissingDirectory_ReturnsFalse()
	{
		var blob = CreateBlob(Path.Combine("missing", "test.bin"));

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
		await Assert.ThrowsAsync<OperationCanceledException>(() => blob.WriteAsync(Data, null, default, cts.Token));
		await Assert.ThrowsAsync<OperationCanceledException>(() => blob.OpenWriteAsync(null, default, cts.Token));
		await Assert.ThrowsAsync<OperationCanceledException>(() => blob.DeleteIfExistsAsync(cts.Token));
		Assert.IsFalse(GetFile().Exists);
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