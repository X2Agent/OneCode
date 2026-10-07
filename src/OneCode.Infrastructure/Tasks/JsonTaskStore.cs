using OneCode.Core.Tasks;
using OneCode.Infrastructure.Persistence;

namespace OneCode.Infrastructure.Tasks;

/// <summary>
/// Atomic JSON snapshot store for agent and BuildRun tasks. The previous valid
/// snapshot is retained as a backup so corrupt primary data never becomes an
/// apparently valid empty task list.
/// </summary>
public sealed class JsonTaskStore : ITaskStore
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public JsonTaskStore(string? basePath = null)
    {
        var root = basePath ?? Path.Combine(PathsHelper.GetUserConfigDir(), "tasks");
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "tasks.json");
    }

    public IReadOnlyList<TaskItem> Load()
    {
        lock (_gate)
        {
            var primary = TryReadSnapshot(_path);
            if (primary is not null)
                return primary;

            var backup = TryReadSnapshot(_path + ".bak");
            if (backup is not null)
                return backup;

            if (!File.Exists(_path) && !File.Exists(_path + ".bak"))
                return [];

            throw new InvalidDataException(
                $"Task snapshot '{_path}' and its backup are corrupt or incompatible.");
        }
    }

    public void Save(IReadOnlyCollection<TaskItem> tasks)
    {
        lock (_gate)
        {
            var envelope = new TaskSnapshotEnvelope(
                SchemaVersion: CurrentSchemaVersion,
                Tasks: tasks.OrderBy(task => task.CreatedAt).ToArray());
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(envelope, _options));

            // 耐久写入走共享原语（临时文件 WriteThrough + 原子替换 + .bak），
            // 校验钩子沿用本 store 的既有语义：临时文件可解析才替换、主文件可解析才备份。
            DurableFile.WriteAtomic(
                _path,
                payload,
                validateTemp: tempPath => _ = ReadSnapshot(tempPath),
                canBackupSource: primaryPath => TryReadSnapshot(primaryPath) is not null,
                validateWritten: writtenPath => _ = ReadSnapshot(writtenPath));
        }
    }

    private IReadOnlyList<TaskItem>? TryReadSnapshot(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            return ReadSnapshot(path);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private IReadOnlyList<TaskItem> ReadSnapshot(string path)
    {
        var envelope = JsonSerializer.Deserialize<TaskSnapshotEnvelope>(
            File.ReadAllText(path),
            _options)
            ?? throw new InvalidDataException($"Task snapshot '{path}' is empty.");

        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Task snapshot '{path}' uses unsupported schema version {envelope.SchemaVersion}.");
        }

        if (envelope.Tasks is null
            || envelope.Tasks.Any(task => string.IsNullOrWhiteSpace(task.Id))
            || envelope.Tasks.GroupBy(task => task.Id, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            throw new InvalidDataException($"Task snapshot '{path}' contains invalid task identities.");
        }

        return envelope.Tasks;
    }

    private sealed record TaskSnapshotEnvelope(
        int SchemaVersion,
        IReadOnlyList<TaskItem>? Tasks);
}
