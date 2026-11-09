using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Serialization.Separated;
using PeterJuhasz.Text.Separated;

namespace PeterJuhasz.Repositories.Blobs;

public static class SeparatedValuesExtensions
{
	extension(IBlob blob)
	{
		public ICollectionRepository<T> AsCsvCollectionRepository<T>(IEqualityComparer<T>? comparer = null) =>
			blob.AsStreamingCollectionRepository<T>(new SeparatedValuesSerializer<T>(SeparatedValuesReaderOptions.Csv, SeparatedValuesWriterOptions.Csv), comparer);

		public ICollectionRepository<T> AsTsvCollectionRepository<T>(IEqualityComparer<T>? comparer = null) =>
			blob.AsStreamingCollectionRepository<T>(new SeparatedValuesSerializer<T>(SeparatedValuesReaderOptions.Tsv, SeparatedValuesWriterOptions.Tsv), comparer);

		public ISetRepository<T> AsCsvSetRepository<T>(IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new SeparatedValuesSerializer<T>(SeparatedValuesReaderOptions.Csv, SeparatedValuesWriterOptions.Csv), comparer);

		public ISetRepository<T> AsTsvSetRepository<T>(IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new SeparatedValuesSerializer<T>(SeparatedValuesReaderOptions.Tsv, SeparatedValuesWriterOptions.Tsv), comparer);
	}
}
