using PeterJuhasz.Repositories.Serialization;

namespace PeterJuhasz.Repositories.Tests.Serialization;

[TestClass]
public class BigEndianBinaryIntegerSerializerTests(TestContext testContext)
{
	private static readonly ISerializer<int> Serializer = BigEndianBinaryIntegerSerializer<int>.Int32Serializer;

	private CancellationToken CT => testContext.CancellationToken;

	[TestMethod]
	public void MediaType_IsOctetStream()
	{
		Assert.AreEqual("application/octet-stream", Serializer.MediaType);
	}

	[TestMethod]
	public void Serialize_BufferWriter_WritesBigEndian()
	{
		Assert.AreSequenceEqual(new byte[] { 0, 0, 1, 2 }, Serializer.SerializeToBufferWriter(258));
	}

	[TestMethod]
	public void Serialize_Span_WritesBigEndian()
	{
		Assert.AreSequenceEqual(new byte[] { 0, 0, 1, 2 }, Serializer.SerializeToSpan(258));
	}

	[TestMethod]
	public void Serialize_Span_TooSmall_ReturnsFalse()
	{
		Assert.IsFalse(Serializer.Serialize(258, new byte[3], out _));
	}

	[TestMethod]
	[DataRow(0)]
	[DataRow(258)]
	[DataRow(int.MaxValue)]
	public void TryGetMaximumSerializedLength_Int32_IsExact(int value)
	{
		Assert.IsTrue(Serializer.TryGetMaximumSerializedLength(value, out var size));
		Assert.AreEqual(sizeof(int), size);
		Assert.HasCount(size, Serializer.SerializeToSpan(value));
	}

	[TestMethod]
	[DataRow(long.MinValue)]
	[DataRow(long.MaxValue)]
	public void TryGetMaximumSerializedLength_Int64_IsExact(long value)
	{
		var serializer = new BigEndianBinaryIntegerSerializer<long>(unsigned: false);

		Assert.IsTrue(serializer.TryGetMaximumSerializedLength(value, out var size));
		Assert.AreEqual(sizeof(long), size);
		Assert.HasCount(size, serializer.SerializeToSpan(value));
	}

	[TestMethod]
	public void TryGetMaximumSerializedLength_Byte_IsExact()
	{
		var serializer = new BigEndianBinaryIntegerSerializer<byte>(unsigned: true);

		Assert.IsTrue(serializer.TryGetMaximumSerializedLength(200, out var size));
		Assert.AreEqual(sizeof(byte), size);
	}

	[TestMethod]
	public void Deserialize_Span_ReadsBigEndian()
	{
		Assert.IsTrue(Serializer.Deserialize([0, 0, 1, 2], out var value));
		Assert.AreEqual(258, value);
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_ReadsBigEndian()
	{
		Assert.AreEqual(258, Serializer.DeserializeSegmented([0, 0, 1, 2]));
	}

	[TestMethod]
	[DataRow(0)]
	[DataRow(1)]
	[DataRow(int.MaxValue)]
	public void Int32Serializer_RoundTrips(int value)
	{
		Assert.IsTrue(Serializer.Deserialize(Serializer.SerializeToSpan(value), out var result));
		Assert.AreEqual(value, result);
	}

	[TestMethod]
	[DataRow(-1)]
	[DataRow(int.MinValue)]
	public void Int32Serializer_NegativeValue_RoundTrips(int value)
	{
		Assert.IsTrue(Serializer.Deserialize(Serializer.SerializeToSpan(value), out var result));
		Assert.AreEqual(value, result);
	}

	[TestMethod]
	[DataRow(long.MinValue)]
	[DataRow(-1L)]
	[DataRow(long.MaxValue)]
	public void Signed_Int64_RoundTrips(long value)
	{
		var serializer = new BigEndianBinaryIntegerSerializer<long>(unsigned: false);

		Assert.IsTrue(serializer.Deserialize(serializer.SerializeToSpan(value), out var result));
		Assert.AreEqual(value, result);
	}

	[TestMethod]
	public async Task SerializeAsync_NotSupported()
	{
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Serializer.SerializeAsync(1, Stream.Null, CT));
	}

	[TestMethod]
	public async Task DeserializeAsync_NotSupported()
	{
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => Serializer.DeserializeAsync(Stream.Null, CT));
	}
}
