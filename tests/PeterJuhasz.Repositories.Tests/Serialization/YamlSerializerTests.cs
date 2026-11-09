using Microsoft.Extensions.Time.Testing;
using PeterJuhasz.Repositories.Blobs;
using PeterJuhasz.Repositories.InMemory;
using PeterJuhasz.Repositories.Serialization;
using PeterJuhasz.Repositories.Serialization.Yaml;
using PeterJuhasz.Text.Yaml.Parsing;
using PeterJuhasz.Text.Yaml.Serialization;
using System.Buffers;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Serialization;

[TestClass]
public class YamlSerializerOptionsYamlSerializerTests(TestContext testContext)
{
	private static readonly JsonTestItem Value = new(Name: "value", Count: 42);
	private const string ValueYaml = "Name: value\nCount: 42\n";

	private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

	private static readonly ISerializer<JsonTestItem> Serializer = YamlSerializerOptionsYamlSerializer<JsonTestItem>.Default;

	private CancellationToken CT => testContext.CancellationToken;

	[TestMethod]
	public void MediaType_IsYaml()
	{
		Assert.AreEqual("application/yaml", Serializer.MediaType);
	}

	[TestMethod]
	public void Serialize_BufferWriter_WritesYaml()
	{
		Assert.AreEqual(ValueYaml, Encoding.UTF8.GetString(Serializer.SerializeToBufferWriter(Value)));
	}

	[TestMethod]
	public void Serialize_UsesOptions()
	{
		var serializer = new YamlSerializerOptionsYamlSerializer<JsonTestItem>(new() { PropertyNamingPolicy = YamlNamingPolicy.CamelCase });

		Assert.AreEqual("name: value\ncount: 42\n", Encoding.UTF8.GetString(serializer.SerializeToBufferWriter(Value)));
	}

	[TestMethod]
	public void Serialize_NonAscii_WritesUtf8()
	{
		var value = new JsonTestItem(Name: "árvíztűrő", Count: 1);

		Assert.AreEqual(value, Serializer.Deserialize(Serializer.SerializeToBufferWriter(value)));
	}

	[TestMethod]
	public void Deserialize_Span_ReadsYaml()
	{
		Assert.IsTrue(Serializer.Deserialize(Encoding.UTF8.GetBytes(ValueYaml), out var result));
		Assert.AreEqual(Value, result);
	}

	[TestMethod]
	public void Deserialize_Span_SkipsUtf8Bom()
	{
		Assert.IsTrue(Serializer.Deserialize([.. Bom, .. Encoding.UTF8.GetBytes(ValueYaml)], out var result));
		Assert.AreEqual(Value, result);
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_ReadsYaml()
	{
		Assert.AreEqual(Value, Serializer.DeserializeSegmented(Encoding.UTF8.GetBytes(ValueYaml)));
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_SkipsUtf8Bom()
	{
		Assert.AreEqual(Value, Serializer.DeserializeSegmented([.. Bom, .. Encoding.UTF8.GetBytes(ValueYaml)]));
	}

	[TestMethod]
	public void Deserialize_InvalidYaml_Throws()
	{
		Assert.Throws<YamlException>(() => Serializer.Deserialize("a: [1"u8, out _));
	}

	[TestMethod]
	public async Task SerializeAsync_WritesYaml()
	{
		using var stream = new MemoryStream();

		await Serializer.SerializeAsync(Value, stream, CT);

		Assert.AreEqual(ValueYaml, Encoding.UTF8.GetString(stream.ToArray()));
	}

	[TestMethod]
	public async Task DeserializeAsync_ReadsYaml()
	{
		using var stream = new MemoryStream([.. Bom, .. Encoding.UTF8.GetBytes(ValueYaml)]);

		Assert.AreEqual(Value, await Serializer.DeserializeAsync(stream, CT));
	}

	[TestMethod]
	public async Task DeserializeAsync_Null_Throws()
	{
		using var stream = new MemoryStream("null"u8.ToArray());

		await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Serializer.DeserializeAsync(stream, CT));
	}
}

[TestClass]
public class YamlSerializerOptionsYamlCollectionSerializerTests(TestContext testContext)
{
	private static readonly JsonTestItem[] Items = [new(Name: "a", Count: 1), new(Name: "b", Count: 2)];
	private const string ItemsYaml = "- Name: a\n  Count: 1\n- Name: b\n  Count: 2\n";

	private static readonly ICollectionSerializer<JsonTestItem> Serializer = YamlSerializerOptionsYamlCollectionSerializer<JsonTestItem>.Default;

