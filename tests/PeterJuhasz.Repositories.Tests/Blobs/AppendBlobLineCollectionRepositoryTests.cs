using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Text;
using System.Text.Json;

namespace PeterJuhasz.Repositories.Tests.Blobs;

[TestClass]
public class AppendBlobLineCollectionRepositoryTests(TestContext testContext)
{
	public sealed record class Item(string Name, int Count);

	private static readonly Item A = new("a", 1);
	private static readonly Item B = new("b", 2);
	private static readonly Item C = new("c", 3);

	private const string ALine = """{"name":"a","count":1}""" + "\n";
	private const string BLine = """{"name":"b","count":2}""" + "\n";

	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryAppendBlob Blob => field ??= new("test", _time);

	private ICollectionRepository<Item> CreateRepository() =>
		new AppendBlobLineCollectionRepository<Item>(Blob, new JsonSerializerOptionsJsonSerializer<Item>(JsonSerializerOptions.Web));

	private Task AppendBlobAsync(string content) => Blob.AppendAsync(Encoding.UTF8.GetBytes(content), CT);

	private async Task<string> ReadBlobAsync()
	{
		var result = await Blob.OpenReadAsync(CT);
		Assert.IsNotNull(result);
		await using var stream = result.Value;
		using var reader = new StreamReader(stream);
		return await reader.ReadToEndAsync(CT);
	}

	private async Task<string?> GetBlobTokenAsync() => (await Blob.GetInfoAsync(CT))?.ConcurrencyToken;

	// Extensions

	[TestMethod]
	public void AsJsonLineCollectionRepository_CreatesLineRepository()
	{
		var repository = Blob.AsJsonLineCollectionRepository<Item>(JsonSerializerOptions.Web);

		Assert.IsInstanceOfType<AppendBlobLineCollectionRepository<Item>>(repository);
	}

	[TestMethod]
	public void AsLineCollectionRepository_CreatesLineRepository()
	{
		var repository = Blob.AsLineCollectionRepository(new JsonSerializerOptionsJsonSerializer<Item>(JsonSerializerOptions.Web));

		Assert.IsInstanceOfType<AppendBlobLineCollectionRepository<Item>>(repository);
	}

	// Empty blob

	[TestMethod]
	public async Task EmptyBlob_IsEmpty()
	{
		var repository = CreateRepository();

		var listed = await repository.ListWithVersionAsync(CT);
		Assert.IsEmpty(listed.Value);
		Assert.IsNull(listed.ETag);
		Assert.IsNull(await repository.GetVersionAsync(CT));
		Assert.IsFalse(await repository.AnyAsync(CT));
		Assert.AreEqual(0, await repository.CountAsync(CT));
		Assert.IsEmpty(await repository.AsAsyncEnumerableAsync(CT).ToListAsync(CT));
		Assert.IsNull(await repository.GetOrDefaultAsync(_ => true, CT));
		Assert.IsNull(await repository.RawStreamAsync(CT));
	}

	// Reading

