using PeterJuhasz.Repositories.Abstractions;
using PeterJuhasz.Repositories.Serialization.Yaml;
using PeterJuhasz.Text.Yaml.Serialization;

namespace PeterJuhasz.Repositories.Blobs;

public static class YamlExtensions
{
	extension(IBlob blob)
	{
		public IObjectRepository<T> AsYamlObjectRepository<T>(YamlSerializerOptions? yamlSerializerOptions = null, IEqualityComparer<T>? comparer = null) where T : class =>
			blob.AsObjectRepository<T>(new YamlSerializerOptionsYamlSerializer<T>(yamlSerializerOptions ?? YamlSerializerOptions.Default), comparer);

		public ICollectionRepository<T> AsYamlCollectionRepository<T>(YamlSerializerOptions? yamlSerializerOptions = null, IEqualityComparer<T>? comparer = null) =>
			blob.AsCollectionRepository<T>(new YamlSerializerOptionsYamlCollectionSerializer<T>(yamlSerializerOptions ?? YamlSerializerOptions.Default), comparer);

		public ISetRepository<T> AsYamlSetRepository<T>(YamlSerializerOptions? yamlSerializerOptions = null, IEqualityComparer<T>? comparer = null) =>
			blob.AsSetRepository<T>(new YamlSerializerOptionsYamlCollectionSerializer<T>(yamlSerializerOptions ?? YamlSerializerOptions.Default), comparer);
	}
}
