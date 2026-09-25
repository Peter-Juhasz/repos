namespace PeterJuhasz.Repositories.Tests;

public sealed class TemporaryDirectory : IDisposable
{
	public TemporaryDirectory()
	{
		Directory = System.IO.Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PeterJuhasz.Repositories.Tests", Guid.NewGuid().ToString("N")));
	}

	public DirectoryInfo Directory { get; }

	public string Path => Directory.FullName;

	public FileInfo GetFile(string relativePath) => new(System.IO.Path.Combine(Path, relativePath));

	public void Dispose()
	{
		try
		{
			Directory.Delete(recursive: true);
		}
		catch (DirectoryNotFoundException)
		{
		}
	}
}
