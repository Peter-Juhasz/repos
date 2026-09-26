using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;

namespace PeterJuhasz.Repositories.Tests.Blobs;

[TestClass]
public class BlobBinaryRepositoryTests(TestContext testContext)
{
	private static readonly byte[] Value = [1, 2, 3];
	private static readonly byte[] OtherValue = [4, 5, 6, 7];

	private const string MediaType = "application/octet-stream";
	private const string OtherMediaType = "image/png";

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlob Blob => field ??= new("test", _time);

	private IBinaryRepository CreateRepository() => new BlobBinaryRepository(Blob);

	private static BinaryData Data(byte[] bytes, string? mediaType = MediaType) => new(bytes, mediaType);

	private async Task<byte[]> ReadBlobAsync()
	{
		var result = await Blob.ReadAsync(CT);
		Assert.IsNotNull(result);
		return result.Value.ToArray();
	}

	private async Task<IBlob.ReadBlobInfo> GetBlobInfoAsync()
	{
		var info = await Blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		return info;
	}

	private async Task<string?> GetBlobTokenAsync() => (await Blob.GetInfoAsync(CT))?.ConcurrencyToken;

	private static async Task<byte[]> ReadToEndAsync(Stream stream, CancellationToken cancellationToken)
	{
		using var buffer = new MemoryStream();
		await stream.CopyToAsync(buffer, cancellationToken);
		return buffer.ToArray();
	}

	// Blob

	[TestMethod]
	public void Blob_ReturnsUnderlyingBlob()
	{
		var repository = new BlobBinaryRepository(Blob);

		Assert.AreSame(Blob, repository.Blob);
	}

	[TestMethod]
	public void AsBinaryRepository_WrapsBlob()
	{
		var repository = Blob.AsBinaryRepository();

		Assert.IsInstanceOfType<BlobBinaryRepository>(repository);
		Assert.IsTrue(repository.TryGetBlob(out var blob));
		Assert.AreSame(Blob, blob);
	}

	[TestMethod]
	public void TryGetBlob_ReturnsUnderlyingBlob()
	{
		var repository = CreateRepository();

		Assert.IsTrue(repository.TryGetBlob(out var blob));
		Assert.AreSame(Blob, blob);
	}

	// Empty blob

	[TestMethod]
	public async Task EmptyBlob_DoesNotExist()
	{
		var repository = CreateRepository();

		Assert.IsFalse(await repository.ExistsAsync(CT));
		Assert.IsNull(await repository.GetVersionAsync(CT));
		Assert.IsNull(await repository.GetAsync(CT));
		Assert.IsNull(await repository.GetStreamAsync(CT));
	}

	// Reading

	[TestMethod]
	public async Task GetAsync_ReturnsBlobContentAndMediaType()
	{
		var repository = CreateRepository();
		var token = await Blob.WriteAsync(Value, null, new(MediaType: OtherMediaType), CT);

		var result = await repository.GetAsync(CT);

		Assert.IsNotNull(result);
		Assert.AreSequenceEqual(Value, result.ToArray());
		Assert.AreEqual(OtherMediaType, result.MediaType);
		Assert.AreEqual(token, await repository.GetVersionAsync(CT));
		Assert.IsTrue(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task GetStreamAsync_ReturnsBlobContent()
	{
		var repository = CreateRepository();
		await Blob.WriteAsync(Value, null, new(MediaType: MediaType), CT);

		await using var stream = await repository.GetStreamAsync(CT);

		Assert.IsNotNull(stream);
		Assert.AreSequenceEqual(Value, await ReadToEndAsync(stream, CT));
	}

	// CreateAsync (BinaryData)

	[TestMethod]
	public async Task CreateAsync_WritesContentWithMediaType()
	{
		var repository = CreateRepository();

		await repository.CreateAsync(Data(Value), CT);

		Assert.AreSequenceEqual(Value, await ReadBlobAsync());
		Assert.AreEqual(MediaType, (await GetBlobInfoAsync()).MediaType);
	}

	[TestMethod]
	public async Task CreateAsync_NoMediaType_WritesContentWithoutMediaType()
	{
		var repository = CreateRepository();

		await repository.CreateAsync(Data(Value, mediaType: null), CT);

		Assert.AreSequenceEqual(Value, await ReadBlobAsync());
		Assert.IsNull((await GetBlobInfoAsync()).MediaType);
	}

	[TestMethod]
	public async Task CreateAsync_RoundTrips()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);

		var result = await repository.GetAsync(CT);

		Assert.IsNotNull(result);
		Assert.AreSequenceEqual(Value, result.ToArray());
		Assert.AreEqual(MediaType, result.MediaType);
		Assert.AreEqual(await GetBlobTokenAsync(), await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task CreateAsync_WhenExists_Throws()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.CreateAsync(Data(OtherValue), CT));

		Assert.AreSequenceEqual(Value, await ReadBlobAsync());
	}

	// UpdateAsync / StoreAsync (BinaryData)

