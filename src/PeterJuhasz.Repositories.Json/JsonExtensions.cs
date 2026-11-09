using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PeterJuhasz.Repositories.Blobs;

public static class JsonExtensions
{
	extension(IBlob blob)
	{
		public IObjectRepository<T> AsJsonObjectRepository<T>(JsonSerializerOptions jsonSerializerOptions, IEqualityComparer<T>? comparer = null) where T : class =>
			blob.AsObjectRepository<T>(new JsonSerializerOptionsJsonSerializer<T>(jsonSerializerOptions), comparer);

		public IObjectRepository<T> AsJsonObjectRepository<T>(JsonSerializerContext context, IEqualityComparer<T>? comparer = null) where T : class =>
			blob.AsObjectRepository<T>(new JsonTypeInfoJsonSerializer<T>(context), comparer);

		public IObjectRepository<T> AsJsonObjectRepository<T>(JsonTypeInfo<T> jsonTypeInfo, IEqualityComparer<T>? comparer = null) where T : class =>
			blob.AsObjectRepository<T>(new JsonTypeInfoJsonSerializer<T>(jsonTypeInfo), comparer);


		public ICollectionRepository<T> AsJsonCollectionRepository<T>(JsonSerializerOptions jsonSerializerOptions, IEqualityComparer<T>? comparer = null) =>
			blob.AsCollectionRepository<T>(new JsonSerializerOptionsJsonCollectionSerializer<T>(jsonSerializerOptions), comparer);

		public ICollectionRepository<T> AsJsonCollectionRepository<T>(JsonSerializerContext context, IEqualityComparer<T>? comparer = null) =>
			blob.AsCollectionRepository<T>(new JsonTypeInfoJsonCollectionSerializer<T>(context), comparer);

		public ICollectionRepository<T> AsStreamingJsonCollectionRepository<T>(JsonSerializerOptions jsonSerializerOptions, IEqualityComparer<T>? comparer = null) =>
			blob.AsStreamingCollectionRepository<T>(new JsonSerializerOptionsJsonCollectionSerializer<T>(jsonSerializerOptions), comparer);

		public ICollectionRepository<T> AsStreamingJsonCollectionRepository<T>(JsonSerializerContext context, IEqualityComparer<T>? comparer = null) =>
			blob.AsStreamingCollectionRepository<T>(new JsonTypeInfoJsonCollectionSerializer<T>(context), comparer);


		public ISetRepository<T> AsJsonSetRepository<T>(JsonSerializerOptions jsonSerializerOptions, IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new JsonSerializerOptionsJsonCollectionSerializer<T>(jsonSerializerOptions), comparer);

		public ISetRepository<T> AsJsonSetRepository<T>(JsonSerializerContext context, IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new JsonTypeInfoJsonCollectionSerializer<T>(context), comparer);
	}

	extension(IAppendBlob blob)
	{
		public ICollectionRepository<T> AsJsonLineCollectionRepository<T>(JsonSerializerOptions jsonSerializerOptions) =>
			blob.AsLineCollectionRepository<T>(new JsonSerializerOptionsJsonSerializer<T>(jsonSerializerOptions));

		public ICollectionRepository<T> AsJsonLineCollectionRepository<T>(JsonTypeInfo<T> context) =>
			blob.AsLineCollectionRepository<T>(new JsonTypeInfoJsonSerializer<T>(context));

		public ICollectionRepository<T> AsJsonLineCollectionRepository<T>(JsonSerializerContext context) =>
			blob.AsLineCollectionRepository<T>(new JsonTypeInfoJsonSerializer<T>(context.GetTypeInfo<T>()));
	}
}
