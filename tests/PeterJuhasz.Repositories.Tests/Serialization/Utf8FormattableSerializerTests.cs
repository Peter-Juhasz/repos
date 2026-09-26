using PeterJuhasz.Repositories.Serialization;
using System.Globalization;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Serialization;

[TestClass]
public class Utf8FormattableSerializerTests(TestContext testContext)
{
	private static readonly ISerializer<int> Serializer = Utf8FormattableSerializer<int>.Int32Serializer;

	private CancellationToken CT => testContext.CancellationToken;

	[TestMethod]
	public void MediaType_IsTextPlain()
	{
		Assert.AreEqual("text/plain", Serializer.MediaType);
	}

	[TestMethod]
	public void Serialize_BufferWriter_WritesText()
	{
		Assert.AreEqual("-42", Encoding.UTF8.GetString(Serializer.SerializeToBufferWriter(-42)));
	}

	[TestMethod]
	public void Serialize_Span_WritesText()
	{
		Assert.AreEqual("-42", Encoding.UTF8.GetString(Serializer.SerializeToSpan(-42)));
	}

	[TestMethod]
	public void Serialize_Span_TooSmall_ReturnsFalse()
	{
		Assert.IsFalse(Serializer.Serialize(12345, new byte[4], out _));
	}

	[TestMethod]
	public void Serialize_WithFormat_UsesFormat()
	{
		var serializer = new Utf8FormattableSerializer<int>("D5");

		Assert.AreEqual("00042", Encoding.UTF8.GetString(serializer.SerializeToSpan(42)));
		Assert.AreEqual("00042", Encoding.UTF8.GetString(serializer.SerializeToBufferWriter(42)));
	}

	[TestMethod]
	public void Serialize_IsCultureInvariant()
	{
		var serializer = new Utf8FormattableSerializer<double>();

		SerializerTestHelpers.WithCulture("hu-HU", () =>
		{
			Assert.AreEqual("1.5", Encoding.UTF8.GetString(serializer.SerializeToSpan(1.5)));
			Assert.IsTrue(serializer.Deserialize("1.5"u8, out var value));
			Assert.AreEqual(1.5, value);
		});
	}

	[TestMethod]
	[DataRow(int.MinValue)]
	[DataRow(int.MaxValue)]
	public void TryGetMaximumSerializedLength_Int32_Fits(int value)
	{
		Assert.AreEqual(value.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetString(Serializer.SerializeToSpan(value)));
	}

	[TestMethod]
	public void TryGetMaximumSerializedLength_Int64_Fits()
	{
		var serializer = Utf8FormattableSerializer<long>.Int64Serializer;

		Assert.AreEqual(long.MinValue.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetString(serializer.SerializeToSpan(long.MinValue)));
	}

	[TestMethod]
	public void TryGetMaximumSerializedLength_UInt64_Fits()
	{
		var serializer = Utf8FormattableSerializer<ulong>.UInt64Serializer;

		Assert.AreEqual(ulong.MaxValue.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetString(serializer.SerializeToSpan(ulong.MaxValue)));
	}

	[TestMethod]
	public void TryGetMaximumSerializedLength_Double_Fits()
	{
		var serializer = new Utf8FormattableSerializer<double>("R");

		Assert.IsTrue(serializer.Deserialize(serializer.SerializeToSpan(-double.Epsilon), out var epsilon));
		Assert.AreEqual(-double.Epsilon, epsilon);
		Assert.IsTrue(serializer.Deserialize(serializer.SerializeToSpan(double.MinValue), out var minValue));
		Assert.AreEqual(double.MinValue, minValue);
	}

	[TestMethod]
	public void Deserialize_Span_ParsesText()
	{
		Assert.IsTrue(Serializer.Deserialize("-42"u8, out var value));
		Assert.AreEqual(-42, value);
	}

	[TestMethod]
	public void Deserialize_SegmentedSequence_ParsesText()
	{
		Assert.AreEqual(12345, Serializer.DeserializeSegmented("12345"u8.ToArray()));
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("abc")]
	[DataRow("99999999999")]
	public void Deserialize_Invalid_ReturnsFalse(string text)
	{
		Assert.IsFalse(Serializer.Deserialize(Encoding.UTF8.GetBytes(text), out _));
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
