using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeterJuhasz.Repositories.Tests.Serialization;

internal sealed record class JsonTestItem(string Name, int Count);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(JsonTestItem))]
[JsonSerializable(typeof(IReadOnlyCollection<JsonTestItem>))]
internal sealed partial class JsonTestContext : JsonSerializerContext;
