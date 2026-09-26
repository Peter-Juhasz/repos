using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

namespace PeterJuhasz.Repositories.Tests;

/// <summary>
/// Configuration for tests, read from <c>appsettings.json</c> (optional, not committed) and environment variables.
/// Environment variables take precedence, use <c>__</c> as section separator (e.g. <c>AzureStorage__ConnectionString</c>).
/// </summary>
public static class TestConfiguration
{
	public static IConfiguration Configuration { get; } = new ConfigurationBuilder()
		.SetBasePath(AppContext.BaseDirectory)
		.AddJsonFile("appsettings.json", optional: true)
		.AddEnvironmentVariables()
		.Build();

	public static string? AzureStorageConnectionString => Configuration["AzureStorage:ConnectionString"] is { Length: > 0 } value ? value : null;

	private static readonly Lazy<BlobServiceClient?> _blobServiceClient = new(() => AzureStorageConnectionString is { } connectionString ? new BlobServiceClient(connectionString) : null);

	/// <summary>
	/// Returns a client for the configured test storage account, or marks the test inconclusive if none is configured.
	/// </summary>
	public static BlobServiceClient GetBlobServiceClient()
	{
		if (_blobServiceClient.Value is { } client)
		{
			return client;
		}

		Assert.Inconclusive("Azure Storage is not configured. Set 'AzureStorage:ConnectionString' in appsettings.json or the 'AzureStorage__ConnectionString' environment variable.");
		throw new UnreachableException();
	}
}
