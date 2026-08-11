namespace OneCode.Infrastructure.Tools;

/// <summary>
/// 根据项目标记文件探测 build/test 命令。
/// 供 InitCommand 模板生成复用。
/// </summary>
public static class ProjectCommandDetector
{
    /// <summary>探测项目类型并返回对应的 test 命令字符串（含参数）。</summary>
    /// <returns>如 "dotnet test"；无匹配返回 null。</returns>
    public static string? DetectTestCommand(string workingDirectory)
    {
        if (HasMarker(workingDirectory, "*.csproj", "*.vbproj", "*.fsproj", "*.slnx", "*.sln"))
            return "dotnet test";
        if (HasMarker(workingDirectory, "package.json"))
            return "npm test";
        if (HasMarker(workingDirectory, "Cargo.toml"))
            return "cargo test";
        if (HasMarker(workingDirectory, "go.mod"))
            return "go test ./...";
        if (HasMarker(workingDirectory, "pyproject.toml", "pytest.ini"))
            return "pytest";
        if (HasMarker(workingDirectory, "pom.xml", "build.gradle", "build.gradle.kts"))
            return "mvn test";
        return null;
    }

    /// <summary>
    /// 检查工作目录（向上递归到根）是否存在任一标记文件。支持 glob 通配符。
    /// </summary>
    public static bool HasMarker(string workingDirectory, params string[] markers)
    {
        var dir = workingDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            foreach (var marker in markers)
            {
                if (FileExistsWithGlob(dir, marker))
                    return true;
            }
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent ?? "";
        }
        return false;
    }

    private static bool FileExistsWithGlob(string dir, string pattern)
    {
        try
        {
            return Directory.GetFiles(dir, pattern).Length > 0
                || Directory.GetDirectories(dir, pattern).Length > 0;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
