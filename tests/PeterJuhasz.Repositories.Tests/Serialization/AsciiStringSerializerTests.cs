using PeterJuhasz.Repositories.Serialization;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Serialization;

[TestClass]
public class AsciiStringSerializerTests(TestContext testContext)
{
	private const string Value = "hello, world!";

	private static readonly ISerializer<string> Serializer = AsciiStringSerializer.Instance;

	private CancellationToken CT => testContext.CancellationToken;

	[TestMethod]
	public void MediaType_IsTextPlain()
	{
		Assert.AreEqual("text/plain", Serializer.MediaType);
	}

	[TestMethod]
	public void Serialize_BufferWriter_WritesAsciiBytes()
	{
		Assert.AreSequenceEqual(Encoding.ASCII.GetBytes(Value), Serializer.SerializeToBufferWriter(Value));
	}

	[TestMethod]
	public void Serialize_Span_WritesAsciiBytes()
	{
		Assert.AreSequenceEqual(Encoding.ASCII.GetBytes(Value), Serializer.SerializeToSpan(Value));
	}

	[TestMethod]
	public void Serialize_Span_TooSmall_ReturnsFalse()
	{
		Assert.IsFalse(Serializer.Serialize(Value, new byte[Value.Length - 1], out _));
	}

	[TestMethod]
	public void Serialize_Empty_WritesNothing()
	{
		Assert.IsEmpty(Serializer.SerializeToBufferWriter(""));
		Assert.IsEmpty(Serializer.SerializeToSpan(""));
	}

	[TestMethod]
	[DataRow("")]
	[DataRow(Value)]
	public void TryGetMaximumSerializedLength_IsExact(string value)
	{
		Assert.IsTrue(Serializer.TryGetMaximumSerializedLength(value, out var size));
		Assert.AreEqual(value.Length, size);
		Assert.HasCount(size, Serializer.SerializeToSpan(value));
	}

	[TestMethod]
	public void Serialize_Span_NonAscii_ReturnsFalse()
	{
		Assert.IsFalse(Serializer.Serialize("héllo", new byte[16], out _));
	}

	[TestMethod]
	public void Serialize_BufferWriter_NonAscii_Throws()
	{
		Assert.Throws<Exception>(() => Serializer.SerializeToBufferWriter("héllo"));
	}

	[TestMethod]
	public void Deserialize_Span_DecodesAscii()
	{
		Assert.IsTrue(Serializer.Deserialize(Encoding.ASCII.GetBytes(Value), out var result));
		Assert.AreEqual(Value, result);
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_DecodesAscii()
	{
		Assert.AreEqual(Value, Serializer.DeserializeSegmented(Encoding.ASCII.GetBytes(Value)));
	}

	[TestMethod]
	public void Deserialize_Empty_ReturnsEmpty()
	{
		Assert.IsTrue(Serializer.Deserialize(ReadOnlySpan<byte>.Empty, out var result));
		Assert.AreEqual("", result);
	}

	[TestMethod]
	public void Deserialize_NonAscii_ReturnsFalse()
	{
		Assert.IsFalse(Serializer.Deserialize(Encoding.UTF8.GetBytes("héllo"), out _));
	}

	[TestMethod]
	public async Task SerializeAsync_NotSupported()
	{
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Serializer.SerializeAsync(Value, Stream.Null, CT));
	}

	[TestMethod]
	public async Task DeserializeAsync_NotSupported()
	{
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Serializer.DeserializeAsync(Stream.Null, CT));
	}
}
