using Azure.Storage.Blobs;

namespace PeterJuhasz.Repositories.Tests;

/// <summary>
/// A uniquely named blob container in the test storage account, deleted on dispose.
/// </summary>
public sealed class DisposableContainer : IAsyncDisposable
{
	private DisposableContainer(BlobContainerClient client)
	{
		Client = client;
	}

	public BlobContainerClient Client { get; }

	public string Name => Client.Name;

	/// <param name="create">When <see langword="false"/>, only a unique name is reserved, the container itself does not exist yet.</param>
	public static async Task<DisposableContainer> CreateAsync(CancellationToken cancellationToken, bool create = true)
	{
		var service = TestConfiguration.GetBlobServiceClient();
		var client = service.GetBlobContainerClient($"test-{Guid.NewGuid():N}");
		if (create)
		{
			await client.CreateAsync(cancellationToken: cancellationToken);
		}

		return new(client);
	}

	public BlobClient GetBlobClient(string name) => Client.GetBlobClient(name);

	public async ValueTask DisposeAsync()
	{
		await Client.DeleteIfExistsAsync();
	}
}
