using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization;
using System.Buffers;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Blobs;

[TestClass]
public class BlobCounterTests(TestContext testContext)
{
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlob Blob => field ??= new("test", _time);

	private IDistributedCounter CreateCounter() => Blob.AsCounter();

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
		var counter = new BlobCounter(Blob, Utf8FormattableSerializer<int>.Int32Serializer);

		Assert.AreSame(Blob, counter.Blob);
	}

	// GetAsync

	[TestMethod]
	public async Task GetAsync_WhenNotExists_ReturnsZero()
	{
		var counter = CreateCounter();

		Assert.AreEqual(0, await counter.GetAsync(CT));
	}

	[TestMethod]
	public async Task GetAsync_ReadsBlobContent()
	{
		var counter = CreateCounter();
		await Blob.WriteAsync(Encoding.UTF8.GetBytes("42"), null, new(MediaType: "text/plain"), CT);

		Assert.AreEqual(42, await counter.GetAsync(CT));
	}

	[TestMethod]
	public async Task GetAsync_InvalidContent_Throws()
	{
		var counter = CreateCounter();
		await Blob.WriteAsync(Encoding.UTF8.GetBytes("not a number"), null, new(MediaType: "text/plain"), CT);

		await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await counter.GetAsync(CT));
	}

	// IncrementAsync / DecrementAsync

	[TestMethod]
	public async Task IncrementAsync_WhenNotExists_CreatesBlob()
	{
		var counter = CreateCounter();

		Assert.AreEqual(1, await counter.IncrementAsync(CT));

		Assert.AreEqual("1", await ReadBlobAsync());
		Assert.AreEqual("text/plain", (await Blob.GetInfoAsync(CT))?.MediaType);
		Assert.AreEqual(1, await counter.GetAsync(CT));
	}

	[TestMethod]
	public async Task IncrementAsync_WhenExists_Increments()
	{
		var counter = CreateCounter();
		await counter.IncrementAsync(CT);

		Assert.AreEqual(2, await counter.IncrementAsync(CT));

		Assert.AreEqual("2", await ReadBlobAsync());
	}

	[TestMethod]
	public async Task IncrementAsync_WithDelta_AddsDelta()
	{
		var counter = CreateCounter();
		await counter.IncrementAsync(10, CT);

		Assert.AreEqual(15, await counter.IncrementAsync(5, CT));

		Assert.AreEqual(15, await counter.GetAsync(CT));
	}

	[TestMethod]
	public async Task DecrementAsync_Decrements()
	{
		var counter = CreateCounter();
		await counter.IncrementAsync(3, CT);

		Assert.AreEqual(2, await counter.DecrementAsync(CT));
		Assert.AreEqual(0, await counter.DecrementAsync(2, CT));
	}

	[TestMethod]
	public async Task DecrementAsync_BelowZero_StoresNegativeValue()
	{
		var counter = CreateCounter();

		Assert.AreEqual(-1, await counter.DecrementAsync(CT));

		Assert.AreEqual("-1", await ReadBlobAsync());
		Assert.AreEqual(-1, await counter.GetAsync(CT));
	}

	[TestMethod]
	public async Task DecrementAsync_ToZero_DeletesBlob()
	{
		var counter = CreateCounter();
		await counter.IncrementAsync(CT);

		Assert.AreEqual(0, await counter.DecrementAsync(CT));

		Assert.IsFalse(await Blob.ExistsAsync(CT));
		Assert.AreEqual(0, await counter.GetAsync(CT));
	}

	[TestMethod]
	public async Task IncrementAsync_ConcurrentIncrements_AllApplied()
	{
		var counter = CreateCounter();

		var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await counter.IncrementAsync(CT), CT)));

		Assert.AreEqual(32, await counter.GetAsync(CT));
		Assert.AreSequenceEqual(Enumerable.Range(1, 32).ToArray(), results.Order().ToArray());
	}

	// SetAsync / ResetAsync

	[TestMethod]
	public async Task SetAsync_StoresValue()
	{
		var counter = CreateCounter();

		await counter.SetAsync(7, CT);

		Assert.AreEqual("7", await ReadBlobAsync());
		Assert.AreEqual(7, await counter.GetAsync(CT));
	}

	[TestMethod]
	public async Task SetAsync_SameValue_DoesNotWrite()
	{
		var counter = CreateCounter();
		await counter.SetAsync(7, CT);
		var token = await GetBlobTokenAsync();

		await counter.SetAsync(7, CT);

		Assert.AreEqual(token, await GetBlobTokenAsync());
	}

	[TestMethod]
	public async Task SetAsync_Zero_DeletesBlob()
	{
		var counter = CreateCounter();
		await counter.SetAsync(7, CT);

		await counter.SetAsync(0, CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	[TestMethod]
	public async Task ResetAsync_WhenExists_DeletesBlob()
	{
		var counter = CreateCounter();
		await counter.IncrementAsync(5, CT);

		await counter.ResetAsync(CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
		Assert.AreEqual(0, await counter.GetAsync(CT));
	}

	[TestMethod]
	public async Task ResetAsync_WhenNotExists_DoesNothing()
	{
		var counter = CreateCounter();

		await counter.ResetAsync(CT);

		Assert.IsFalse(await Blob.ExistsAsync(CT));
	}

	// ApplyAsync

	[TestMethod]
	public async Task ApplyAsync_WhenNotExists_TransformsZero()
	{
		var counter = CreateCounter();
		int? seen = null;

		var result = await counter.ApplyAsync(value =>
		{
			seen = value;
			return value + 10;
		}, CT);

		Assert.AreEqual(0, seen);
		Assert.AreEqual(10, result);
		Assert.AreEqual("10", await ReadBlobAsync());
	}

	[TestMethod]
	public async Task ApplyAsync_PreservesBlobMetadata()
	{
		var counter = CreateCounter();
		await Blob.WriteAsync(Encoding.UTF8.GetBytes("1"), null, new(MediaType: "text/plain", Metadata: new Dictionary<string, string> { ["a"] = "b" }), CT);

		await counter.IncrementAsync(CT);

		var info = await Blob.GetInfoAsync(CT);
		Assert.IsNotNull(info);
		Assert.IsNotNull(info.Metadata);
		Assert.AreEqual("b", info.Metadata["a"]);
		Assert.AreEqual("2", await ReadBlobAsync());
	}

	[TestMethod]
	public async Task ApplyAsync_ConflictingWrite_Retries()
	{
		var counter = CreateCounter();
		await counter.SetAsync(1, CT);
		var calls = 0;

		var result = await counter.ApplyAsync(value =>
		{
			if (calls++ == 0)
			{
				// simulate a concurrent writer between read and write
				Blob.WriteAsync(Encoding.UTF8.GetBytes("10"), IBlob.AnyConcurrencyToken, new(MediaType: "text/plain"), CT).GetAwaiter().GetResult();
			}

			return value * 2;
		}, CT);

		Assert.AreEqual(2, calls);
		Assert.AreEqual(20, result);
		Assert.AreEqual(20, await counter.GetAsync(CT));
	}

	// Serializers

	[TestMethod]
	public async Task BinarySerializer_RoundTrips()
	{
		var counter = Blob.AsCounter(BigEndianBinaryIntegerSerializer<int>.Int32Serializer);

		await counter.IncrementAsync(258, CT);

		var result = await Blob.ReadAsync(CT);
		Assert.IsNotNull(result);
		Assert.AreSequenceEqual(new byte[] { 0, 0, 1, 2 }, result.Value.ToArray());
		Assert.AreEqual("application/octet-stream", result.Value.MediaType);
		Assert.AreEqual(258, await counter.GetAsync(CT));
		Assert.AreEqual(259, await counter.IncrementAsync(CT));
	}

	[TestMethod]
	public async Task BinarySerializer_NegativeValue_RoundTrips()
	{
		var counter = Blob.AsCounter(BigEndianBinaryIntegerSerializer<int>.Int32Serializer);

		await counter.DecrementAsync(CT);

		Assert.AreEqual(-1, await counter.GetAsync(CT));
	}

	[TestMethod]
	public async Task FormattableSerializer_RoundTrips()
	{
		var counter = Blob.AsCounter(FormattableSerializer<int>.Int32Serializer);

		await counter.IncrementAsync(123, CT);

		Assert.AreEqual("123", await ReadBlobAsync());
		Assert.AreEqual(123, await counter.GetAsync(CT));
	}

	[TestMethod]
	[DataRow(int.MinValue)]
	[DataRow(int.MaxValue)]
	public async Task Utf8FormattableSerializer_ExtremeValues_RoundTrip(int value)
	{
		var counter = CreateCounter();

		await counter.SetAsync(value, CT);

		Assert.AreEqual(value.ToString(System.Globalization.CultureInfo.InvariantCulture), await ReadBlobAsync());
		Assert.AreEqual(value, await counter.GetAsync(CT));
	}
}
