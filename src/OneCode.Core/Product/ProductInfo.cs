namespace OneCode.Core.Product;

public sealed record ProductInfo
{
    public static readonly ProductInfo Default = new();

    public string Name { get; init; } = "OneCode";
    public string CommandName { get; init; } = "onecode";
    public string ConfigDirName { get; init; } = ".onecode";
    public string SystemIdentifier { get; init; } = "onecode";
    public string Version { get; init; } = "1.0.0";
    public string UserAgent => $"{SystemIdentifier}/{Version}";

    public ProductRepo Repository { get; init; } = new();
}

public sealed record ProductRepo
{
    public string Owner { get; init; } = "X2Agent";
    public string Name { get; init; } = "OneCode";
    public string Url => $"https://github.com/{Owner}/{Name}";
    public string ReleasesUrl => $"{Url}/releases";
    public string IssuesUrl => $"{Url}/issues";
    public string LatestReleaseApiUrl =>
        $"https://api.github.com/repos/{Owner}/{Name}/releases/latest";
    public string RawContentUrl(string branch, string path) =>
        $"https://raw.githubusercontent.com/{Owner}/{Name}/{branch}/{path}";
}
