using PeterJuhasz.Repositories.Serialization;
using PeterJuhasz.Repositories.Serialization.Separated;
using PeterJuhasz.Text.Separated;
using System.Text;

namespace PeterJuhasz.Repositories.Tests.Serialization;

[TestClass]
public class SeparatedValuesSerializerTests(TestContext testContext)
{
	public sealed record class Row(int Id, string Name);

	private static readonly Row[] Rows = [new(1, "a"), new(2, "b")];

	private static readonly ICollectionSerializer<Row> Serializer = new SeparatedValuesSerializer<Row>(SeparatedValuesReaderOptions.Csv, SeparatedValuesWriterOptions.Csv);

	private CancellationToken CT => testContext.CancellationToken;

	private async Task<string> SerializeAsync(ICollectionSerializer<Row> serializer, IReadOnlyCollection<Row> rows)
	{
		using var stream = new MemoryStream();
		await serializer.SerializeAsync(rows, stream, CT);
		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private static MemoryStream ToStream(string text) => new(Encoding.UTF8.GetBytes(text));

	[TestMethod]
	public void MediaType_IsCsv()
	{
		Assert.AreEqual("text/csv", Serializer.MediaType);
	}

	[TestMethod]
	public async Task SerializeAsync_WritesHeaderAndOneLinePerRow()
	{
		var lines = (await SerializeAsync(Serializer, Rows)).Split('\n');

		Assert.HasCount(4, lines);
		Assert.AreEqual("Id,Name", lines[0]);
		Assert.EndsWith(",a", lines[1]);
		Assert.EndsWith(",b", lines[2]);
		Assert.AreEqual("", lines[3]);
	}

	[TestMethod]
	public async Task SerializeAsync_Empty_WritesHeaderOnly()
	{
		Assert.AreEqual("Id,Name\n", await SerializeAsync(Serializer, []));
	}

	[TestMethod]
	public async Task SerializeAsync_QuotesValuesContainingDelimiter()
	{
		Assert.EndsWith(",\"c,d\"\n", await SerializeAsync(Serializer, [new(1, "c,d")]));
	}

	[TestMethod]
	public async Task SerializeAsync_Tsv_UsesTabDelimiter()
	{
		ICollectionSerializer<Row> serializer = new SeparatedValuesSerializer<Row>(SeparatedValuesReaderOptions.Tsv, SeparatedValuesWriterOptions.Tsv);

		var text = await SerializeAsync(serializer, Rows);

		Assert.StartsWith("Id\tName\n", text);
		using var stream = ToStream(text);
		Assert.AreSequenceEqual(Rows, (await serializer.DeserializeAsync(stream, CT)).ToArray());
	}

	[TestMethod]
	public async Task SerializeAsync_LeavesStreamOpen()
	{
		using var stream = new MemoryStream();

		await Serializer.SerializeAsync(Rows, stream, CT);

		Assert.IsTrue(stream.CanWrite);
	}

	[TestMethod]
	public async Task DeserializeAsyncEnumerable_ReadsRows()
	{
		using var stream = ToStream("Id,Name\n1,a\n2,b\n");

		var result = await Serializer.DeserializeAsyncEnumerable(stream, CT).ToListAsync(CT);

		Assert.AreSequenceEqual(Rows, result);
	}

	[TestMethod]
	public async Task DeserializeAsync_ReadsRows()
	{
		using var stream = ToStream("Id,Name\n1,a\n2,b\n");

		var result = await Serializer.DeserializeAsync(stream, CT);

		Assert.AreSequenceEqual(Rows, result.ToArray());
	}

	[TestMethod]
	public async Task SerializeAsync_ThenDeserializeAsync_RoundTrips()
	{
		Row[] rows = [new(1, "c,d"), new(2, "quote \" here"), new(3, "")];
		using var stream = new MemoryStream();
		await Serializer.SerializeAsync(rows, stream, CT);
		stream.Position = 0;

		var result = await Serializer.DeserializeAsync(stream, CT);

		Assert.AreSequenceEqual(rows, result.ToArray());
	}
}
