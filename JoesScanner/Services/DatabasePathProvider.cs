namespace JoesScanner.Services;

public sealed class DatabasePathProvider : IDatabasePathProvider
{
    public string DbPath { get; } = AppPaths.GetDbPath("joesscanner.db");
}
