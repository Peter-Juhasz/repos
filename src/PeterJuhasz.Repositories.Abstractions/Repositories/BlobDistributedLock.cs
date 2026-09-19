using PeterJuhasz.Repositories.Abstractions;
using System.Collections.Frozen;

namespace PeterJuhasz.Repositories.Blobs;

public sealed class BlobDistributedLock(
	IBlob blob,
	BlobDistributedLock.Options options,
	TimeProvider timeProvider
) : IDistributedLock
{
	private const string MetadataExpiresKey = "Duration";
	private const int MaxAttempts = 5;

	public ValueTask<string?> TryEnterAsync(TimeSpan duration, CancellationToken cancellationToken) =>
		TryEnterCoreAsync(duration, attempt: 1, cancellationToken);

	private async ValueTask<string?> TryEnterCoreAsync(TimeSpan duration, int attempt, CancellationToken cancellationToken)
	{
		if (attempt > MaxAttempts)
		{
			return null;
		}

		try
		{
			var metadata = FrugalDictionary.Create<string, string>(MetadataExpiresKey, (timeProvider.GetUtcNow() + duration).ToString("O"));

			var concurrencyToken = await blob.WriteAsync(
				ReadOnlyMemory<byte>.Empty,
				concurrencyToken: null,
				new(Metadata: metadata),
				cancellationToken
			);

			return concurrencyToken;
		}
		catch (ConflictException)
		{
			// blob already exists — check if it's expired
			var info = await blob.GetInfoAsync(cancellationToken);

			if (info is null)
			{
				// blob was deleted between attempts
				return await TryEnterCoreAsync(duration, attempt + 1, cancellationToken);
			}

			// error state — no valid expiration metadata
			if (info.Metadata is null ||
				!info.Metadata.TryGetValue(MetadataExpiresKey, out var durationString) ||
				!durationString.TryParseAs<DateTimeOffset>(out var expiration))
			{
				await ReleaseAsync(info.ConcurrencyToken);
				return await TryEnterCoreAsync(duration, attempt + 1, cancellationToken);
			}

			// expired
			if (expiration < timeProvider.GetUtcNow())
			{
				if (await ReleaseAsync(info.ConcurrencyToken))
				{
					return await TryEnterCoreAsync(duration, attempt + 1, cancellationToken);
				}
			}

			return null;
		}
	}

	public async Task<string> WaitForEnterAsync(TimeSpan duration, CancellationToken cancellationToken)
	{
		while (await TryEnterAsync(duration, cancellationToken) is var concurrencyToken)
		{
			if (concurrencyToken != null)
			{
				return concurrencyToken;
			}

			await Task.Delay(options.WaitPeriod, cancellationToken);
		}

		throw new InvalidOperationException();
	}

	public async Task<bool> ReleaseAsync(string concurrencyToken)
	{
		try
		{
			await blob.DeleteAsync(concurrencyToken, CancellationToken.None);
			return true;
		}
		catch (ConflictException)
		{
		}
		catch (NotFoundException)
		{
		}

		return false;
	}

	public record class Options(TimeSpan WaitPeriod);
}

public static partial class Extensions
{
	extension(IBlob blob)
	{
		public BlobDistributedLock AsLock(BlobDistributedLock.Options options) =>
			new(blob, options, TimeProvider.System);
	}
}
