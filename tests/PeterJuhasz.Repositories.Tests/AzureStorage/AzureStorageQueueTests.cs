using Azure.Storage.Queues;
using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.AzureStorage;
using PeterJuhasz.Repositories.Serialization;
using PeterJuhasz.Repositories.Serialization.Json;
using PeterJuhasz.Repositories.Tests.Serialization;
using System.Text.Json;

namespace PeterJuhasz.Repositories.Tests.AzureStorage;

[TestClass]
[TestCategory("Azure")]
public sealed class AzureStorageQueueTests(TestContext testContext)
{
	private QueueClient _client = null!;

	private CancellationToken CT => testContext.CancellationToken;

	[TestInitialize]
	public void Initialize()
	{
		if (TestConfiguration.AzureStorageConnectionString is not { } connectionString)
		{
			Assert.Inconclusive("Azure Storage is not configured.");
			return;
		}

		// the queue is created on the first send
		_client = new QueueServiceClient(connectionString).GetQueueClient($"test-{Guid.NewGuid():N}");
	}

	[TestCleanup]
	public async Task CleanupAsync()
	{
		if (_client != null)
		{
			await _client.DeleteIfExistsAsync(CT);
		}
	}

	private IStorageQueue<JsonTestItem> CreateQueue(ISerializer<JsonTestItem>? serializer = null) =>
		_client.AsQueue(serializer ?? new JsonSerializerOptionsJsonSerializer<JsonTestItem>(JsonSerializerOptions.Web));

	[TestMethod]
	public async Task ReceiveAsync_NotExistingQueue_ReturnsEmpty()
	{
		var queue = CreateQueue();

		Assert.IsEmpty(await queue.ReceiveAsync(batchCount: 10, visibilityTimeout: null, CT).ToListAsync(CT));
	}

	[TestMethod]
	public async Task SendMessageAsync_ReceiveAsync_RoundTrips()
	{
		var queue = CreateQueue();
		var item = new JsonTestItem(Name: "a", Count: 1);

		await queue.SendMessageAsync(item, new(), CT);

		var received = await queue.ReceiveAsync(batchCount: 1, visibilityTimeout: null, CT).ToListAsync(CT);
		Assert.AreSequenceEqual([item], received);
	}

	[TestMethod]
	public async Task SendMessageAsync_UsesSerializer()
	{
		var queue = CreateQueue(new JsonTypeInfoJsonSerializer<JsonTestItem>(JsonTestContext.Default));

		await queue.SendMessageAsync(new(Name: "a", Count: 1), new(), CT);

		var message = await _client.PeekMessageAsync(CT);
		Assert.AreEqual("""{"name":"a","count":1}""", message.Value.Body.ToString());
	}

	[TestMethod]
	public async Task SendMessagesAsync_ReceiveAsync_RoundTripsAll()
	{
		var queue = CreateQueue();
		JsonTestItem[] items = [new(Name: "a", Count: 1), new(Name: "b", Count: 2)];

		await queue.SendMessagesAsync(items, new(), CT);

		Assert.AreEqual(2L, await queue.GetCountAsync(CT));
		var received = await queue.ReceiveAsync(batchCount: 10, visibilityTimeout: null, CT).ToListAsync(CT);
		// messages are sent in parallel, so their order is not guaranteed
		Assert.AreSequenceEqual(items, received.OrderBy(static i => i.Name).ToArray());
		Assert.AreEqual(0L, await queue.GetCountAsync(CT));
	}
}
