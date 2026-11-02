using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RiMCP.Launcher.ViewModels;

/// <summary>分片状态表的一行（纯 POCO，字段全部来自 ManifestComparer 的输出）。</summary>
public sealed class ShardRow : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string Shard { get; init; }

    /// <summary>build.json 的 formatVersion（写 "—" 表示索引缺失）。</summary>
    public required string IndexVersion { get; init; }

    /// <summary>当前环境（当前 RimWorldData）的游戏版本。</summary>
    public required string GameVersion { get; init; }

    /// <summary>defs 与 symbols 合用同一判定闸门（阶段二语义）。</summary>
    public required string DefsSymbols { get; init; }

    public required string Vectors { get; init; }
    public required string Lucene { get; init; }
    public required string Graph { get; init; }

    /// <summary>上次构建时间（manifest.builtAt，本地时间）。</summary>
    public required string LastBuiltAt { get; init; }

    /// <summary>就绪 / 需重建 / 缺失 / 截断。</summary>
    public required string State { get; init; }

    /// <summary>比对明细（ManifestComparer.Describe），用于状态列悬停提示。</summary>
    public required string Detail { get; init; }

    /// <summary>变化类型（UpdateAdvisor 诊断结果的中文展示）。</summary>
    public required string ChangeKindText { get; init; }

    /// <summary>UpdateAdvisor 给出的原因，用于“变化类型”列悬停提示。</summary>
    public required string ChangeReason { get; init; }

    /// <summary>是否被勾选（供“按分片构建”提示，Indexer 无分片选择参数）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
