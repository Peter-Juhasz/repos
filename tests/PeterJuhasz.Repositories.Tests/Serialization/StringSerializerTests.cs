using PeterJuhasz.Repositories.Serialization;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Serialization;

[TestClass]
public class StringSerializerTests(TestContext testContext)
{
	private const string Value = "héllo wörld €😀";

	private static readonly ISerializer<string> Serializer = StringSerializer.Utf8;

	private CancellationToken CT => testContext.CancellationToken;

	[TestMethod]
	public void MediaType_IsTextPlain()
	{
		Assert.AreEqual("text/plain", Serializer.MediaType);
	}

	[TestMethod]
	public void Serialize_BufferWriter_WritesEncodedBytes()
	{
		Assert.AreSequenceEqual(Encoding.UTF8.GetBytes(Value), Serializer.SerializeToBufferWriter(Value));
	}

	[TestMethod]
	public void Serialize_Span_WritesEncodedBytes()
	{
		Assert.AreSequenceEqual(Encoding.UTF8.GetBytes(Value), Serializer.SerializeToSpan(Value));
	}

	[TestMethod]
	public void Serialize_Span_TooSmall_ReturnsFalse()
	{
		Assert.IsFalse(Serializer.Serialize(Value, new byte[Encoding.UTF8.GetByteCount(Value) - 1], out _));
	}

	[TestMethod]
	public void Serialize_Empty_WritesNothing()
	{
		Assert.IsEmpty(Serializer.SerializeToBufferWriter(""));
		Assert.IsEmpty(Serializer.SerializeToSpan(""));
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("abc")]
	[DataRow(Value)]
	public void TryGetMaximumSerializedLength_Fits(string value)
	{
		Assert.IsTrue(Serializer.TryGetMaximumSerializedLength(value, out var size));
		Assert.IsGreaterThanOrEqualTo(Encoding.UTF8.GetByteCount(value), size);
	}

	[TestMethod]
	public void Serialize_Utf16Encoding_UsesEncoding()
	{
		var serializer = new StringSerializer(Encoding.Unicode);

		Assert.AreSequenceEqual(Encoding.Unicode.GetBytes(Value), serializer.SerializeToSpan(Value));
		Assert.IsTrue(serializer.Deserialize(Encoding.Unicode.GetBytes(Value), out var result));
		Assert.AreEqual(Value, result);
	}

	[TestMethod]
	public void Deserialize_Span_DecodesBytes()
	{
		Assert.IsTrue(Serializer.Deserialize(Encoding.UTF8.GetBytes(Value), out var result));
		Assert.AreEqual(Value, result);
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_DecodesCharactersSplitAcrossSegments()
	{
		Assert.AreEqual(Value, Serializer.DeserializeSegmented(Encoding.UTF8.GetBytes(Value)));
	}

	[TestMethod]
	public void Deserialize_Empty_ReturnsEmpty()
	{
		Assert.IsTrue(Serializer.Deserialize(ReadOnlySpan<byte>.Empty, out var result));
		Assert.AreEqual("", result);
	}

	[TestMethod]
	public async Task SerializeAsync_WritesEncodedBytes()
	{
		using var stream = new MemoryStream();

		await Serializer.SerializeAsync(Value, stream, CT);

		Assert.AreSequenceEqual(Encoding.UTF8.GetBytes(Value), stream.ToArray());
	}

	[TestMethod]
	public async Task DeserializeAsync_DecodesBytes()
	{
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Value));

		Assert.AreEqual(Value, await Serializer.DeserializeAsync(stream, CT));
	}

	[TestMethod]
	public async Task SerializeAsync_ThenDeserializeAsync_RoundTrips()
	{
		using var stream = new MemoryStream();
		await Serializer.SerializeAsync(Value, stream, CT);
		stream.Position = 0;

		Assert.AreEqual(Value, await Serializer.DeserializeAsync(stream, CT));
	}
}
