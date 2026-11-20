using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// 이름 사전 종류(사양 3.1절). Alias = 표 파일 안 <c>#define 이름 값</c>(숫자·코어 상수),
/// Macro = 함수 꼴 <c>#define</c>(원문, 뜻을 모름), Constant = 그 밖의 셈 수 있는 이름(개수, 코어 방향 값 등).
/// </summary>
public enum NameKind
{
    Profile, Input, InputHigh, IoNone, Output, Belt, LoadIf, SeqId, SeqTable, Label, ActId, ActTable,
    State, CallNo, Alias, Macro, CoreParam, FndParam, Constant,
}

/// <summary>
/// 사전의 이름 하나. Value = 숫자 값(입출력은 핀 ID, 프로파일은 번호), 모르면 null.
/// File·Def = 정의한 파일·조각(코어 이름은 null). Weak = 원문에서 글자로 찾은 정의(<c>#ifndef</c> 기본값 등). 겹침 검사에서 뺀다.
/// </summary>
public sealed record NameDef(string Name, NameKind Kind, long? Value, SourceFile? File, Piece? Def, bool Weak = false)
{
    public bool Core => File == null;

    /// <summary>정의 줄의 꼬리 주석(쓰는 호기 등).</summary>
    public string Comment => IoProfile.StripComment((Def as Statement)?.TailComment ?? "");

    /// <summary>IO 맵 신호가 쓰는 프로파일(<c>/* pf: … */</c>). 주석이 없으면 null = 모두.</summary>
    public IReadOnlyList<string>? Profiles => Def is Statement { Shape: StatementShape.Define } st ? IoProfile.Of(st) : null;
}

/// <summary>벨트 n: <c>BELT&lt;n&gt;_CW</c>·<c>_CCW</c> 정의. 없는 쪽은 null, <c>NO_OUT</c> 쪽은 출력 목록이 비었다.</summary>
public sealed record BeltDef(string Name, Statement? Cw, Statement? Ccw, IReadOnlyList<long> CwPins, IReadOnlyList<long> CcwPins)
{
    public bool Complete => Cw != null && Ccw != null;

    public IReadOnlyList<long> Pins(string dir) => dir == "CW" ? CwPins : CcwPins;

    public IEnumerable<long> AllPins => CwPins.Concat(CcwPins).Distinct();
}

/// <summary>
/// 이름 사전. 코어 상수 표 + 사이트 파일에서 만든다(사양 4.3절). 조각에서 계산한 값이다. 따로 저장하지 않는다.
/// 라벨은 파일마다 따로 든다(<see cref="Find"/> 의 file).
/// </summary>
public sealed class NameDict
{
    readonly List<NameDef> all = [];
    readonly Dictionary<string, List<NameDef>> byName = new(StringComparer.Ordinal);

    public IReadOnlyList<NameDef> All => all;
    public IReadOnlyDictionary<string, BeltDef> Belts { get; private set; } = new Dictionary<string, BeltDef>();

    /// <summary>사이트 코어가 v0.58.0 이상(<see cref="Site.NewDsl"/>). 새 줄 쓰기·묶음 개수 한계가 이것으로 갈린다.</summary>
    public bool NewDsl { get; internal set; }

    internal void Add(NameDef d)
    {
        all.Add(d);
        if (!byName.TryGetValue(d.Name, out var list)) byName[d.Name] = list = [];
        list.Add(d);
    }

    internal void SetBelts(Dictionary<string, BeltDef> belts) => Belts = belts;

    public IReadOnlyList<NameDef> Get(string name) => byName.TryGetValue(name, out var l) ? l : [];

    /// <summary>첫 정의. 라벨은 <paramref name="file"/> 의 것만.</summary>
    public NameDef? Find(string name, SourceFile? file = null) =>
        Get(name).FirstOrDefault(d => d.Kind != NameKind.Label || d.File == file);

    public IEnumerable<NameDef> Of(NameKind kind) => all.Where(d => d.Kind == kind);

    /// <summary>풀이 나무의 숫자 값(숫자, 값 있는 이름, a ± b). 모르면 null.</summary>
    public long? Value(Node node, SourceFile? file = null) => node switch
    {
        NumberNode n => n.Value,
        NameNode n => Find(n.Name, file)?.Value,
        BinaryNode b when Value(b.Left, file) is long l && Value(b.Right, file) is long r => b.Op == '+' ? l + r : l - r,
        _ => null,
    };

    /// <summary>코어 이름(문법 표 이름, 코어 상수, 고정 낱말). N-5 겹침 기준.</summary>
    public static readonly IReadOnlySet<string> CoreNames = BuildCoreNames(false);

    /// <summary><see cref="CoreNames"/> + 고정 낱말. 매크로(<c>#define</c>) 이름은 낱말과도 겹치면 안 된다.</summary>
    public static readonly IReadOnlySet<string> CoreNamesAndWords = BuildCoreNames(true);

    static HashSet<string> BuildCoreNames(bool words)
    {
        var s = new HashSet<string>(GrammarTable.OldNames, StringComparer.Ordinal);
        foreach (var c in CoreConstants.Inputs.Concat(CoreConstants.Outputs).Concat(CoreConstants.SeqIds).Concat(CoreConstants.Directions)) s.Add(c.Name);
        foreach (var p in CoreConstants.Params) s.Add(p.Core);
        foreach (var (alias, _) in CoreConstants.ParamAliases) s.Add(alias);
        s.Add("SEQ_SITE_BASE");
        s.Add("SEQ_MAX");
        if (words)
        {
            foreach (var w in GrammarTable.Rows.SelectMany(r => r.Args).SelectMany(a => a.Words ?? [])) s.Add(w);
            s.Add("KEY");
        }
        return s;
    }

    /// <summary>가까운 이름 셋까지(편집 거리, 앞뒤 붙은 글자 포함). N-1 후보.</summary>
    public static IReadOnlyList<string> Near(string name, IEnumerable<string> pool)
    {
        int limit = Math.Max(2, name.Length / 3);
        return pool.Distinct()
            .Select(p => (p, d: Distance(name, p)))
            .Where(x => x.d <= limit || x.p.EndsWith(name, StringComparison.Ordinal) || x.p.StartsWith(name, StringComparison.Ordinal))
            .OrderBy(x => x.d).ThenBy(x => x.p, StringComparer.Ordinal)
            .Take(3).Select(x => x.p).ToList();
    }

    static int Distance(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int t = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = t;
            }
        }
        return d[b.Length];
    }
}
