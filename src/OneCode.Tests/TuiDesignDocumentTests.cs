using YamlDotNet.RepresentationModel;

namespace OneCode.Tests;

public sealed class TuiDesignDocumentTests
{
    [Fact]
    public void DesignDocument_ColorTokensMatchRuntimePalette()
    {
        var document = File.ReadAllText(FindDesignDocument());
        var frontMatter = ExtractFrontMatter(document);
        var yaml = new YamlStream();
        yaml.Load(new StringReader(frontMatter));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var colors = (YamlMappingNode)root[new YamlScalarNode("colors")];
        var parsed = colors.Children.ToDictionary(
            pair => ((YamlScalarNode)pair.Key).Value!,
            pair => ((YamlScalarNode)pair.Value).Value!);

        parsed.Should().BeEquivalentTo(
            OneCode.App.Tui.TuiPalette.DesignTokens,
            options => options.WithStrictOrdering());
    }

    private static string FindDesignDocument()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "OneCode.App", "Tui", "DESIGN.md");
            if (File.Exists(candidate))
                return candidate;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Unable to locate the TUI DESIGN.md file.");
    }

    private static string ExtractFrontMatter(string document)
    {
        const string delimiter = "---";
        var lines = document.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != delimiter)
            throw new InvalidDataException("DESIGN.md must start with YAML front matter.");

        var end = Array.FindIndex(lines, 1, line => line.Trim() == delimiter);
        if (end < 0)
            throw new InvalidDataException("DESIGN.md YAML front matter is not closed.");

        return string.Join('\n', lines, 1, end - 1);
    }
}
