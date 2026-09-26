using PeterJuhasz.Repositories.Serialization;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace PeterJuhasz.Repositories.Tests.Serialization;

[TestClass]
public class JsonTypeInfoJsonCollectionSerializerTests(TestContext testContext)
{
	private static readonly JsonTestItem[] Items = [new("a", 1), new("b", 2)];
	private const string ItemsJson = """[{"name":"a","count":1},{"name":"b","count":2}]""";

	private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

	private static readonly ICollectionSerializer<JsonTestItem> Serializer = new JsonTypeInfoJsonCollectionSerializer<JsonTestItem>(
		JsonTestContext.Default.IReadOnlyCollectionJsonTestItem,
		JsonTestContext.Default.JsonTestItem
	);

	private CancellationToken CT => testContext.CancellationToken;

	private static string Serialize(ICollectionSerializer<JsonTestItem> serializer, IReadOnlyCollection<JsonTestItem> items)
	{
		var writer = new ArrayBufferWriter<byte>();
		serializer.Serialize(items, writer);
		return Encoding.UTF8.GetString(writer.WrittenSpan);
	}

	[TestMethod]
	public void MediaType_IsJson()
	{
		Assert.AreEqual("application/json", Serializer.MediaType);
	}

	[TestMethod]
	public void Serialize_WritesJsonArray()
	{
		Assert.AreEqual(ItemsJson, Serialize(Serializer, Items));
	}

	[TestMethod]
	public void Serialize_Empty_WritesEmptyArray()
	{
		Assert.AreEqual("[]", Serialize(Serializer, []));
	}

	[TestMethod]
	public void Constructor_FromContext_UsesContextTypeInfo()
	{
		var serializer = new JsonTypeInfoJsonCollectionSerializer<JsonTestItem>(JsonTestContext.Default);

		Assert.AreEqual(ItemsJson, Serialize(serializer, Items));
	}

	[TestMethod]
	public void Deserialize_Span_ReadsJsonArray()
	{
		Assert.IsTrue(Serializer.Deserialize(Encoding.UTF8.GetBytes(ItemsJson), out var result));
		Assert.AreSequenceEqual(Items, result.ToArray());
	}

	[TestMethod]
	public void Deserialize_Span_SkipsUtf8Bom()
	{
		Assert.IsTrue(Serializer.Deserialize([.. Bom, .. Encoding.UTF8.GetBytes(ItemsJson)], out var result));
		Assert.AreSequenceEqual(Items, result.ToArray());
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_ReadsJsonArray()
	{
		Assert.IsTrue(Serializer.Deserialize(SerializerTestHelpers.Segmented(Encoding.UTF8.GetBytes(ItemsJson)), out var result));
		Assert.AreSequenceEqual(Items, result.ToArray());
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_SkipsUtf8Bom()
	{
		Assert.IsTrue(Serializer.Deserialize(SerializerTestHelpers.Segmented([.. Bom, .. Encoding.UTF8.GetBytes(ItemsJson)]), out var result));
		Assert.AreSequenceEqual(Items, result.ToArray());
	}

	[TestMethod]
	public void Deserialize_InvalidJson_Throws()
	{
		Assert.Throws<JsonException>(() => Serializer.Deserialize("not json"u8, out _));
	}

	[TestMethod]
	public async Task SerializeAsync_WritesJsonArray()
	{
		using var stream = new MemoryStream();

		await Serializer.SerializeAsync(Items, stream, CT);

		Assert.AreEqual(ItemsJson, Encoding.UTF8.GetString(stream.ToArray()));
	}

	[TestMethod]
	public async Task DeserializeAsync_ReadsJsonArray()
	{
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ItemsJson));

		var result = await Serializer.DeserializeAsync(stream, CT);

		Assert.AreSequenceEqual(Items, result.ToArray());
	}

	[TestMethod]
	public async Task DeserializeAsync_Null_Throws()
	{
		using var stream = new MemoryStream("null"u8.ToArray());

		await Assert.ThrowsExactlyAsync<JsonException>(() => Serializer.DeserializeAsync(stream, CT));
	}

	[TestMethod]
	public async Task DeserializeAsyncEnumerable_YieldsItems()
	{
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ItemsJson));

		var result = await Serializer.DeserializeAsyncEnumerable(stream, CT).ToListAsync(CT);

		Assert.AreSequenceEqual(Items, result);
	}

	[TestMethod]
	public async Task DeserializeAsyncEnumerable_SkipsNullItems()
	{
		using var stream = new MemoryStream("""[null,{"name":"a","count":1},null]"""u8.ToArray());

		var result = await Serializer.DeserializeAsyncEnumerable(stream, CT).ToListAsync(CT);

		Assert.AreSequenceEqual(new[] { Items[0] }, result);
	}
}
