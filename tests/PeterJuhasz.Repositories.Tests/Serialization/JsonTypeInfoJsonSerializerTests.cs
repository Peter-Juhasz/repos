using PeterJuhasz.Repositories.Serialization;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Text;
using System.Text.Json;

namespace PeterJuhasz.Repositories.Tests.Serialization;

[TestClass]
public class JsonTypeInfoJsonSerializerTests(TestContext testContext)
{
	private static readonly JsonTestItem Value = new("value", 42);
	private const string ValueJson = """{"name":"value","count":42}""";

	private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

	private static readonly ISerializer<JsonTestItem> Serializer = new JsonTypeInfoJsonSerializer<JsonTestItem>(JsonTestContext.Default.JsonTestItem);

	private CancellationToken CT => testContext.CancellationToken;

	[TestMethod]
	public void MediaType_IsJson()
	{
		Assert.AreEqual("application/json", Serializer.MediaType);
	}

	[TestMethod]
	public void Serialize_BufferWriter_WritesJson()
	{
		Assert.AreEqual(ValueJson, Encoding.UTF8.GetString(Serializer.SerializeToBufferWriter(Value)));
	}

	[TestMethod]
	public void Constructor_FromContext_UsesContextTypeInfo()
	{
		var serializer = new JsonTypeInfoJsonSerializer<JsonTestItem>(JsonTestContext.Default);

		Assert.AreEqual(ValueJson, Encoding.UTF8.GetString(serializer.SerializeToBufferWriter(Value)));
	}

	[TestMethod]
	public void Serialize_Repeatedly_ProducesIndependentOutputs()
	{
		var first = Serializer.SerializeToBufferWriter(Value);
		var second = Serializer.SerializeToBufferWriter(new JsonTestItem("other", 1));

		Assert.AreEqual(ValueJson, Encoding.UTF8.GetString(first));
		Assert.AreEqual("""{"name":"other","count":1}""", Encoding.UTF8.GetString(second));
	}

	[TestMethod]
	public void TryGetMaximumSerializedLength_ReturnsFalse()
	{
		Assert.IsFalse(Serializer.TryGetMaximumSerializedLength(Value, out _));
	}

	[TestMethod]
	public void Deserialize_Span_ReadsJson()
	{
		Assert.IsTrue(Serializer.Deserialize(Encoding.UTF8.GetBytes(ValueJson), out var result));
		Assert.AreEqual(Value, result);
	}

	[TestMethod]
	public void Deserialize_Span_SkipsUtf8Bom()
	{
		Assert.IsTrue(Serializer.Deserialize([.. Bom, .. Encoding.UTF8.GetBytes(ValueJson)], out var result));
		Assert.AreEqual(Value, result);
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_ReadsJson()
	{
		Assert.AreEqual(Value, Serializer.DeserializeSegmented(Encoding.UTF8.GetBytes(ValueJson)));
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_SkipsUtf8Bom()
	{
		Assert.AreEqual(Value, Serializer.DeserializeSegmented([.. Bom, .. Encoding.UTF8.GetBytes(ValueJson)]));
	}

	[TestMethod]
	public void Deserialize_InvalidJson_Throws()
	{
		Assert.Throws<JsonException>(() => Serializer.Deserialize("not json"u8, out _));
	}

	[TestMethod]
	public async Task SerializeAsync_WritesJson()
	{
		using var stream = new MemoryStream();

		await Serializer.SerializeAsync(Value, stream, CT);

		Assert.AreEqual(ValueJson, Encoding.UTF8.GetString(stream.ToArray()));
	}

	[TestMethod]
	public async Task DeserializeAsync_ReadsJson()
	{
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ValueJson));

		Assert.AreEqual(Value, await Serializer.DeserializeAsync(stream, CT));
	}

	[TestMethod]
	public async Task DeserializeAsync_Null_Throws()
	{
		using var stream = new MemoryStream("null"u8.ToArray());

		await Assert.ThrowsExactlyAsync<JsonException>(() => Serializer.DeserializeAsync(stream, CT));
	}
}
