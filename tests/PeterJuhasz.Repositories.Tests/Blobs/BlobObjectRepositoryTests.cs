using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Text;
using System.Text.Json;

namespace PeterJuhasz.Repositories.Tests.Blobs;

[TestClass]
public class BlobObjectRepositoryTests(TestContext testContext)
{
	public sealed record class Item(string Name);

	private static readonly Item Value = new("value");
	private static readonly Item OtherValue = new("other");

	private const string ValueJson = """{"name":"value"}""";
	private const string OtherValueJson = """{"name":"other"}""";

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlob Blob => field ??= new("test", _time);

	private IObjectRepository<Item> CreateRepository(IEqualityComparer<Item>? comparer = null) =>
		new BlobObjectRepository<Item>(Blob, new JsonSerializerOptionsJsonSerializer<Item>(JsonSerializerOptions.Web), comparer);

	private Task<string> WriteBlobAsync(string content, string? concurrencyToken, IBlob.WriteBlobInfo options) =>
		Blob.WriteAsync(Encoding.UTF8.GetBytes(content), concurrencyToken, options, CT);

	private async Task<string> ReadBlobAsync()
	{
		var result = await Blob.ReadAsync(CT);
		Assert.IsNotNull(result);
		return result.Value.ToString();
	}

	private async Task<string?> GetBlobTokenAsync() => (await Blob.GetInfoAsync(CT))?.ConcurrencyToken;

	// Blob

	[TestMethod]
	public void Blob_ReturnsUnderlyingBlob()
	{
		var repository = new BlobObjectRepository<Item>(Blob, new JsonSerializerOptionsJsonSerializer<Item>(JsonSerializerOptions.Web));

		Assert.AreSame(Blob, repository.Blob);
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
		Assert.IsNull(await repository.GetOrDefaultAsync(CT));
		Assert.IsNull(await repository.GetOrDefaultWithVersionAsync(CT));
		Assert.IsNull(await repository.RawStreamAsync(CT));
	}