	[TestMethod]
	public async Task UpdateAsync_WithCurrentVersion_Overwrites()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);
		var version = await repository.GetVersionAsync(CT);
		Assert.IsNotNull(version);

		await repository.UpdateAsync(Data(OtherValue, OtherMediaType), version, CT);

		Assert.AreSequenceEqual(OtherValue, await ReadBlobAsync());
		Assert.AreEqual(OtherMediaType, (await GetBlobInfoAsync()).MediaType);
		Assert.AreNotEqual(version, await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task UpdateAsync_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.UpdateAsync(Data(Value), "version", CT));

		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task UpdateAsync_WithStaleVersion_Throws()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);
		var version = await repository.GetVersionAsync(CT);
		Assert.IsNotNull(version);
		await repository.UpdateAsync(Data(Value), version, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.UpdateAsync(Data(OtherValue), version, CT));

		Assert.AreSequenceEqual(Value, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task UpdateAsync_ReplacesBlobMetadata()
	{
		var repository = CreateRepository();
		var token = await Blob.WriteAsync(Value, null, new(MediaType: MediaType, Metadata: new Dictionary<string, string> { ["a"] = "b" }), CT);

		await repository.UpdateAsync(Data(OtherValue), token, CT);

		Assert.IsNull((await GetBlobInfoAsync()).Metadata);
	}

	[TestMethod]
	public async Task StoreAsync_AnyToken_WhenExists_Overwrites()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);

		await repository.StoreAsync(Data(OtherValue), IBlob.AnyConcurrencyToken, CT);

		Assert.AreSequenceEqual(OtherValue, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task StoreAsync_AnyToken_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.StoreAsync(Data(Value), IBlob.AnyConcurrencyToken, CT));

		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task StoreAsync_AnyOrNoneToken_WhenNotExists_Creates()
	{
		var repository = CreateRepository();

		await repository.StoreAsync(Data(Value), IBlob.AnyOrNoneConcurrencyToken, CT);

		Assert.AreSequenceEqual(Value, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task StoreAsync_AnyOrNoneToken_WhenExists_Overwrites()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);

		await repository.StoreAsync(Data(OtherValue), IBlob.AnyOrNoneConcurrencyToken, CT);

		Assert.AreSequenceEqual(OtherValue, await ReadBlobAsync());
	}

	// CreateAsync / UpdateAsync / StoreAsync (Stream)

	[TestMethod]
	public async Task CreateAsync_Stream_WritesContentWithMediaType()
	{
		var repository = CreateRepository();

		await repository.CreateAsync(new MemoryStream(Value), MediaType, CT);

		Assert.AreSequenceEqual(Value, await ReadBlobAsync());
		Assert.AreEqual(MediaType, (await GetBlobInfoAsync()).MediaType);
	}

	[TestMethod]
	public async Task CreateAsync_Stream_NoMediaType_WritesContentWithoutMediaType()
	{
		var repository = CreateRepository();

		await repository.CreateAsync(new MemoryStream(Value), null, CT);

		Assert.AreSequenceEqual(Value, await ReadBlobAsync());
		Assert.IsNull((await GetBlobInfoAsync()).MediaType);
	}

	[TestMethod]
	public async Task CreateAsync_Stream_Empty_CreatesEmptyBlob()
	{
		var repository = CreateRepository();

		await repository.CreateAsync(new MemoryStream(), MediaType, CT);

		Assert.IsTrue(await repository.ExistsAsync(CT));
		Assert.IsEmpty(await ReadBlobAsync());
	}

	[TestMethod]
	public async Task CreateAsync_Stream_WhenExists_Throws()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.CreateAsync(new MemoryStream(OtherValue), MediaType, CT));

		Assert.AreSequenceEqual(Value, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task UpdateAsync_Stream_WithCurrentVersion_Overwrites()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);
		var version = await repository.GetVersionAsync(CT);
		Assert.IsNotNull(version);

		await repository.UpdateAsync(new MemoryStream(OtherValue), OtherMediaType, version, CT);

		Assert.AreSequenceEqual(OtherValue, await ReadBlobAsync());
		Assert.AreEqual(OtherMediaType, (await GetBlobInfoAsync()).MediaType);
		Assert.AreNotEqual(version, await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task UpdateAsync_Stream_WithStaleVersion_Throws()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);
		var version = await repository.GetVersionAsync(CT);
		Assert.IsNotNull(version);
		await repository.UpdateAsync(Data(Value), version, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.UpdateAsync(new MemoryStream(OtherValue), MediaType, version, CT));

		Assert.AreSequenceEqual(Value, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task UpdateAsync_Stream_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.UpdateAsync(new MemoryStream(Value), MediaType, "version", CT));

		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task StoreAsync_Stream_AnyOrNoneToken_WhenExists_Overwrites()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);

		await repository.StoreAsync(new MemoryStream(OtherValue), MediaType, IBlob.AnyOrNoneConcurrencyToken, CT);

		Assert.AreSequenceEqual(OtherValue, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task StoreAsync_Stream_AnyToken_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.StoreAsync(new MemoryStream(Value), MediaType, IBlob.AnyConcurrencyToken, CT));

		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	// DeleteAsync / DeleteIfExistsAsync

	[TestMethod]
	public async Task DeleteAsync_WithCurrentVersion_RemovesBlob()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);
		var version = await repository.GetVersionAsync(CT);
		Assert.IsNotNull(version);

		await repository.DeleteAsync(version, CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
		Assert.IsNull(await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_AnyToken_RemovesBlob()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);

		await repository.DeleteAsync(IBlob.AnyConcurrencyToken, CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WithStaleVersion_Throws()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);
		var version = await repository.GetVersionAsync(CT);
		Assert.IsNotNull(version);
		await repository.UpdateAsync(Data(OtherValue), version, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.DeleteAsync(version, CT));

		Assert.AreSequenceEqual(OtherValue, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => repository.DeleteAsync("version", CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenExists_RemovesBlob()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Data(Value), CT);

		await repository.DeleteIfExistsAsync(CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenNotExists_DoesNothing()
	{
		var repository = CreateRepository();

		await repository.DeleteIfExistsAsync(CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}
}
