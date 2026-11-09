using System.Text.Json;

namespace PeterJuhasz.Repositories.Abstractions;

public static class DefaultJsonSerializerOptions
{
	static DefaultJsonSerializerOptions()
	{
		Default = new JsonSerializerOptions(JsonSerializerDefaults.Web);
	}

	public static readonly JsonSerializerOptions Default;
}
