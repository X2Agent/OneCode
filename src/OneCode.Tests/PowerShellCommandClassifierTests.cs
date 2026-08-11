using OneCode.Core.Permissions;

namespace OneCode.Tests;

public sealed class PowerShellCommandClassifierTests
{
    [Theory]
    [InlineData("Get-Content")]
    [InlineData("Write-Output")]
    [InlineData("Get-ChildItem")]
    [InlineData("Get-Process")]
    [InlineData("Get-Item")]
    [InlineData("Test-Path")]
    [InlineData("Get-Location")]
    public void IsReadOnly_ReadOnlyCommands_ReturnsTrue(string command)
    {
        PowerShellCommandClassifier.IsReadOnly(command).Should().BeTrue();
    }

    [Theory]
    [InlineData("Get-Content")]
    [InlineData("Write-Output")]
    [InlineData("Get-ChildItem")]
    [InlineData("Get-Process")]
    public void IsDestructive_ReadOnlyCommands_ReturnsFalse(string command)
    {
        PowerShellCommandClassifier.IsDestructive(command).Should().BeFalse();
    }

    [Theory]
    [InlineData("Remove-Item")]
    [InlineData("Stop-Process")]
    [InlineData("Invoke-Expression")]
    [InlineData("Start-Process")]
    [InlineData("Format-Volume")]
    [InlineData("Clear-Disk")]
    [InlineData("Set-Content")]
    public void IsDestructive_DangerousCommands_ReturnsTrue(string command)
    {
        PowerShellCommandClassifier.IsDestructive(command).Should().BeTrue();
    }

    [Theory]
    [InlineData("Remove-Item")]
    [InlineData("Stop-Process")]
    [InlineData("Invoke-Expression")]
    [InlineData("Start-Process")]
    [InlineData("Format-Volume")]
    public void IsReadOnly_DangerousCommands_ReturnsFalse(string command)
    {
        PowerShellCommandClassifier.IsReadOnly(command).Should().BeFalse();
    }

    [Fact]
    public void IsDestructive_PipelineWithDangerousCommand_ReturnsTrue()
    {
        PowerShellCommandClassifier.IsDestructive("Get-Process | Stop-Process").Should().BeTrue();
    }

    [Fact]
    public void IsReadOnly_PipelineWithDangerousCommand_ReturnsFalse()
    {
        PowerShellCommandClassifier.IsReadOnly("Get-Process | Stop-Process").Should().BeFalse();
    }

    [Fact]
    public void IsDestructive_EncodedCommandPlaceholder_ReturnsTrue()
    {
        // The trailing '>' in "<base64>" is parsed as a PowerShell write-redirection
        // token by the tokenizer, so the classifier flags this input as destructive.
        PowerShellCommandClassifier.IsDestructive("-EncodedCommand <base64>").Should().BeTrue();
    }

    [Fact]
    public void GetDestructiveCommandWarning_EncodedCommandPlaceholder_ReturnsWarning()
    {
        PowerShellCommandClassifier.GetDestructiveCommandWarning("-EncodedCommand <base64>")
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData("del")]
    [InlineData("rd")]
    [InlineData("rm")]
    [InlineData("rmdir")]
    [InlineData("ri")]
    [InlineData("iex")]
    public void IsDestructive_AliasesOfDangerousCommands_ReturnsTrue(string command)
    {
        PowerShellCommandClassifier.IsDestructive(command).Should().BeTrue();
    }

    [Theory]
    [InlineData("del", "remove-item")]
    [InlineData("rd", "remove-item")]
    [InlineData("iex", "invoke-expression")]
    [InlineData("cat", "get-content")]
    [InlineData("ls", "get-childitem")]
    public void GetPrimaryCommandName_Alias_NormalizesToCanonical(string alias, string canonical)
    {
        PowerShellCommandClassifier.GetPrimaryCommandName(alias).Should().Be(canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsDestructive_EmptyOrWhitespaceCommand_ReturnsFalse(string command)
    {
        PowerShellCommandClassifier.IsDestructive(command).Should().BeFalse();
    }

    [Fact]
    public void IsDestructive_Comment_ReturnsFalse()
    {
        PowerShellCommandClassifier.IsDestructive("# comment").Should().BeFalse();
    }

    [Fact]
    public void IsReadOnly_EmptyCommand_ReturnsFalse()
    {
        PowerShellCommandClassifier.IsReadOnly("").Should().BeFalse();
    }

    [Fact]
    public void IsReadOnly_Comment_ReturnsFalse()
    {
        PowerShellCommandClassifier.IsReadOnly("# comment").Should().BeFalse();
    }

    [Fact]
    public void GetPrimaryCommandName_EmptyCommand_ReturnsNull()
    {
        PowerShellCommandClassifier.GetPrimaryCommandName("").Should().BeNull();
    }

    [Fact]
    public void ExtractReferencedPaths_GetContentPositional_ExtractsPath()
    {
        var paths = PowerShellCommandClassifier.ExtractReferencedPaths("Get-Content file.txt");
        paths.Should().Contain("file.txt");
    }

    [Fact]
    public void ExtractReferencedPaths_EmptyCommand_ReturnsEmpty()
    {
        var paths = PowerShellCommandClassifier.ExtractReferencedPaths("");
        paths.Should().BeEmpty();
    }

    [Fact]
    public void IsDestructive_Redirection_ReturnsTrue()
    {
        PowerShellCommandClassifier.IsDestructive("Get-Content file.txt > out.txt").Should().BeTrue();
    }

    [Fact]
    public void IsReadOnly_Redirection_ReturnsFalse()
    {
        PowerShellCommandClassifier.IsReadOnly("Get-Content file.txt > out.txt").Should().BeFalse();
    }
}
