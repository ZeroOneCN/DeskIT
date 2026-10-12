using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskIT.Core;

namespace DeskIT.Windows;

/// <summary>
/// 目标机档案与快照的存储（M2）。落盘位置为 U 盘数据目录下的 data\snapshots\，
/// 每台机器一个子目录（目录名由机器标识规范化并加哈希后缀，保证 ASCII 且不冲突），
/// 目录内保存 profile.json（档案）与各次快照文件；同一档案仅保留最新 5 份快照。
/// </summary>
public interface ISnapshotStore
{
    /// <summary>
    /// 读取全部目标机档案（用于回退标识匹配与台账归并）。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <returns>已存在的档案列表；目录不存在时返回空列表。</returns>
    IReadOnlyList<MachineProfile> LoadAllProfiles(string dataDirectory);

    /// <summary>
    /// 读取指定机器标识的档案。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="machineId">机器标识。</param>
    /// <returns>档案；不存在时返回 null。</returns>
    MachineProfile? LoadProfile(string dataDirectory, string machineId);

    /// <summary>
    /// 保存一次采集结果：写入快照文件与档案，并按保留上限清理最旧的快照。
    /// </summary>
    /// <param name="dataDirectory">U 盘数据目录。</param>
    /// <param name="profile">归并后的档案。</param>
    /// <param name="snapshot">本次采集生成的快照。</param>
    void Save(string dataDirectory, MachineProfile profile, MachineSnapshot snapshot);
}

/// <inheritdoc cref="ISnapshotStore" />
public sealed class SnapshotStore : ISnapshotStore
{
    private const string RootFolderName = "snapshots";
    private const string ProfileFileName = "profile.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public IReadOnlyList<MachineProfile> LoadAllProfiles(string dataDirectory)
    {
        var root = GetRoot(dataDirectory);
        if (!Directory.Exists(root))
        {
            return Array.Empty<MachineProfile>();
        }

        var profiles = new List<MachineProfile>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var profile = TryReadProfile(Path.Combine(directory, ProfileFileName));
            if (profile is not null)
            {
                profiles.Add(profile);
            }
        }

        return profiles;
    }

    /// <inheritdoc />
    public MachineProfile? LoadProfile(string dataDirectory, string machineId)
    {
        var path = Path.Combine(GetRoot(dataDirectory), BuildMachineKey(machineId), ProfileFileName);
        return TryReadProfile(path);
    }

    /// <inheritdoc />
    public void Save(string dataDirectory, MachineProfile profile, MachineSnapshot snapshot)
    {
        var directory = Path.Combine(GetRoot(dataDirectory), BuildMachineKey(profile.MachineId));
        Directory.CreateDirectory(directory);

        // 先落快照，再落档案：任一步失败都不会留下“档案有记录但快照缺失”的中间态。
        var snapshotPath = Path.Combine(directory, $"{snapshot.SnapshotId}.json");
        File.WriteAllText(snapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions));

        var trimmed = CollectionRules.SelectSnapshotIdsToTrim(profile.SnapshotIds);
        if (trimmed.Count > 0)
        {
            foreach (var id in trimmed)
            {
                var path = Path.Combine(directory, $"{id}.json");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            profile = profile with { SnapshotIds = profile.SnapshotIds.Except(trimmed).ToList() };
        }

        File.WriteAllText(Path.Combine(directory, ProfileFileName), JsonSerializer.Serialize(profile, JsonOptions));
    }

    /// <summary>快照根目录。</summary>
    /// <param name="dataDirectory">数据目录。</param>
    /// <returns>data\snapshots 的绝对路径。</returns>
    private static string GetRoot(string dataDirectory) => Path.Combine(dataDirectory, RootFolderName);

    /// <summary>读取档案文件；文件缺失、损坏或为空时返回 null，不影响其他档案。</summary>
    /// <param name="path">档案文件路径。</param>
    /// <returns>档案或 null。</returns>
    private static MachineProfile? TryReadProfile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<MachineProfile>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 由机器标识生成目录名：保留 ASCII 字母数字与连字符，其余替换为下划线，并附加标识哈希前缀避免冲突。
    /// </summary>
    /// <param name="machineId">机器标识。</param>
    /// <returns>安全且唯一的目录名。</returns>
    private static string BuildMachineKey(string machineId)
    {
        var builder = new StringBuilder(machineId.Length);
        foreach (var ch in machineId)
        {
            builder.Append(ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.' or '_'
                ? ch
                : '_');
        }

        var safe = builder.ToString();
        if (safe.Length > 40)
        {
            safe = safe[..40];
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machineId)))[..8];
        return $"{safe}-{hash}";
    }
}