	[TestMethod]
	public async Task GetAsync_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<NotFoundException>(async () => await repository.GetAsync(CT));
	}

	// Reading

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_DeserializesBlobContent()
	{
		var repository = CreateRepository();
		var token = await WriteBlobAsync(ValueJson, null, new(MediaType: "application/json"));

		var result = await repository.GetOrDefaultWithVersionAsync(CT);

		Assert.IsNotNull(result);
		Assert.AreEqual(Value, result.Value.Value);
		Assert.AreEqual(token, result.Value.ETag);
		Assert.AreEqual(token, await repository.GetVersionAsync(CT));
		Assert.IsTrue(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_Utf8Bom_Deserializes()
	{
		var repository = CreateRepository();
		await WriteBlobAsync("﻿" + ValueJson, null, new(MediaType: "application/json"));

		Assert.AreEqual(Value, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_UnexpectedMediaType_Throws()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ValueJson, null, new(MediaType: "text/plain"));

		await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await repository.GetOrDefaultWithVersionAsync(CT));
	}

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_NoMediaType_Throws()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ValueJson, null, default);

		await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await repository.GetOrDefaultWithVersionAsync(CT));
	}

	[TestMethod]
	public async Task GetOrDefaultWithVersionAsync_InvalidJson_Throws()
	{
		var repository = CreateRepository();
		await WriteBlobAsync("not json", null, new(MediaType: "application/json"));

		await Assert.ThrowsAsync<JsonException>(async () => await repository.GetOrDefaultWithVersionAsync(CT));
	}

	// RawStreamAsync

	[TestMethod]
	public async Task RawStreamAsync_ReturnsBlobContentAndInfo()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		var result = await repository.RawStreamAsync(CT);

		Assert.IsNotNull(result);
		await using var raw = result.Value;
		Assert.AreEqual(created.ETag, raw.ETag);
		Assert.AreEqual(_time.GetUtcNow(), raw.LastModified);
		Assert.IsNull(raw.Encoding);
		using var reader = new StreamReader(raw.Stream);
		Assert.AreEqual(ValueJson, await reader.ReadToEndAsync(CT));
	}

	[TestMethod]
	public async Task RawStreamAsync_ReturnsContentEncoding()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ValueJson, null, new(ContentEncoding: "gzip", MediaType: "application/json"));

		var result = await repository.RawStreamAsync(CT);

		Assert.IsNotNull(result);
		await using var raw = result.Value;
		Assert.AreEqual("gzip", raw.Encoding);
	}

	// CreateAsync

	[TestMethod]
	public async Task CreateAsync_WritesJsonWithMediaType()
	{
		var repository = CreateRepository();

		var result = await repository.CreateAsync(Value, CT);

		Assert.AreSame(Value, result.Value);
		Assert.AreEqual(await GetBlobTokenAsync(), result.ETag);
		Assert.AreEqual(ValueJson, await ReadBlobAsync());
		var info = await Blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.AreEqual("application/json", info.MediaType);
	}

	[TestMethod]
	public async Task CreateAsync_RoundTrips()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		var result = await repository.GetOrDefaultWithVersionAsync(CT);

		Assert.IsNotNull(result);
		Assert.AreEqual(Value, result.Value.Value);
		Assert.AreEqual(created.ETag, result.Value.ETag);
	}

	[TestMethod]
	public async Task CreateAsync_WhenExists_Throws()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.CreateAsync(OtherValue, CT));

		Assert.AreEqual(ValueJson, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task CreateIfNotExistsAsync_WhenNotExists_CreatesAndReturnsTrue()
	{
		var repository = CreateRepository();

		Assert.IsTrue(await repository.CreateIfNotExistsAsync(Value, CT));

		Assert.AreEqual(Value, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task CreateIfNotExistsAsync_WhenExists_ReturnsFalse()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		Assert.IsFalse(await repository.CreateIfNotExistsAsync(OtherValue, CT));

		Assert.AreEqual(Value, await repository.GetAsync(CT));
	}

	// UpdateAsync / StoreAsync

	[TestMethod]
	public async Task UpdateAsync_WithCurrentVersion_Overwrites()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		var updated = await repository.UpdateAsync(OtherValue, created.ETag, CT);

		Assert.AreSame(OtherValue, updated.Value);
		Assert.AreNotEqual(created.ETag, updated.ETag);
		Assert.AreEqual(await GetBlobTokenAsync(), updated.ETag);
		Assert.AreEqual(OtherValueJson, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task UpdateAsync_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.UpdateAsync(Value, "version", CT));

		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task UpdateAsync_WithStaleVersion_Throws()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);
		await repository.UpdateAsync(Value, created.ETag, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.UpdateAsync(OtherValue, created.ETag, CT));

		Assert.AreEqual(ValueJson, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task UpdateAsync_ReplacesBlobMetadata()
	{
		var repository = CreateRepository();
		var token = await WriteBlobAsync(ValueJson, null, new(MediaType: "application/json", Metadata: new Dictionary<string, string> { ["a"] = "b" }));

		await repository.UpdateAsync(OtherValue, token, CT);

		var info = await Blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.IsNull(info.Metadata);
	}

	[TestMethod]
	public async Task StoreAsync_AnyToken_WhenExists_Overwrites()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		await repository.StoreAsync(OtherValue, IBlob.AnyConcurrencyToken, CT);

		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task StoreAsync_AnyToken_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.StoreAsync(Value, IBlob.AnyConcurrencyToken, CT));

		Assert.IsFalse(await repository.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task StoreAsync_AnyOrNoneToken_WhenNotExists_Creates()
	{
		var repository = CreateRepository();

		var result = await repository.StoreAsync(Value, IBlob.AnyOrNoneConcurrencyToken, CT);

		Assert.AreEqual(Value, await repository.GetAsync(CT));
		Assert.AreEqual(result.ETag, await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task StoreAsync_AnyOrNoneToken_WhenExists_Overwrites()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		await repository.StoreAsync(OtherValue, IBlob.AnyOrNoneConcurrencyToken, CT);

		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
	}

	// DeleteWithVersionAsync / DeleteAsync / DeleteIfExistsAsync

	[TestMethod]
	public async Task DeleteWithVersionAsync_WithCurrentVersion_RemovesBlob()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		await repository.DeleteWithVersionAsync(created.ETag, CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
		Assert.IsNull(await repository.GetOrDefaultWithVersionAsync(CT));
	}

	[TestMethod]
	public async Task DeleteWithVersionAsync_WithStaleVersion_Throws()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);
		await repository.UpdateAsync(OtherValue, created.ETag, CT);

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.DeleteWithVersionAsync(created.ETag, CT));

		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task DeleteWithVersionAsync_WhenNotExists_ThrowsConflict()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<ConflictException>(() => repository.DeleteWithVersionAsync("version", CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenExists_RemovesBlob()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		await repository.DeleteAsync(CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteAsync_WhenNotExists_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<NotFoundException>(() => repository.DeleteAsync(CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenNotExists_ReturnsFalse()
	{
		var repository = CreateRepository();

		Assert.IsFalse(await repository.DeleteIfExistsAsync(CT));
	}

	[TestMethod]
	public async Task DeleteIfExistsAsync_WhenExists_RemovesAndReturnsTrue()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		Assert.IsTrue(await repository.DeleteIfExistsAsync(CT));

		Assert.IsFalse(await Blob.ExistsAsync(CT));
		Assert.IsFalse(await repository.DeleteIfExistsAsync(CT));
	}

	// ApplyAsync

	[TestMethod]
	public async Task ApplyAsync_WhenNotExists_Creates()
	{
		var repository = CreateRepository();

		var result = await repository.ApplyAsync(current =>
		{
			Assert.IsNull(current);
			return Value;
		}, CT);

		Assert.AreEqual(Value, result);
		Assert.AreEqual(ValueJson, await ReadBlobAsync());
		Assert.AreEqual("application/json", (await Blob.GetInfoAsync(CT))?.MediaType);
	}

	[TestMethod]
	public async Task ApplyAsync_WhenExists_Updates()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		var result = await repository.ApplyAsync(current =>
		{
			Assert.AreEqual(Value, current);
			return OtherValue;
		}, CT);

		Assert.AreEqual(OtherValue, result);
		Assert.AreEqual(OtherValueJson, await ReadBlobAsync());
		Assert.AreNotEqual(created.ETag, await GetBlobTokenAsync());
	}

	[TestMethod]
	public async Task ApplyAsync_WhenExists_PreservesBlobMetadata()
	{
		var repository = CreateRepository();
		await WriteBlobAsync(ValueJson, null, new(MediaType: "application/json", Metadata: new Dictionary<string, string> { ["a"] = "b" }));

		await repository.ApplyAsync(_ => OtherValue, CT);

		var info = await Blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.IsNotNull(info.Metadata);
		Assert.AreEqual("b", info.Metadata["a"]);
		Assert.AreEqual(OtherValueJson, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task ApplyAsync_ReturningSameInstance_DoesNotWrite()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		var result = await repository.ApplyAsync(current => current, CT);

		Assert.AreEqual(Value, result);
		Assert.AreEqual(created.ETag, await GetBlobTokenAsync());
	}

	[TestMethod]
	public async Task ApplyAsync_ReturningEqualValue_DoesNotWrite()
	{
		var repository = CreateRepository();
		var created = await repository.CreateAsync(Value, CT);

		var result = await repository.ApplyAsync(_ => new Item("value"), CT);

		Assert.AreEqual(Value, result);
		Assert.AreEqual(created.ETag, await GetBlobTokenAsync());
	}

	[TestMethod]
	public async Task ApplyAsync_CustomComparerEqual_DoesNotWriteAndReturnsOldValue()
	{
		var repository = CreateRepository(new NameIgnoreCaseComparer());
		var created = await repository.CreateAsync(Value, CT);

		var result = await repository.ApplyAsync(_ => new Item("VALUE"), CT);

		Assert.AreEqual(Value, result);
		Assert.AreEqual(created.ETag, await GetBlobTokenAsync());
		Assert.AreEqual(ValueJson, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task ApplyAsync_CustomComparerNotEqual_Writes()
	{
		var repository = CreateRepository(new NameIgnoreCaseComparer());
		await repository.CreateAsync(Value, CT);

		var result = await repository.ApplyAsync(_ => OtherValue, CT);

		Assert.AreEqual(OtherValue, result);
		Assert.AreEqual(OtherValueJson, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task ApplyAsync_ReturningNull_WhenExists_Deletes()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);

		var result = await repository.ApplyAsync(_ => null, CT);

		Assert.IsNull(result);
		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ReturningNull_WhenNotExists_DoesNothing()
	{
		var repository = CreateRepository();

		var result = await repository.ApplyAsync(_ => null, CT);

		Assert.IsNull(result);
		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ConflictingWrite_Retries()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(new Item("0"), CT);
		var calls = 0;

		var result = await repository.ApplyAsync(async (current, ct) =>
		{
			if (calls++ == 0)
			{
				// simulate a concurrent writer between read and write
				await repository.ApplyAsync(c => new Item(c!.Name + "a"), ct);
			}

			return new Item(current!.Name + "b");
		}, CT);

		Assert.AreEqual(2, calls);
		Assert.AreEqual(new Item("0ab"), result);
		Assert.AreEqual(new Item("0ab"), await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ConflictingDelete_Retries()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(Value, CT);
		var calls = 0;

		var result = await repository.ApplyAsync(async (current, ct) =>
		{
			if (calls++ == 0)
			{
				await Blob.DeleteAsync(IBlob.AnyConcurrencyToken, ct);
			}

			return current is null ? OtherValue : new Item(current.Name + "!");
		}, CT);

		Assert.AreEqual(2, calls);
		Assert.AreEqual(OtherValue, result);
		Assert.AreEqual(OtherValue, await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task ApplyAsync_ConcurrentAppliers_AllApplied()
	{
		var repository = CreateRepository();
		await repository.CreateAsync(new Item(""), CT);

		await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => repository.ApplyAsync(c => new Item(c!.Name + "x"), CT), CT)));

		Assert.AreEqual(new Item(new string('x', 32)), await repository.GetAsync(CT));
	}

	private sealed class NameIgnoreCaseComparer : IEqualityComparer<Item>
	{
		public bool Equals(Item? x, Item? y) => StringComparer.OrdinalIgnoreCase.Equals(x?.Name, y?.Name);

		public int GetHashCode(Item obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name);
	}
}
