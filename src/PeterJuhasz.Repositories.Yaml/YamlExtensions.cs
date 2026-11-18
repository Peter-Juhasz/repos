using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Serialization.Yaml;
using PeterJuhasz.Text.Yaml.Serialization;
using PeterJuhasz.Text.Yaml.Serialization.Metadata;

namespace PeterJuhasz.Repositories.Blobs;

public static class YamlExtensions
{
	extension(IBlob blob)
	{
		public IObjectRepository<T> AsYamlObjectRepository<T>(YamlSerializerOptions? yamlSerializerOptions = null, IEqualityComparer<T>? comparer = null) where T : class =>
			blob.AsObjectRepository<T>(new YamlSerializerOptionsYamlSerializer<T>(yamlSerializerOptions ?? YamlSerializerOptions.Default), comparer);

		public IObjectRepository<T> AsYamlObjectRepository<T>(YamlSerializerContext context, IEqualityComparer<T>? comparer = null) where T : class =>
			blob.AsObjectRepository<T>(new YamlTypeInfoYamlSerializer<T>(context), comparer);

		public IObjectRepository<T> AsYamlObjectRepository<T>(YamlTypeInfo<T> yamlTypeInfo, IEqualityComparer<T>? comparer = null) where T : class =>
			blob.AsObjectRepository<T>(new YamlTypeInfoYamlSerializer<T>(yamlTypeInfo), comparer);


		public ICollectionRepository<T> AsYamlCollectionRepository<T>(YamlSerializerOptions? yamlSerializerOptions = null, IEqualityComparer<T>? comparer = null) =>
			blob.AsCollectionRepository<T>(new YamlSerializerOptionsYamlCollectionSerializer<T>(yamlSerializerOptions ?? YamlSerializerOptions.Default), comparer);

		public ICollectionRepository<T> AsYamlCollectionRepository<T>(YamlSerializerContext context, IEqualityComparer<T>? comparer = null) =>
			blob.AsCollectionRepository<T>(new YamlTypeInfoYamlCollectionSerializer<T>(context), comparer);

		public ICollectionRepository<T> AsYamlCollectionRepository<T>(YamlTypeInfo<IReadOnlyCollection<T>> yamlTypeInfo, IEqualityComparer<T>? comparer = null) =>
			blob.AsCollectionRepository<T>(new YamlTypeInfoYamlCollectionSerializer<T>(yamlTypeInfo), comparer);


		public ISetRepository<T> AsYamlSetRepository<T>(YamlSerializerOptions? yamlSerializerOptions = null, IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new YamlSerializerOptionsYamlCollectionSerializer<T>(yamlSerializerOptions ?? YamlSerializerOptions.Default), comparer);

		public ISetRepository<T> AsYamlSetRepository<T>(YamlSerializerContext context, IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new YamlTypeInfoYamlCollectionSerializer<T>(context), comparer);

		public ISetRepository<T> AsYamlSetRepository<T>(YamlTypeInfo<IReadOnlyCollection<T>> yamlTypeInfo, IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new YamlTypeInfoYamlCollectionSerializer<T>(yamlTypeInfo), comparer);
	}
}
