using PeterJuhasz.Repositories.Serialization;
using System.Globalization;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Serialization;

[TestClass]
public class DateTimeTicksSerializerTests(TestContext testContext)
{
	private static readonly ISerializer<DateTime> DateTimeSerializer = DateTimeTicksSerializer.Instance;
	private static readonly ISerializer<DateTimeOffset> DateTimeOffsetSerializer = DateTimeTicksSerializer.Instance;

	private static readonly DateTime Value = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
	private static readonly string ValueTicks = Value.Ticks.ToString(CultureInfo.InvariantCulture);

	private CancellationToken CT => testContext.CancellationToken;

	[TestMethod]
	public void MediaType_IsTextPlain()
	{
		Assert.AreEqual("text/plain", DateTimeSerializer.MediaType);
		Assert.AreEqual("text/plain", DateTimeOffsetSerializer.MediaType);
	}

	// DateTime

	[TestMethod]
	public void Serialize_DateTime_BufferWriter_WritesTicks()
	{
		Assert.AreEqual(ValueTicks, Encoding.ASCII.GetString(DateTimeSerializer.SerializeToBufferWriter(Value)));
	}

	[TestMethod]
	public void Serialize_DateTime_Span_WritesTicks()
	{
		Assert.AreEqual(ValueTicks, Encoding.ASCII.GetString(DateTimeSerializer.SerializeToSpan(Value)));
	}

	[TestMethod]
	public void Serialize_DateTime_Span_TooSmall_ReturnsFalse()
	{
		Assert.IsFalse(DateTimeSerializer.Serialize(Value, new byte[ValueTicks.Length - 1], out _));
	}

	[TestMethod]
	public void Serialize_DateTime_MaxValue_Fits()
	{
		Assert.AreEqual(DateTime.MaxValue.Ticks.ToString(CultureInfo.InvariantCulture), Encoding.ASCII.GetString(DateTimeSerializer.SerializeToSpan(DateTime.MaxValue)));
	}

	[TestMethod]
	public void Serialize_WithFormat_UsesFormat()
	{
		ISerializer<DateTime> serializer = new DateTimeTicksSerializer("N0");

		var expected = DateTime.MaxValue.Ticks.ToString("N0", CultureInfo.InvariantCulture);
		Assert.AreEqual(expected, Encoding.ASCII.GetString(serializer.SerializeToSpan(DateTime.MaxValue)));
		Assert.AreEqual(expected, Encoding.ASCII.GetString(serializer.SerializeToBufferWriter(DateTime.MaxValue)));
	}

	[TestMethod]
	public void Serialize_IsCultureInvariant()
	{
		ISerializer<DateTime> serializer = new DateTimeTicksSerializer("N0");

		SerializerTestHelpers.WithCulture("hu-HU", () =>
		{
			Assert.AreEqual(Value.Ticks.ToString("N0", CultureInfo.InvariantCulture), Encoding.ASCII.GetString(serializer.SerializeToSpan(Value)));
		});
	}

	[TestMethod]
	public void Deserialize_DateTime_ReturnsUtc()
	{
		Assert.IsTrue(DateTimeSerializer.Deserialize(Encoding.ASCII.GetBytes(ValueTicks), out var result));

		Assert.AreEqual(Value, result);
		Assert.AreEqual(DateTimeKind.Utc, result.Kind);
	}

	[TestMethod]
	public void Deserialize_DateTime_SegmentedSequence_ReturnsUtc()
	{
		var result = DateTimeSerializer.DeserializeSegmented(Encoding.ASCII.GetBytes(ValueTicks));

		Assert.AreEqual(Value, result);
		Assert.AreEqual(DateTimeKind.Utc, result.Kind);
	}

	[TestMethod]
	[DataRow("")]
	[DataRow("abc")]
	public void Deserialize_DateTime_Invalid_ReturnsFalse(string text)
	{
		Assert.IsFalse(DateTimeSerializer.Deserialize(Encoding.ASCII.GetBytes(text), out _));
	}

	[TestMethod]
	public void Deserialize_DateTime_OutOfRange_ReturnsFalse()
	{
		var text = (DateTime.MaxValue.Ticks + 1).ToString(CultureInfo.InvariantCulture);

		Assert.IsFalse(DateTimeSerializer.Deserialize(Encoding.ASCII.GetBytes(text), out _));
	}

	// DateTimeOffset

	[TestMethod]
	public void Serialize_DateTimeOffset_WritesUtcTicks()
	{
		var value = new DateTimeOffset(Value).ToOffset(TimeSpan.FromHours(2));

		Assert.AreEqual(ValueTicks, Encoding.ASCII.GetString(DateTimeOffsetSerializer.SerializeToSpan(value)));
		Assert.AreEqual(ValueTicks, Encoding.ASCII.GetString(DateTimeOffsetSerializer.SerializeToBufferWriter(value)));
	}

	[TestMethod]
	public void Serialize_DateTimeOffset_MaxValue_Fits()
	{
		Assert.AreEqual(DateTimeOffset.MaxValue.UtcTicks.ToString(CultureInfo.InvariantCulture), Encoding.ASCII.GetString(DateTimeOffsetSerializer.SerializeToSpan(DateTimeOffset.MaxValue)));
	}

	[TestMethod]
	public void Deserialize_DateTimeOffset_ReturnsZeroOffset()
	{
		Assert.IsTrue(DateTimeOffsetSerializer.Deserialize(Encoding.ASCII.GetBytes(ValueTicks), out var result));

		Assert.AreEqual(new DateTimeOffset(Value), result);
		Assert.AreEqual(TimeSpan.Zero, result.Offset);
	}

	[TestMethod]
	public void Deserialize_DateTimeOffset_Invalid_ReturnsFalse()
	{
		Assert.IsFalse(DateTimeOffsetSerializer.Deserialize("abc"u8, out _));
	}

	// Async

	[TestMethod]
	public async Task SerializeAsync_NotSupported()
	{
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => DateTimeSerializer.SerializeAsync(Value, Stream.Null, CT));
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => DateTimeOffsetSerializer.SerializeAsync(new DateTimeOffset(Value), Stream.Null, CT));
	}

	[TestMethod]
	public async Task DeserializeAsync_NotSupported()
	{
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => DateTimeSerializer.DeserializeAsync(Stream.Null, CT));
		await Assert.ThrowsExactlyAsync<NotSupportedException>(() => DateTimeOffsetSerializer.DeserializeAsync(Stream.Null, CT));
	}
}
