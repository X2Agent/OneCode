namespace OneCode.Infrastructure.Config;

/// <summary>
/// OneCode 配置服务。统一解析内置默认值、用户配置、项目配置、环境变量和会话覆盖，
/// 并通过显式作用域补丁执行持久化。
/// </summary>
public interface IConfigManager
{
    ConfigSnapshot Current { get; }

    string ConfigDir { get; }

    string? ProjectConfigDir { get; }

    string SettingsFilePath { get; }

    string? ProjectSettingsFilePath { get; }

    void Reload();

    Task<ConfigApplyResult> ApplyAsync(ConfigPatch patch, CancellationToken ct = default);

    T? GetSetting<T>(string key, T? defaultValue = default) =>
        Current.Effective.Get(key, defaultValue);

    void InitializeWatcher();

    event Action<ConfigSnapshot>? SettingsChanged;
}
