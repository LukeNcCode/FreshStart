using System.Text.Json;
using FreshStart.Core;
using FreshStart.Core.Operations;
using FreshStart.Services.Registry;

namespace FreshStart.Services.FileSystem;

/// <summary>
/// 文件/目录移动（也用于启动文件夹的 .disabled 重命名、发送到项目的隔离）。
/// 与注册表操作一样，移动前记录源/目标状态，失败自动移回。
/// </summary>
public sealed class FileMoveOperation : OperationBase
{
    private readonly string _source;
    private readonly string _destination;
    private readonly string _displayName;
    private readonly bool _isDirectory;
    private readonly RiskLevel _risk;

    public FileMoveOperation(
        string id,
        string name,
        string description,
        RiskLevel risk,
        string source,
        string destination,
        string? displayName = null)
    {
        Id = id;
        Name = name;
        Description = description;
        _risk = risk;
        _source = source;
        _destination = destination;
        _displayName = displayName ?? Path.GetFileName(source);
        _isDirectory = Directory.Exists(source);
    }

    public override string Id { get; }
    public override string Name { get; }
    public override string Description { get; }
    public override RiskLevel RiskLevel => _risk;

    public override IReadOnlyList<ChangeDescriptor> DescribeChanges() => new[]
    {
        new ChangeDescriptor
        {
            Target = _source,
            Kind = _isDirectory ? "文件夹" : "文件",
            Before = _source,
            After = _destination,
            Impact = Description
        }
    };

    public override Task<OperationSnapshot> CaptureCurrentStateAsync(CancellationToken ct)
    {
        var sourceEntry = CreateEntry(_source);
        var destinationEntry = CreateEntry(_destination);
        sourceEntry.Aux = "source";
        destinationEntry.Aux = "destination";
        return Task.FromResult(CreateSnapshot(new[] { sourceEntry, destinationEntry }));
    }

    protected override Task<OperationResult> ApplyCoreAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        if (!Exists(_source)) return Task.FromResult(OperationResult.Fail($"源不存在：{_source}"));
        if (Exists(_destination)) return Task.FromResult(OperationResult.Fail($"目标已存在：{_destination}"));

        progress?.Report(new OperationStep { Message = "正在移动文件…", Percent = 60 });
        var parent = Path.GetDirectoryName(_destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        if (_isDirectory) Directory.Move(_source, _destination);
        else File.Move(_source, _destination, overwrite: false);

        Ensure(!Exists(_source), "移动后源仍然存在");
        Ensure(Exists(_destination), "移动后目标不存在");
        return Task.FromResult(OperationResult.Ok($"已移动：{_displayName}"));
    }

    protected override Task<OperationResult> RollbackCoreAsync(OperationSnapshot state, IProgress<OperationStep>? progress, CancellationToken ct)
    {
        progress?.Report(new OperationStep { Message = "正在移回…", Percent = 60 });
        if (!Exists(_destination))
        {
            return Task.FromResult(Exists(_source)
                ? OperationResult.Ok("源文件仍在，无需移回")
                : OperationResult.Fail("目标与源都不存在，无法回滚"));
        }

        var parent = Path.GetDirectoryName(_source);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        if (_isDirectory) Directory.Move(_destination, _source);
        else File.Move(_destination, _source, overwrite: true);

        Ensure(Exists(_source), "回滚后源文件不存在");
        return Task.FromResult(OperationResult.Ok("已移回"));
    }

    private SnapshotEntry CreateEntry(string path)
    {
        var exists = Exists(path);
        var isDir = Directory.Exists(path);
        return new SnapshotEntry
        {
            Kind = SnapshotKind.File,
            Target = path,
            Existed = exists,
            DataJson = JsonSerializer.Serialize(new FileEntry { Path = path, IsDirectory = isDir }, JsonConfig.Options)
        };
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private sealed class FileEntry
    {
        public string Path { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
    }
}
