using OneCode.Infrastructure;
using OneCode.Infrastructure.Agent;
using OneCode.Infrastructure.Config;

namespace OneCode.App.Services.Agent;

/// <summary>
/// 自定义子代理定义目录（<c>~/.onecode/agents/*.md</c> 用户级 + <c>{cwd}/.onecode/agents/*.md</c>
/// 项目级）。项目级覆盖同名用户级——对齐 <c>SkillCatalog</c> 的用户级→项目级覆盖语义。
/// 启动扫描 + 文件 mtime 签名变更惰性重扫（不引入 <c>FileSystemWatcher</c> 常驻监听）。
/// 解析错误显式保留在 <see cref="LoadErrors"/>，由派工入口报错，绝不静默跳过。
/// </summary>
public sealed class CustomAgentCatalog(string workingDir, string? userHome = null)
{
    private readonly string _workingDir = Path.GetFullPath(workingDir);
    private readonly string _userHome = userHome ?? PathsHelper.UserHome;
    private readonly object _lock = new();
    private Dictionary<string, AgentDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _loadErrors = [];
    private string? _signature;

    /// <summary>解析失败的定义文件（路径 + 原因）。非空时派工入口显式报错；修复保存后自动恢复。</summary>
    public IReadOnlyList<string> LoadErrors
    {
        get { EnsureCurrent(); lock (_lock) return _loadErrors; }
    }

    public IReadOnlyList<AgentDefinition> GetAll()
    {
        EnsureCurrent();
        lock (_lock) return _definitions.Values.ToList();
    }

    public AgentDefinition? Find(string name)
    {
        EnsureCurrent();
        lock (_lock) return _definitions.GetValueOrDefault(name);
    }

    /// <summary>用户级在前、项目级在后（后插入者覆盖同名）。</summary>
    private IEnumerable<string> CandidateDirectories()
    {
        yield return Path.Combine(_userHome, Constants.App.ConfigDirName, "agents");
        yield return Path.Combine(_workingDir, Constants.App.ConfigDirName, "agents");
    }

    private string ComputeSignature()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var dir in CandidateDirectories())
        {
            builder.Append(dir).Append(';');
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
                builder.Append(file).Append(':').Append(File.GetLastWriteTimeUtc(file).Ticks).Append(';');
        }
        return builder.ToString();
    }

    private void EnsureCurrent()
    {
        var signature = ComputeSignature();
        lock (_lock)
        {
            if (_signature == signature)
                return;
        }

        var (definitions, errors) = Scan();
        lock (_lock)
        {
            _definitions = definitions;
            _loadErrors = errors;
            _signature = signature;
        }
    }

    private (Dictionary<string, AgentDefinition> Definitions, List<string> Errors) Scan()
    {
        var definitions = new Dictionary<string, AgentDefinition>(StringComparer.OrdinalIgnoreCase);
        List<string> errors = [];
        foreach (var dir in CandidateDirectories())
        {
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var definition = AgentDefinitionParser.Parse(content, file);
                    definitions[definition.Name] = definition;
                }
                catch (AgentDefinitionParseException ex)
                {
                    errors.Add(ex.Message);
                }
                catch (Exception ex)
                {
                    errors.Add($"Agent definition '{file}': {ex.Message}");
                }
            }
        }
        return (definitions, errors);
    }
}