	[TestMethod]
	public async Task ListWithVersionAsync_DeserializesLines()
	{
		var repository = CreateRepository();
		await AppendBlobAsync(ALine + BLine);
		var token = await GetBlobTokenAsync();

		var result = await repository.ListWithVersionAsync(CT);

		Assert.AreSequenceEqual(new[] { A, B }, result.Value);
		Assert.AreEqual(token, result.ETag);
		Assert.AreEqual(token, await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task ListWithVersionAsync_EmptyBlob_ReturnsEmptyWithVersion()
	{
		var repository = CreateRepository();
		await AppendBlobAsync("");
		var token = await GetBlobTokenAsync();

		var result = await repository.ListWithVersionAsync(CT);

		Assert.IsEmpty(result.Value);
		Assert.AreEqual(token, result.ETag);
	}

	[TestMethod]
	public async Task ListWithVersionAsync_CrLfLineEndings_Deserializes()
	{
		var repository = CreateRepository();
		await AppendBlobAsync(ALine.Replace("\n", "\r\n") + BLine.Replace("\n", "\r\n"));

		Assert.AreSequenceEqual(new[] { A, B }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ListWithVersionAsync_Utf8Bom_Deserializes()
	{
		var repository = CreateRepository();
		await AppendBlobAsync("﻿" + ALine + BLine);

		Assert.AreSequenceEqual(new[] { A, B }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ListWithVersionAsync_LastLineWithoutNewLine_Deserializes()
	{
		var repository = CreateRepository();
		await AppendBlobAsync(ALine + BLine.TrimEnd('\n'));

		Assert.AreSequenceEqual(new[] { A, B }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ListWithVersionAsync_BlankLine_IsSkipped()
	{
		var repository = CreateRepository();
		await AppendBlobAsync(ALine + "\n" + BLine);

		Assert.AreSequenceEqual(new[] { A, B }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ListWithVersionAsync_InvalidJson_Throws()
	{
		var repository = CreateRepository();
		await AppendBlobAsync("not json\n");

		await Assert.ThrowsAsync<JsonException>(() => repository.ListWithVersionAsync(CT));
	}

	[TestMethod]
	public async Task ListWithVersionAsync_ManyItems_DeserializesAll()
	{
		var repository = CreateRepository();
		var items = Enumerable.Range(0, 10_000).Select(i => new Item($"item{i}", i)).ToArray();
		await repository.AddRangeAsync(items, CT);

		Assert.AreSequenceEqual(items, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task AsAsyncEnumerableAsync_YieldsItems()
	{
		var repository = CreateRepository();
		await AppendBlobAsync(ALine + BLine);

		Assert.AreSequenceEqual(new[] { A, B }, await repository.AsAsyncEnumerableAsync(CT).ToListAsync(CT));
	}

	[TestMethod]
	public async Task AsAsyncEnumerableAsync_StopEarly_DoesNotReadRest()
	{
		var repository = CreateRepository();
		await AppendBlobAsync(ALine + "not json\n");

		Assert.AreEqual(A, await repository.AsAsyncEnumerableAsync(CT).FirstAsync(CT));
	}

	[TestMethod]
	public async Task QueryMethods_ReturnMatchingItems()
	{
		var repository = CreateRepository();
		await AppendBlobAsync(ALine + BLine);

		Assert.IsTrue(await repository.AnyAsync(CT));
		Assert.IsTrue(await repository.AnyAsync(i => i.Name == "b", CT));
		Assert.IsFalse(await repository.AnyAsync(i => i.Name == "c", CT));
		Assert.AreEqual(2, await repository.CountAsync(CT));
		Assert.AreEqual(1, await repository.CountAsync(i => i.Count > 1, CT));
		Assert.AreEqual(B, await repository.GetOrDefaultAsync(i => i.Name == "b", CT));
		Assert.IsNull(await repository.GetOrDefaultAsync(i => i.Name == "c", CT));
	}

	// RawStreamAsync

	[TestMethod]
	public async Task RawStreamAsync_ReturnsBlobContentAndInfo()
	{
		var repository = CreateRepository();
		await AppendBlobAsync(ALine + BLine);
		var token = await GetBlobTokenAsync();

		var result = await repository.RawStreamAsync(CT);

		Assert.IsNotNull(result);
		await using var raw = result.Value;
		Assert.AreEqual(token, raw.ETag);
		Assert.AreEqual(_time.GetUtcNow(), raw.LastModified);
		using var reader = new StreamReader(raw.Stream);
		Assert.AreEqual(ALine + BLine, await reader.ReadToEndAsync(CT));
	}

	// AddAsync / AddRangeAsync

	[TestMethod]
	public async Task AddAsync_WhenNotExists_CreatesBlob()
	{
		var repository = CreateRepository();

		Assert.IsTrue(await repository.AddAsync(A, CT));

		Assert.AreEqual(ALine, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task AddAsync_WhenExists_Appends()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);

		Assert.IsTrue(await repository.AddAsync(B, CT));

		Assert.AreEqual(ALine + BLine, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task AddAsync_ChangesVersion()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);
		var version = await repository.GetVersionAsync(CT);

		await repository.AddAsync(B, CT);

		Assert.AreNotEqual(version, await repository.GetVersionAsync(CT));
	}

	[TestMethod]
	public async Task AddAsync_AllowsDuplicates()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);

		await repository.AddAsync(A, CT);

		Assert.AreSequenceEqual(new[] { A, A }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task AddAsync_StringWithNewLine_IsEscaped()
	{
		var repository = CreateRepository();
		var item = new Item("line1\nline2", 1);

		await repository.AddAsync(item, CT);
		await repository.AddAsync(B, CT);

		Assert.AreSequenceEqual(new[] { item, B }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task AddRangeAsync_WhenNotExists_CreatesBlob()
	{
		var repository = CreateRepository();

		await repository.AddRangeAsync([A, B], CT);

		Assert.AreEqual(ALine + BLine, await ReadBlobAsync());
	}

	[TestMethod]
	public async Task AddRangeAsync_WhenExists_Appends()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);

		await repository.AddRangeAsync([B, C], CT);

		Assert.AreSequenceEqual(new[] { A, B, C }, await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task AddRangeAsync_Empty_DoesNotWrite()
	{
		var repository = CreateRepository();

		await repository.AddRangeAsync([], CT);

		Assert.IsNull(await Blob.GetInfoAsync(CT));
	}

	[TestMethod]
	public async Task AddAsync_Concurrent_AllAdded()
	{
		var repository = CreateRepository();

		await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => repository.AddAsync(new Item("x", i), CT), CT)));

		var items = await repository.ListAsync(CT);
		Assert.AreSequenceEqual(Enumerable.Range(0, 32), items.Select(i => i.Count).Order());
	}

	// ClearAsync

	[TestMethod]
	public async Task ClearAsync_WhenExists_DeletesBlob()
	{
		var repository = CreateRepository();
		await repository.AddRangeAsync([A, B], CT);

		await repository.ClearAsync(CT);

		Assert.IsNull(await Blob.GetInfoAsync(CT));
		Assert.IsEmpty(await repository.ListAsync(CT));
	}

	[TestMethod]
	public async Task ClearAsync_WhenNotExists_DoesNothing()
	{
		var repository = CreateRepository();

		await repository.ClearAsync(CT);

		Assert.IsNull(await Blob.GetInfoAsync(CT));
	}

	[TestMethod]
	public async Task ClearAsync_ThenAdd_StartsFresh()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);
		await repository.ClearAsync(CT);

		await repository.AddAsync(B, CT);

		Assert.AreSequenceEqual(new[] { B }, await repository.ListAsync(CT));
	}

	// Unsupported mutations

	[TestMethod]
	public async Task ApplyAsync_Throws()
	{
		var repository = CreateRepository();

		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => repository.ApplyAsync(items => items, CT));
	}

	[TestMethod]
	public async Task UpdateMethods_Throw()
	{
		var repository = CreateRepository();
		await repository.AddAsync(A, CT);

		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => repository.StoreAsync([B], CT));
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => repository.UpdateAsync(_ => true, i => i, CT));
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => repository.AddOrUpdateAsync(A, i => i, CT));
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => repository.DeleteAsync(A, CT));
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => repository.DeleteAsync(_ => true, CT));

		Assert.AreSequenceEqual(new[] { A }, await repository.ListAsync(CT));
	}
}
