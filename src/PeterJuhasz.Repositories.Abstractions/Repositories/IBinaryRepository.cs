namespace PeterJuhasz.Repositories.Abstractions;

public interface IBinaryRepository
{
	Task CreateAsync(BinaryData value, CancellationToken cancellationToken) =>
		StoreAsync(value, concurrencyToken: null, cancellationToken);

	Task StoreAsync(BinaryData value, string? concurrencyToken, CancellationToken cancellationToken);

	Task UpdateAsync(BinaryData value, string concurrencyToken, CancellationToken cancellationToken) =>
		StoreAsync(value, concurrencyToken, cancellationToken);


	Task StoreAsync(Stream stream, string? mediaType, string? concurrencyToken, CancellationToken cancellationToken);

	Task CreateAsync(Stream value, string? mediaType, CancellationToken cancellationToken) =>
		StoreAsync(value, mediaType, concurrencyToken: null, cancellationToken);

	Task UpdateAsync(Stream value, string? mediaType, string concurrencyToken, CancellationToken cancellationToken) =>
		StoreAsync(value, mediaType, concurrencyToken, cancellationToken);


	Task<bool> ExistsAsync(CancellationToken cancellationToken);

	async Task<BinaryData?> GetAsync(CancellationToken cancellationToken)
	{
		var stream = await GetStreamAsync(cancellationToken);
		if (stream == null)
		{
			return null;
		}

		await using (stream)
		{
			return await BinaryData.FromStreamAsync(stream, cancellationToken);
		}
	}

	Task<Stream?> GetStreamAsync(CancellationToken cancellationToken);

	Task<string?> GetVersionAsync(CancellationToken cancellationToken);

	Task DeleteIfExistsAsync(CancellationToken cancellationToken);

	Task DeleteAsync(string concurrencyToken, CancellationToken cancellationToken);
}
