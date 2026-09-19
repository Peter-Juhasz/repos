namespace PeterJuhasz.Repositories.Abstractions;

public interface IDistributedLock
{
	ValueTask<string?> TryEnterAsync(TimeSpan duration, CancellationToken cancellationToken);

	Task<string> WaitForEnterAsync(TimeSpan duration, CancellationToken cancellationToken);

	Task<bool> ReleaseAsync(string concurrencyToken);
}