using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>별칭 하나: 이름, 핀 1 활성, 쓰인 곳 수, 주석, 검사 결과.</summary>
public sealed record AliasVm(string Name, bool High, int Uses, string Comment, Severity? Mark)
{
    public string UsesText => Uses.ToString();
    public string Tip => Comment.Length == 0 ? Name : $"{Name} · {Comment}";
}

/// <summary>IO 한 줄: 코어 ID, 코어 이름, 별칭들, 주석(별칭 주석을 잇는다).</summary>
public sealed class IoRowVm(int id, string core, IReadOnlyList<AliasVm> aliases) : Observable
{
    public int Id { get; } = id;
    public string Core { get; } = core;
    public IReadOnlyList<AliasVm> Aliases { get; } = aliases;
    public bool HasAliases => Aliases.Count > 0;
    public override string ToString() => $"{Id} {Core}";
    public string Comment { get; } = string.Join(" · ", aliases.Select(a => a.Comment).Where(c => c.Length > 0));

    bool hot;
    public bool IsHot { get => hot; set => Set(ref hot, value); }
}

/// <summary>벨트 한 줄: 이름, CW 묶음, CCW 묶음(없는 방향은 —).</summary>
public sealed record BeltRowVm(string Name, string Cw, string Ccw);

/// <summary>IO 맵 화면(사양 9.2절, 1차 읽기 전용): 입력 32줄·출력 32줄, 미연결 별칭, 벨트 표.</summary>
public sealed class IoVm : Observable
{
    public IReadOnlyList<IoRowVm> Inputs { get; private set; } = [];
    public IReadOnlyList<IoRowVm> Outputs { get; private set; } = [];
    public IReadOnlyList<AliasVm> Unwired { get; private set; } = [];
    public IReadOnlyList<BeltRowVm> Belts { get; private set; } = [];
    public bool HasUnwired => Unwired.Count > 0;
    public bool HasBelts => Belts.Count > 0;
    public string CountText => $"별칭 {Inputs.Concat(Outputs).Sum(r => r.Aliases.Count) + Unwired.Count} · 벨트 {Belts.Count}";

    static readonly HashSet<string> AliasFiles = new(["io_map.h", "custom_io.h", "tag_table.c", "act_ids.h"], StringComparer.OrdinalIgnoreCase);

    public void Reload(Site s, IReadOnlyList<Finding> findings)
    {
        // 쓰인 곳: 인자에 그 이름이 나오는 문장 수(벨트 정의 포함, 자기 정의 줄 빼고)
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in s.Files)
            foreach (var st in f.Statements)
                foreach (var n in st.Args.SelectMany(a => a.Node.Names()).Distinct().Where(n => n != st.Name))
                    uses[n] = uses.GetValueOrDefault(n) + 1;

        AliasVm Alias(NameDef d)
        {
            var mark = findings.Where(x => ReferenceEquals(x.Piece, d.Def)).OrderBy(x => x.Severity).FirstOrDefault()?.Severity;
            return new(d.Name, d.Kind == NameKind.InputHigh, uses.GetValueOrDefault(d.Name), BlockHost.Strip(d.Comment), mark);
        }
        var site = s.Names.All.Where(d => !d.Core && !d.Weak && d.File != null && AliasFiles.Contains(d.File.Name) && d.Def is Statement { Shape: StatementShape.Define }).ToList();

        List<IoRowVm> Rows(IReadOnlyList<CoreName> core, params NameKind[] kinds) =>
            Enumerable.Range(0, 32).Select(i => new IoRowVm(i, core.FirstOrDefault(c => c.Value == i)?.Name ?? "—",
                site.Where(d => kinds.Contains(d.Kind) && d.Value == i).Select(Alias).ToList())).ToList();

        Inputs = Rows(CoreConstants.Inputs, NameKind.Input, NameKind.InputHigh);
        Outputs = Rows(CoreConstants.Outputs, NameKind.Output);
        Unwired = site.Where(d => d.Kind == NameKind.IoNone).Select(Alias).ToList();
        string Group(Statement? st) => st == null ? "—" : st.Args[0].Node is CallNode c ? string.Join("  ", c.Args.Select(a => BlockBuilder.Collapse(a.Text))) : BlockBuilder.Collapse(st.Args[0].Text);
        Belts = s.Names.Belts.Values.Select(b => new BeltRowVm(b.Name, Group(b.Cw), Group(b.Ccw))).ToList();
        foreach (var n in new[] { nameof(Inputs), nameof(Outputs), nameof(Unwired), nameof(Belts), nameof(HasUnwired), nameof(HasBelts), nameof(CountText) }) Raise(n);
    }

    /// <summary>검사 줄에서: 그 별칭이 든 줄을 강조(정의 이름).</summary>
    public void SelectName(string name)
    {
        IoRowVm? hit = null;
        foreach (var r in Inputs.Concat(Outputs))
        {
            r.IsHot = r.Aliases.Any(a => a.Name == name);
            if (r.IsHot) hit ??= r;
        }
        if (hit != null) ScrollTo?.Invoke(hit);
    }

    public event Action<object>? ScrollTo;
}
