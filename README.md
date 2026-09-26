# Repositories

Lightweight storage abstractions over blobs: objects, collections, sets, counters, queues, indexes and locks, with optimistic concurrency, serialization, compression, caching and locking.

Examples, store objects easily in blob storage:

```
/dashboards/{customerId}/{dashboardId}.json
/favorites/{customerId}/favorites.json
/audit-logs/{customerId}/{year}/{month}/{day}.jsonl
/exports/{customerId}/{exportId}.tsv
/count-of-unread-alerts/{customerId}.txt
/profile-pictures/{userId}/{pictureId}.webp
```

Example to map to a repository:

```cs
var repository = blobServiceClient.GetBlobContainerClient("dashboards")
	.GetPartition(customerId)
	.GetBlob($"{dashboardId}.json")
		.WithCompression(BrotliCompressionOptions.Maximum)
		.WithCaching(CacheOptions.AlwaysRevalidate)
	.AsJsonObjectRepository<Dashboard>(JsonSerializerOptions.Web);
```

See more [examples](#examples) below.


## Repositories

### Blob

`IBlob` is the lowest level abstraction: a named binary payload with an ETag, media type and metadata.

```cs
IBlob blob = partition.GetBlob("item.json");
```

Reads and writes carry a concurrency token for optimistic concurrency:

```cs
IBlob.BlobReadResult? result = await blob.ReadAsync(ct);
string etag = await blob.WriteAsync(data, result?.Info.ConcurrencyToken, new(MediaType: "application/json"), ct);
await blob.DeleteIfExistsAsync(ct);
```

Use `TransformAsync` to read, change and write back with automatic retries on conflict:

```cs
await blob.TransformAsync(data => Append(data), ct);
```

### Object repository
 
The `IObjectRepository<T>` stores single objects.

```cs
var repository = container.GetPartition().GetBlob("item.json")
	.AsObjectRepository<Item>(serializer);
```

Usage:

```cs
Versioned<Item> versioned = await repository.CreateAsync(newItem, ct);
bool exists = await repository.ExistsAsync(ct);
versioned = await repository.UpdateAsync(updatedItem, versioned.ETag, ct);
await repository.DeleteAsync(versioned.ETag, ct);
```

Use `ApplyAsync` to update an existing object:

```cs
await repository.ApplyAsync(item => item with { Count = item.Count + 1 }, ct);
```

### Collection repository

The `ICollectionRepository<T>` stores a list of items in a single blob.

```cs
var repository = partition.GetBlob("items.json").AsJsonCollectionRepository<Item>(JsonSerializerOptions.Web);
```

Usage:

```cs
await repository.AddAsync(item, ct);
IReadOnlyCollection<Item> items = await repository.ListAsync(ct);
await repository.UpdateAsync(i => i.Id == id, i => i with { Done = true }, ct);
await repository.DeleteAsync(i => i.Id == id, ct);
```

All mutations go through `ApplyAsync`, which retries on concurrency conflicts:

```cs
await repository.ApplyAsync(items => items.Safe().Add(item), ct);
```

Use `AsStreamingCollectionRepository` instead to serialize directly to and from the network stream, without buffering the whole collection:

```cs
var repository = partition.GetBlob("items.json").AsStreamingJsonCollectionRepository<Item>(JsonSerializerOptions.Web);

await foreach (var item in repository.AsAsyncEnumerableAsync(ct))
{
}
```

### Binary repository

The `IBinaryRepository` stores raw payloads, as `BinaryData` or as a stream.

```cs
var repository = partition.GetBlob("photo.jpg").AsBinaryRepository();
```

Usage:

```cs
await repository.CreateAsync(BinaryData.FromBytes(bytes), ct);
BinaryData? data = await repository.GetAsync(ct);
await using Stream? stream = await repository.GetStreamAsync(ct);
await repository.DeleteIfExistsAsync(ct);
```

### Append blob

An `IAppendBlob` can only be appended to. Exposed as a collection repository of newline delimited items:

```cs
var repository = partition.GetAppendBlob("log.jsonl").AsJsonLineCollectionRepository<LogEntry>(JsonSerializerOptions.Web);
```

Usage:

```cs
await repository.AddAsync(entry, ct);
await repository.AddRangeAsync([entry1, entry2], ct);

await foreach (var item in repository.AsAsyncEnumerableAsync(ct))
{
}
```

Items can only be added and the whole blob cleared, so `ApplyAsync` throws `NotSupportedException`.

### Counter

The `IDistributedCounter` stores a single number, updated with optimistic concurrency.

```cs
IDistributedCounter counter = partition.GetBlob("visits").AsCounter();
```

Usage:

```cs
int value = await counter.IncrementAsync(ct);
value = await counter.DecrementAsync(delta: 5, ct);
value = await counter.GetAsync(ct);
await counter.ResetAsync(ct);
```

### Queue

The `IStorageQueue<T>` sends and receives messages.

Usage:

```cs
await queue.SendMessageAsync(job, new(), ct);
long count = await queue.GetCountAsync(ct);

await foreach (var message in queue.ReceiveAsync(batchCount: 32, visibilityTimeout: TimeSpan.FromMinutes(5), ct))
{
}
```

Use `WithDeduplication` to drop messages already sent, tracked in a set:

```cs
var deduplicated = queue.WithDeduplication(set, job => job.Id);
```

### Set

The `ISetRepository<T>` stores a set of unique items in a single blob.

```cs
ISetRepository<string> set = partition.GetBlob("tags.json").AsSetRepository<string>(serializer);
```

Usage:

```cs
bool added = await set.AddAsync("red", ct);
bool contains = await set.ContainsAsync("red", ct);
await set.DeleteAsync("red", ct);
```

Alternatively, store each item as an empty blob named after the item, which scales to large sets and makes membership checks a single request:

```cs
ISetRepository<string> set = partition.AsStringSetRepositoryAsNames();
```

### One to one foreign key index

The `IOneToOneForeignKeyIndex` maps a foreign key to a single principal key, stored as one small blob per foreign key.

```cs
IOneToOneForeignKeyIndex index = partition.AsOneToOneForeignKeyIndex();
```

Usage:

```cs
await index.AddAsync(email, userId, ct);
string? userId = await index.GetOrDefaultAsync(email, ct);
await index.DeleteIfExistsAsync(email, ct);
```

`AddAsync` throws `ConflictException` if the foreign key is already taken, which makes it usable for reserving unique values.

### One to many foreign key index

The `IOneToManyForeignKeyIndex` maps a principal key to many foreign keys, stored as one collection blob per principal key.

```cs
IOneToManyForeignKeyIndex index = partition.AsOneToManyForeignKeyIndexInBlob(JsonSerializerOptionsJsonCollectionSerializer<string>.Web);
```

Usage:

```cs
await index.AddAsync(userId, orderId, ct);
bool contains = await index.ContainsAsync(userId, orderId, ct);

await foreach (var key in index.ListAsync(userId, ct))
{
}
```

Alternatively, store each relation as an empty blob in a sub partition per principal key, which avoids rewriting the whole list on every change:

```cs
IOneToManyForeignKeyIndex index = partition.AsOneToManyForeignKeyIndexInBlobNames();
```

### Lock

The `IDistributedLock` coordinates across processes by taking a lease on a blob.

```cs
IDistributedLock distributedLock = partition.GetBlob("job.lock").AsLock(new(WaitPeriod: TimeSpan.FromSeconds(1)));
```

Usage:

```cs
if (await distributedLock.TryEnterAsync(TimeSpan.FromMinutes(1), ct) is string token)
{
	try
	{
		// do work
	}
	finally
	{
		await distributedLock.ReleaseAsync(token);
	}
}
```

Use `WaitForEnterAsync` to wait for the lock instead of failing fast.


## Serialization

`ISerializer<T>` serializes a single object, `ICollectionSerializer<T>` a collection.

### JSON

Pass `JsonSerializerOptions`:

```cs
var repository = blob.AsJsonObjectRepository<Item>(JsonSerializerOptions.Web);
```

Pass `JsonTypeInfo` or a `JsonSerializerContext` for source generated, AOT friendly serialization:

```cs
var repository = blob.AsJsonObjectRepository<Item>(AppJsonSerializerContext.Default.Item);
var collection = blob.AsJsonCollectionRepository<Item>(AppJsonSerializerContext.Default);
```

### CSV, TSV

Collections can be stored as separated values:

```cs
var csv = blob.AsCsvCollectionRepository<Item>();
var tsv = blob.AsTsvCollectionRepository<Item>();
```


## Compression

`WithCompression` wraps a blob so its content is compressed on write and decompressed on read, setting `Content-Encoding` accordingly.

### Brotli

```cs
var blob = partition.GetBlob("items.json").WithBrotliCompression();
```

### GZip

```cs
var blob = partition.GetBlob("items.json").WithCompression(GZipCompressionOptions.Maximum);
```


## Caching

`WithCaching` wraps a blob with an in-memory cache. `CacheOptions.Immutable` never revalidates, `CacheOptions.AlwaysRevalidate` checks the ETag on every read.

```cs
var blob = partition.GetBlob("item.json").WithCaching(CacheOptions.AlwaysRevalidate);
```

Use a `SlidingExpiration` to revalidate only after a period of inactivity:

```cs
var blob = partition.GetBlob("item.json").WithCaching(new(SlidingExpiration: TimeSpan.FromMinutes(5)));
```

Caching can be used on the repositories layer too.


## Locking

`WithLocking` serializes access to a blob within the process, so concurrent callers do not race and retry against each other. For coordination across processes, use the [distributed lock](#lock).

```cs
var blob = partition.GetBlob("item.json").WithLocking();
```

Pass a shared `SemaphoreSlim` to serialize access across multiple blobs:

```cs
var blob = partition.GetBlob("item.json").WithLocking(semaphore);
```


## Providers

### Azure

```cs
var container = new BlobServiceClient(connectionString).GetBlobContainerClient("data");
IBlobPartition partition = container.GetPartition("users");
```

Queues are backed by `QueueClient`:

```cs
var queue = new QueueServiceClient(connectionString).GetQueueClient("jobs");
IStorageQueue<Job> queue = queue.AsJson<Job>();
```

### File system

Data can be stored in the file system, for example for local development or testing.

```cs
var fileBlob = new DirectoryInfo(@"C:\data").GetPartition("users")
	.GetBlob("item.json");
```

### In-memory

In-memory implementations are useful for testing and prototyping.

```cs
IBlob blob = new InMemoryBlob("item.json", TimeProvider.System);
IAppendBlob appendBlob = new InMemoryAppendBlob("log.jsonl", TimeProvider.System);
```


## Examples

### Store objects in partitions

Structure:

```
/dashboards/{customerId}/{dashboardId}.json
```

Code example:

```cs
var repository = blobServiceClient.GetBlobContainerClient("dashboards")
	.GetPartition(customerId)
	.GetBlob($"{dashboardId}.json")
		.WithCompression(BrotliCompressionOptions.Maximum)
	.AsJsonObjectRepository<Dashboard>(JsonSerializerOptions.Web)
		.WithCaching(CacheOptions.AlwaysRevalidate);
```

### Store a collection of objects in a single blob

Structure:

```
/favorites/{customerId}/favorites.json
/exports/{customerId}/{exportId}.tsv
```

Code example:

```cs
var repository = blobServiceClient.GetBlobContainerClient("favorites")
	.GetPartition(customerId)
	.GetBlob("favorites.json")
		.WithCompression(BrotliCompressionOptions.Maximum)
		.WithCaching(CacheOptions.AlwaysRevalidate)
	.AsJsonCollectionRepository<Favorite>(JsonSerializerOptions.Web);
```

### Store log entries in an append blob

Structure:

```
/audit-logs/{customerId}/{year}/{month}/{day}.jsonl
```

Code example:

```cs
var repository = blobServiceClient.GetBlobContainerClient("audit-logs")
	.GetPartition(customerId)
	.GetAppendBlob($"{year}/{month}/{day}.jsonl")
	.AsJsonLineCollectionRepository<LogEntry>(JsonSerializerOptions.Web);

await repository.AddAsync(entry, ct);
```

### Store a counter in a blob

Structure:

```
/count-of-unread-alerts/{customerId}.txt
```

Code example:

```cs
var repository = blobServiceClient.GetBlobContainerClient("count-of-unread-alerts")
	.GetPartition()
	.GetBlob($"{customerId}.txt")
	.AsCounter();

await repository.IncrementAsync(ct);
```

### Store profile pictures in a blob

Structure:

```
/profile-pictures/{userId}/{pictureId}.webp
```

Code example:

```cs
var repository = blobServiceClient.GetBlobContainerClient("profile-pictures")
	.GetPartition(userId)
	.GetBlob($"{pictureId}.webp")
	.AsBinaryRepository()
	.WithCaching(CacheOptions.Immutable);

await repository.CreateAsync(image, ct);
```