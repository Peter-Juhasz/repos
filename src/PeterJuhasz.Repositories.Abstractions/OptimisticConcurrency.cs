namespace PeterJuhasz.Repositories.Abstractions;

public record class OptimisticConcurrencyOptions(
	int? MaximumTryCount = null,
	Func<int, TimeSpan>? DelayBetweenRetries = null,
	TimeSpan? Timeout = default
)
{
	public static readonly OptimisticConcurrencyOptions Default = new();
}

public static class OptimisticConcurrency
{
	public static async Task Retry(Func<CancellationToken, ValueTask> factory, OptimisticConcurrencyOptions? options, CancellationToken cancellationToken)
	{
		options ??= OptimisticConcurrencyOptions.Default;

		var effective = cancellationToken;
		CancellationTokenSource? linked = null;
		if (options.Timeout != null)
		{
			linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			linked.CancelAfter(options.Timeout.Value);
			effective = linked.Token;
		}
		var retryCount = 0;

		try
		{
			while (true)
			{
				effective.ThrowIfCancellationRequested();

				if (retryCount++ > options.MaximumTryCount)
				{
					throw new OperationCanceledException("The operation could not be completed due to repeated conflicts.");
				}

				try
				{
					await factory(effective);
					return;
				}
				catch (ConflictException)
				{
					if (options.DelayBetweenRetries is { } delayFunc)
					{
						var delay = delayFunc(retryCount);
						if (delay > TimeSpan.Zero)
						{
							await Task.Delay(delay, effective);
						}
					}

					continue;
				}
			}
		}
		finally
		{
			linked?.Dispose();
		}
	}

	public static async Task Retry(Func<CancellationToken, ValueTask> factory, CancellationToken cancellationToken) =>
		await Retry(factory, OptimisticConcurrencyOptions.Default, cancellationToken);


	public static async Task<T> Retry<T>(Func<CancellationToken, ValueTask<T>> factory, OptimisticConcurrencyOptions? options, CancellationToken cancellationToken)
	{
		options ??= OptimisticConcurrencyOptions.Default;

		var effective = cancellationToken;
		CancellationTokenSource? linked = null;
		if (options.Timeout != null)
		{
			linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			linked.CancelAfter(options.Timeout.Value);
			effective = linked.Token;
		}
		var retryCount = 0;

		try
		{
			while (true)
			{
				effective.ThrowIfCancellationRequested();

				if (retryCount++ > options.MaximumTryCount)
				{
					throw new OperationCanceledException("The operation could not be completed due to repeated conflicts.");
				}

				try
				{
					return await factory(effective);
				}
				catch (ConflictException)
				{
					if (options.DelayBetweenRetries is { } delayFunc)
					{
						var delay = delayFunc(retryCount);
						if (delay > TimeSpan.Zero)
						{
							await Task.Delay(delay, effective);
						}
					}

					continue;
				}
			}
		}
		finally
		{
			linked?.Dispose();
		}
	}

	public static async Task<T> Retry<T>(Func<CancellationToken, ValueTask<T>> factory, CancellationToken cancellationToken) =>
		await Retry(factory, OptimisticConcurrencyOptions.Default, cancellationToken);
}