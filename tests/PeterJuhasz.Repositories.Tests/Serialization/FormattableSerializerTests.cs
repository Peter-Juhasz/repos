using PeterJuhasz.Repositories.Serialization;
using System.Globalization;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Serialization;

[TestClass]
public class FormattableSerializerTests(TestContext testContext)
{
	private static readonly ISerializer<int> Serializer = FormattableSerializer<int>.Int32Serializer;

	private CancellationToken CT => testContext.CancellationToken;

	[TestMethod]
	public void MediaType_IsTextPlain()
	{
		Assert.AreEqual("text/plain", Serializer.MediaType);
	}

	[TestMethod]
	public void Serialize_BufferWriter_WritesText()
	{
		Assert.AreEqual("-42", Encoding.ASCII.GetString(Serializer.SerializeToBufferWriter(-42)));
	}

	[TestMethod]
	public void Serialize_Span_WritesText()
	{
		Assert.AreEqual("-42", Encoding.ASCII.GetString(Serializer.SerializeToSpan(-42)));
	}

	[TestMethod]
	public void Serialize_Span_TooSmall_ReturnsFalse()
	{
		Assert.IsFalse(Serializer.Serialize(12345, new byte[4], out _));
	}

	[TestMethod]
	public void Serialize_MultiByteEncoding_UsesEncoding()
	{
		var serializer = new FormattableSerializer<long>(Encoding.UTF32);

		Assert.AreSequenceEqual(Encoding.UTF32.GetBytes("-42"), serializer.SerializeToSpan(-42));
		Assert.AreSequenceEqual(Encoding.UTF32.GetBytes("-42"), serializer.SerializeToBufferWriter(-42));
	}

	[TestMethod]
	public void Serialize_MultiByteEncoding_MaximumLengthFits()
	{
		var serializer = new FormattableSerializer<long>(Encoding.UTF32);

		var bytes = serializer.SerializeToBufferWriter(long.MinValue);

		Assert.AreSequenceEqual(Encoding.UTF32.GetBytes(long.MinValue.ToString(CultureInfo.InvariantCulture)), bytes);
		Assert.IsTrue(serializer.Deserialize(bytes, out var value));
		Assert.AreEqual(long.MinValue, value);
	}

	[TestMethod]
	public void Serialize_IsCultureInvariant()
	{
		var serializer = new FormattableSerializer<double>(Encoding.ASCII);

		SerializerTestHelpers.WithCulture("hu-HU", () =>
		{
			Assert.AreEqual("1.5", Encoding.ASCII.GetString(serializer.SerializeToSpan(1.5)));
			Assert.IsTrue(serializer.Deserialize("1.5"u8, out var value));
			Assert.AreEqual(1.5, value);
		});
	}

	[TestMethod]
	[DataRow(long.MinValue)]
	[DataRow(long.MaxValue)]
	public void Int64Serializer_ExtremeValues_RoundTrip(long value)
	{
		var serializer = FormattableSerializer<long>.Int64Serializer;

		Assert.IsTrue(serializer.Deserialize(serializer.SerializeToSpan(value), out var result));
		Assert.AreEqual(value, result);
	}

	[TestMethod]
	public void DateTimeOffsetSerializer_RoundTrips()
	{
		var serializer = FormattableSerializer<DateTimeOffset>.DateTimeOffsetSerializer;
		var value = new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.FromHours(14)).AddTicks(9_999_999);

		var bytes = serializer.SerializeToSpan(value);

		Assert.AreEqual("9999-12-31T23:59:59.9999999+14:00", Encoding.ASCII.GetString(bytes));
		Assert.IsTrue(serializer.Deserialize(bytes, out var result));
		Assert.AreEqual(value, result);
		Assert.AreEqual(value.Offset, result.Offset);
	}

	[TestMethod]
	public void DateTimeSerializer_UtcValue_RoundTrips()
	{
		var serializer = FormattableSerializer<DateTime>.DateTimeSerializer;
		var value = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

		var bytes = serializer.SerializeToSpan(value);

		Assert.AreEqual("2026-01-02T03:04:05.0000000Z", Encoding.ASCII.GetString(bytes));
		Assert.IsTrue(serializer.Deserialize(bytes, out var result));
		Assert.AreEqual(value, result);
		Assert.AreEqual(DateTimeKind.Utc, result.Kind);
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
		Assert.IsFalse(Serializer.Deserialize(Encoding.ASCII.GetBytes(text), out _));
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
