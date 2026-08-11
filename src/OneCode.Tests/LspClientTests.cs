using System.Text.Json;
using OneCode.App.Services.Lsp;
using OneCode.Core.Lsp;

namespace OneCode.Tests;

/// <summary>
/// Regression tests for LSP request-id matching and project-marker detection.
/// The string-id key bug caused every LSP response to be dropped (GetRawText includes
/// quotes) so initialize always timed out after 120s.
/// </summary>
public sealed class LspClientTests
{
    [Fact]
    public void ToPendingRequestKey_StringId_StripsJsonQuotes()
    {
        using var doc = JsonDocument.Parse("""{"id":"deadbeefcafebabe"}""");
        var key = LspClient.ToPendingRequestKey(doc.RootElement.GetProperty("id"));

        // Must match the unquoted Guid string stored when sending the request.
        key.Should().Be("deadbeefcafebabe");
        key.Should().NotContain("\"");
    }

    [Fact]
    public void ToPendingRequestKey_NumericId_UsesRawDigits()
    {
        using var doc = JsonDocument.Parse("""{"id":42}""");
        var key = LspClient.ToPendingRequestKey(doc.RootElement.GetProperty("id"));

        key.Should().Be("42");
    }

    [Fact]
    public void ToPendingRequestKey_GetRawTextWouldNotMatch_StoredStringKey()
    {
        // Documents the bug: GetRawText on a string id returns quoted JSON which
        // never equals the unquoted key we store at send time.
        const string storedKey = "abc123";
        using var doc = JsonDocument.Parse($$"""{"id":"{{storedKey}}"}""");
        var id = doc.RootElement.GetProperty("id");

        id.GetRawText().Should().Be($"\"{storedKey}\"");
        LspClient.ToPendingRequestKey(id).Should().Be(storedKey);
    }
}

public sealed class LspProjectMatcherTests
{
    [Fact]
    public void Matches_CsprojPresent_ReturnsTrue()
    {
        var dir = Directory.CreateTempSubdirectory("lsp-match-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Foo.csproj"), "<Project/>");
            var pack = new LanguagePack
            {
                Id = "csharp",
                DisplayName = "C#",
                Extensions = [".cs"],
                ProjectFiles = ["*.csproj", "*.sln"],
                Server = new LanguageServerSpec { Command = "csharp-ls" },
            };

            LspProjectMatcher.Matches(pack, dir.FullName).Should().BeTrue();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Matches_NoMarkers_ReturnsFalse()
    {
        var dir = Directory.CreateTempSubdirectory("lsp-nomatch-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "readme.txt"), "x");
            var pack = new LanguagePack
            {
                Id = "csharp",
                DisplayName = "C#",
                Extensions = [".cs"],
                ProjectFiles = ["*.csproj", "*.sln"],
                Server = new LanguageServerSpec { Command = "csharp-ls" },
            };

            LspProjectMatcher.Matches(pack, dir.FullName).Should().BeFalse();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Matches_BinDebugStyleDir_ReturnsFalse()
    {
        // Reproduces the failure mode from logs: launching from bin/Debug/net10.0
        // has no top-level .csproj, so auto-start and /lsp enable must refuse.
        var dir = Directory.CreateTempSubdirectory("lsp-bin-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "OneCode.Cli.dll"), "x");
            File.WriteAllText(Path.Combine(dir.FullName, "OneCode.Cli.pdb"), "x");
            var pack = BuiltInLanguagePacks.All.First(p => p.Id == "csharp");

            LspProjectMatcher.Matches(pack, dir.FullName).Should().BeFalse();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