	private CancellationToken CT => testContext.CancellationToken;

	private static string Serialize(IReadOnlyCollection<JsonTestItem> items)
	{
		var writer = new ArrayBufferWriter<byte>();
		Serializer.Serialize(items, writer);
		return Encoding.UTF8.GetString(writer.WrittenSpan);
	}

	[TestMethod]
	public void Serialize_WritesYamlSequence()
	{
		Assert.AreEqual(ItemsYaml, Serialize(Items));
	}

	[TestMethod]
	public void Serialize_Empty_WritesEmptySequence()
	{
		Assert.AreEqual("[]\n", Serialize([]));
	}

	[TestMethod]
	public void Deserialize_Span_ReadsYamlSequence()
	{
		Assert.IsTrue(Serializer.Deserialize(Encoding.UTF8.GetBytes(ItemsYaml), out var result));
		Assert.AreSequenceEqual(Items, result.ToArray());
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_ReadsYamlSequence()
	{
		Assert.IsTrue(Serializer.Deserialize(SerializerTestHelpers.Segmented(Encoding.UTF8.GetBytes(ItemsYaml)), out var result));
		Assert.AreSequenceEqual(Items, result.ToArray());
	}

	[TestMethod]
	public void Deserialize_Span_SkipsUtf8Bom()
	{
		Assert.IsTrue(Serializer.Deserialize([.. Utf8Bom.Preamble, .. Encoding.UTF8.GetBytes(ItemsYaml)], out var result));
		Assert.AreSequenceEqual(Items, result.ToArray());
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_SkipsUtf8Bom()
	{
		Assert.IsTrue(Serializer.Deserialize(SerializerTestHelpers.Segmented([.. Utf8Bom.Preamble, .. Encoding.UTF8.GetBytes(ItemsYaml)]), out var result));
		Assert.AreSequenceEqual(Items, result.ToArray());
	}

	[TestMethod]
	public async Task DeserializeAsync_SkipsUtf8Bom()
	{
		using var stream = new MemoryStream([.. Utf8Bom.Preamble, .. Encoding.UTF8.GetBytes(ItemsYaml)]);

		var result = await Serializer.DeserializeAsync(stream, CT);

		Assert.AreSequenceEqual(Items, result.ToArray());
	}

	[TestMethod]
	public async Task SerializeAsync_WritesYamlSequence()
	{
		using var stream = new MemoryStream();

		await Serializer.SerializeAsync(Items, stream, CT);

		Assert.AreEqual(ItemsYaml, Encoding.UTF8.GetString(stream.ToArray()));
	}

	[TestMethod]
	public async Task DeserializeAsyncEnumerable_SkipsNullItems()
	{
		using var stream = new MemoryStream("- null\n- Name: a\n  Count: 1\n- null\n"u8.ToArray());

		var result = await Serializer.DeserializeAsyncEnumerable(stream, CT).ToListAsync(CT);

		Assert.AreSequenceEqual([Items[0]], result);
	}
}

[TestClass]
public class YamlExtensionsTests(TestContext testContext)
{
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

	private CancellationToken CT => testContext.CancellationToken;

	private InMemoryBlob Blob => field ??= new("test.yaml", _time);

	[TestMethod]
	public async Task AsYamlObjectRepository_RoundTrips()
	{
		var repository = Blob.AsYamlObjectRepository<JsonTestItem>();

		await repository.StoreAsync(new(Name: "a", Count: 1), concurrencyToken: null, CT);

		Assert.AreEqual(new JsonTestItem(Name: "a", Count: 1), await repository.GetAsync(CT));
	}

	[TestMethod]
	public async Task AsYamlCollectionRepository_RoundTrips()
	{
		var repository = Blob.AsYamlCollectionRepository<JsonTestItem>();

		await repository.AddAsync(new(Name: "a", Count: 1), CT);
		await repository.AddAsync(new(Name: "b", Count: 2), CT);

		Assert.AreSequenceEqual([new(Name: "a", Count: 1), new(Name: "b", Count: 2)], (await repository.ListAsync(CT)).ToArray());
	}

	[TestMethod]
	public async Task AsYamlSetRepository_RoundTrips()
	{
		var repository = Blob.AsYamlSetRepository<string>();

		await repository.AddAsync("a", CT);

		Assert.IsTrue(await repository.ContainsAsync("a", CT));
		Assert.IsFalse(await repository.ContainsAsync("b", CT));
	}
}
