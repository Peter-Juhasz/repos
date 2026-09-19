namespace PeterJuhasz.Repositories.Caching;

public record class CacheOptions(
	TimeSpan? SlidingExpiration = null,
	bool MustRevalidate = false
)
{
	public static readonly CacheOptions Immutable = new();

	public static readonly CacheOptions AlwaysRevalidate = new(MustRevalidate: true);
}
