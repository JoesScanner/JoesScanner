namespace JoesScanner.Services;

public sealed class DatabasePathProvider : IDatabasePathProvider
{
    public string DbPath { get; } = Path.Combine(FileSystem.AppDataDirectory, "joesscanner.db");
}
