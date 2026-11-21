using System.Text.RegularExpressions;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// 열린 사이트 = 파일들(조각 목록) + 이름 사전 + 뜻 보기. 보기는 조각을 가리키고 사본을 두지 않는다(설계서 4.1절).
/// </summary>
public sealed partial class Site
{
    public string Folder { get; }
    public string? CoreFolder { get; }
    public IReadOnlyList<SourceFile> Files { get; }
    public NameDict Names { get; } = new();

    /// <summary><c>core/VERSION</c> 첫 칸. 코어 폴더가 없으면 null.</summary>
    public string? CoreVersion { get; }

    /// <summary>
    /// 코어 <c>VERSION</c> 이 v0.58.0 이상이다: 새 DSL 형식(<c>AND( … )</c>·<c>AGV_SPEED( m/min )</c> 등)으로 새 줄을 쓰고 새 규칙(D-*)을 건다.
    /// 코어 폴더가 없거나 버전을 모르면 옛 형식이다.
    /// </summary>
    public bool NewDsl => GrammarTable.IsNew(CoreVersion);

    public IReadOnlyList<ZoneBlock> Zones { get; }
    public IReadOnlyList<ProfileDef> Profiles { get; }
    public IReadOnlyList<ProfileRow> ProfileRows { get; }
    public IReadOnlyList<TagRule> TagRules { get; }
    public IReadOnlyList<SeqTable> Seqs { get; }
    public IReadOnlyList<MapRow> SeqMap { get; }
    public IReadOnlyList<MapRow> ActMap { get; }
    public IReadOnlyList<Statement> ManualRows { get; }
    public IReadOnlyList<ManChain> ManualChains { get; }

    /// <summary>
    /// 읽을 때(또는 마지막으로 저장한 때)의 디스크 상태(파일 이름 → 크기·시각·해시). 저장 전에 바깥에서 바뀌었는지 본다(사양 5.7절).
    /// 편집 명령이 만든 사이트들은 한 기록을 같이 본다. 그래서 저장한 뒤 되돌린 사이트도 지금 디스크를 기준으로 견준다.
    /// <see cref="FromFiles"/> 로 연 사이트는 비었다.
    /// </summary>
    public IReadOnlyDictionary<string, FileStamp> Stamps => disk.Stamps;

    /// <summary>디스크 기록 한 벌. 되돌리기 단계(조각 목록)와 따로 간다.</summary>
    internal sealed class DiskRecord(IReadOnlyDictionary<string, FileStamp> stamps)
    {
        public IReadOnlyDictionary<string, FileStamp> Stamps = stamps;
    }

    DiskRecord disk;

    /// <summary>저장했다: other(저장 뒤 다시 읽은 사이트)의 디스크 기록을 이 사이트와 같은 기록을 보는 사이트 모두가 쓴다.</summary>
    internal void AdoptDisk(Site other)
    {
        disk.Stamps = other.disk.Stamps;
        other.disk = disk;
    }

    /// <summary>other 의 디스크 기록을 같이 본다(되돌리기 단계들을 저장 뒤의 기록에 맞춘다).</summary>
    internal void ShareDisk(Site other) => disk = other.disk;

    public static Site Open(string siteFolder, string? coreFolder = null)
    {
        // 시각을 먼저 잡는다. 읽는 사이에 바뀌면 시각이 달라 해시를 다시 본다
        var times = FileZones.Files.Select(f => Path.Combine(siteFolder, f.FileName)).Where(System.IO.File.Exists)
            .ToDictionary(p => Path.GetFileName(p), System.IO.File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
        var files = SiteFolder.Read(siteFolder);
        var stamps = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            var b = f.ToBytes();
            stamps[f.Name] = new FileStamp(b.Length, times.GetValueOrDefault(f.Name), FileStamp.Hash(b));
        }
        return new(siteFolder, coreFolder, files, new DiskRecord(stamps));
    }

    /// <summary>이미 읽은 파일로 연다(시험, 편집 뒤 다시 보기).</summary>
    public static Site FromFiles(IEnumerable<SourceFile> files, string folder = "", string? coreFolder = null) =>
        new(folder, coreFolder, files.ToList(), new DiskRecord(new Dictionary<string, FileStamp>()));

    /// <summary>같은 폴더·읽은 기록으로 파일만 바꾼 새 사이트(편집 명령의 결과).</summary>
    internal Site With(IReadOnlyList<SourceFile> files) => new(Folder, CoreFolder, files, disk);

    /// <summary>파일과 디스크 기록을 바꾼 새 사이트(초안 복원: 낡은 파일은 초안을 쓴 때의 해시를 기록으로 둔다).</summary>
    internal Site With(IReadOnlyList<SourceFile> files, IReadOnlyDictionary<string, FileStamp> stamps) =>
        new(Folder, CoreFolder, files, new DiskRecord(stamps));

    Site(string folder, string? coreFolder, IReadOnlyList<SourceFile> files, DiskRecord disk)
    {
        Folder = folder;
        CoreFolder = coreFolder;
        Files = files;
        this.disk = disk;
        if (coreFolder != null)
        {
            var v = Path.Combine(coreFolder, "VERSION");
            if (System.IO.File.Exists(v)) CoreVersion = System.IO.File.ReadAllText(v).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }

        Zones = files.SelectMany(BlocksOf).ToList();
        Names.NewDsl = NewDsl;
        Names.IdleOk = GrammarTable.IdleOk(CoreVersion);
        Names.CoreVersion = CoreVersion;
        BuildNames();

        Profiles = Names.Of(NameKind.Profile).Where(d => !d.Core && d.Def is Statement)
            .Select(d => new ProfileDef(d.Name, d.Value, (Statement)d.Def!)).ToList();
        ProfileRows = ZonesOf("PROFILE_TABLE_BEGIN").SelectMany(z => z.Statements)
            .Select(st => new ProfileRow(st, ProfileKey.Name(st.Args[0]), Num(st.Args[1]), Num(st.Args[2]), Num(st.Args[3]))).ToList();
        TagRules = ZonesOf("TAG_TABLE_BEGIN").SelectMany(z => z.Statements).Select(st => new TagRule(st)).ToList();
        Seqs = Zones.Where(z => z.Open is "SEQ_BEGIN" or "ACT_BEGIN").Select(z => new SeqTable(z)).ToList();
        SeqMap = ZonesOf("SEQ_MAP_BEGIN").SelectMany(z => z.Statements.Select(st => new MapRow(st, z))).ToList();
        ActMap = ZonesOf("ACT_MAP_BEGIN").SelectMany(z => z.Statements.Select(st => new MapRow(st, z))).ToList();
        ManualRows = ZonesOf("MANUAL_BEGIN").SelectMany(z => z.Statements).ToList();
        ManualChains = Chains(ManualRows);
    }

    public SourceFile? File(string name) => Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<ZoneBlock> ZonesOf(string open) => Zones.Where(z => z.Open == open);

    /// <summary>문장이 든 구역(없으면 null).</summary>
    public ZoneBlock? ZoneOf(Statement st) => Zones.FirstOrDefault(z => z.Statements.Any(s => ReferenceEquals(s, st)));

    public SourceFile FileOf(Piece p) => Files.First(f => f.Pieces.Any(x => ReferenceEquals(x, p)));

    /// <summary>인자의 숫자 값(숫자, 값 있는 별칭·상수).</summary>
    public long? Num(Arg a, SourceFile? file = null) => Names.Value(a.Node, file);

    /// <summary>이 프로파일 이름의 프로파일 표 줄.</summary>
    public IEnumerable<ProfileRow> RowsOf(string profile) => ProfileRows.Where(r => r.Profile == profile);

    /// <summary>호기 id 가 받을 수 있는 프로파일(위에서 아래로 첫 줄이 맞는다. 타입 0 줄 뒤는 닿지 않는다).</summary>
    public IReadOnlyList<string> ProfilesAt(long id)
    {
        var list = new List<string>();
        foreach (var r in ProfileRows.Where(r => r.First <= id && id <= r.Last))
        {
            if (!list.Contains(r.Profile)) list.Add(r.Profile);
            if (r.Type == 0) break;
        }
        return list;
    }

    static IEnumerable<ZoneBlock> BlocksOf(SourceFile f)
    {
        var ps = f.Pieces;
        for (int i = 0; i < ps.Count; i++)
        {
            if (ps[i] is not Statement { Shape: StatementShape.Macro, Zone: null } begin) continue;
            var def = FileZones.For(f.Name).Zones.FirstOrDefault(z => z.Open == begin.Name);
            if (def == null) continue;
            var body = new List<Piece>();
            Statement? end = null;
            int j = i + 1;
            for (; j < ps.Count; j++)
            {
                if (ps[j] is Statement { Zone: null } e && e.Name == def.Close) { end = e; break; }
                body.Add(ps[j]);
            }
            yield return new ZoneBlock(f, begin, body, end);
            i = j;
        }
    }

    static List<ManChain> Chains(IReadOnlyList<Statement> rows)
    {
        var chains = new List<ManChain>();
        List<Statement>? cur = null;
        bool Same(Statement a, Statement b) => a.Args[0].Text == b.Args[0].Text && a.Args[1].Text == b.Args[1].Text;
        foreach (var st in rows)
        {
            bool open = cur != null && cur[^1].Name == "MAN" && Same(cur[^1], st);
            if (st.Name == "MAN" || st.Name == "MAN_ELSE")
            {
                if (open) cur!.Add(st);
                else chains.Add(new ManChain(cur = [st]));
                if (st.Name == "MAN_ELSE") cur = null;
            }
            else cur = null;
        }
        return chains;
    }

    // ---- 이름 사전 ----

    [GeneratedRegex(@"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)(\()?[ \t]*(\S*)", RegexOptions.Multiline)]
    private static partial Regex RawDefineRx();

    [GeneratedRegex(@"^BELT(\w+)_(CW|CCW)$")]
    private static partial Regex BeltRx();

    void BuildNames()
    {
        foreach (var c in CoreConstants.Inputs)
            Names.Add(new(c.Name, c.Name == "IO_NONE" ? NameKind.IoNone : NameKind.Input, c.Value, null, null));
        foreach (var c in CoreConstants.Outputs) Names.Add(new(c.Name, NameKind.Output, c.Value, null, null));
        foreach (var c in CoreConstants.SeqIds) Names.Add(new(c.Name, NameKind.SeqId, c.Value, null, null));
        foreach (var c in CoreConstants.Directions) Names.Add(new(c.Name, NameKind.Constant, c.Value, null, null));
        foreach (var p in CoreConstants.Params.Where(p => p.Since.Length == 0 || GrammarTable.AtLeast(CoreVersion, p.Since) || CoreVersion == null))
        {
            Names.Add(new(p.Core, NameKind.CoreParam, p.Number, null, null));
            foreach (var t in p.TableNames) Names.Add(new(t, NameKind.FndParam, p.Number, null, null));
        }
        foreach (var (alias, number) in CoreConstants.ParamAliases) Names.Add(new(alias, NameKind.CoreParam, number, null, null));
        Names.Add(new("SEQ_SITE_BASE", NameKind.Constant, CoreConstants.SeqSiteBase, null, null));
        Names.Add(new("SEQ_MAX", NameKind.Constant, CoreConstants.SeqMax, null, null));
        Names.Add(new("ANY_PROFILE", NameKind.Profile, 0, null, null));

        // 원문 안 #define(#ifndef 기본값, 함수 꼴). 겹침 검사에서 뺀다
        foreach (var f in Files)
            foreach (var p in f.Pieces.Where(p => p.Kind == PieceKind.Raw))
                foreach (Match m in RawDefineRx().Matches(p.Text))
                {
                    var name = m.Groups[1].Value;
                    if (m.Groups[2].Success) Names.Add(new(name, NameKind.Macro, null, f, p, Weak: true));
                    else if (CNumber(m.Groups[3].Value) is long v) Names.Add(new(name, NameKind.Constant, v, f, p, Weak: true));
                }

        Enumerators("site.h", n => n.StartsWith("PF_", StringComparison.Ordinal) ? NameKind.Profile : NameKind.Constant);
        Enumerators("seq_custom.h", n =>
            IsCountName(n) ? NameKind.Constant
            : n.StartsWith("SEQ_", StringComparison.Ordinal) ? NameKind.SeqId
            : n.StartsWith("CALL_", StringComparison.Ordinal) ? NameKind.CallNo
            : n.StartsWith("ST_", StringComparison.Ordinal) ? NameKind.State
            : NameKind.Constant);
        Enumerators("act_ids.h", n => n.StartsWith("ACT_", StringComparison.Ordinal) && !IsCountName(n) ? NameKind.ActId : NameKind.Constant);
        Enumerators("tag_custom.h", n => n.StartsWith("TAGC_", StringComparison.Ordinal) && !IsCountName(n) ? NameKind.CallNo : NameKind.Constant);

        var belts = new Dictionary<string, (Statement? Cw, Statement? Ccw, List<long> CwPins, List<long> CcwPins)>(StringComparer.Ordinal);
        foreach (var fname in new[] { "io_map.h", "custom_io.h", "act_ids.h", "tag_table.c" })
        {
            if (File(fname) is not { } f) continue;
            foreach (var st in f.Statements.Where(s => s.Shape == StatementShape.Define))
            {
                var value = st.Args[0].Node;
                var bm = BeltRx().Match(st.Name);
                if (bm.Success && (value is CallNode { Name: "OUTS" } || value is NameNode { Name: "NO_OUT" }))
                {
                    var key = bm.Groups[1].Value;
                    var b = belts.TryGetValue(key, out var x) ? x : (null, null, [], []);
                    var pins = value is CallNode c ? c.Args.Select(a => Names.Value(a.Node)).OfType<long>().ToList() : [];
                    belts[key] = bm.Groups[2].Value == "CW" ? (st, b.Ccw, pins, b.CcwPins) : (b.Cw, st, b.CwPins, pins);
                    Names.Add(new(st.Name, NameKind.Constant, null, f, st));
                    continue;
                }
                Names.Add(Classify(st, f));
            }
        }
        Names.SetBelts(belts.ToDictionary(kv => kv.Key, kv => new BeltDef(kv.Key, kv.Value.Cw, kv.Value.Ccw, kv.Value.CwPins, kv.Value.CcwPins)));

        foreach (var f in Files)
            foreach (var st in f.Statements.Where(s => s.Shape == StatementShape.Macro))
            {
                switch (st.Name)
                {
                    case "LOAD_IF": Names.Add(new(st.Args[0].Text, NameKind.LoadIf, null, f, st)); break;
                    case "SEQ_BEGIN": Names.Add(new(st.Args[0].Text, NameKind.SeqTable, null, f, st)); break;
                    case "ACT_BEGIN": Names.Add(new(st.Args[0].Text, NameKind.ActTable, null, f, st)); break;
                    case "SEQ_LABELS":
                        for (int i = 0; i < st.Args.Count; i++) Names.Add(new(st.Args[i].Text, NameKind.Label, i + 1, f, st));
                        break;
                }
            }

        foreach (var f in Files.Where(f => FileZones.For(f.Name).Top.Contains("SEQ_LABELS"))) EnumLabels(f);
    }

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex CommentRx();

    [GeneratedRegex(@"\benum\b[^{};]*\{([^{}]*)\}")]
    private static partial Regex EnumRx();

    [GeneratedRegex(@"^\s*([A-Za-z_]\w*)\s*(?:=\s*(.+?))?\s*$", RegexOptions.Singleline)]
    private static partial Regex EnumItemRx();

    /// <summary>
    /// 시퀀스 표 파일 안 <c>enum { 이름 = 값, … }</c>(표 구역 밖 원문, 한 줄·여러 줄, 주석 섞임)의 열거자도 그 파일의 라벨이다.
    /// 읽기만 한다: 정의 조각은 이름이 시작하는 원문 줄이고 편집기는 선언을 고치지 않는다.
    /// </summary>
    void EnumLabels(SourceFile f)
    {
        var sb = new System.Text.StringBuilder();
        var starts = new List<(int At, Piece P)>();
        foreach (var p in f.Pieces.Where(p => p.Kind != PieceKind.Statement)) { starts.Add((sb.Length, p)); sb.Append(p.Text); }
        var text = CommentRx().Replace(sb.ToString(), m => new string(' ', m.Length));
        foreach (Match m in EnumRx().Matches(text))
        {
            long? next = 0;
            var body = m.Groups[1].Value;
            int depth = 0, from = 0;
            for (int i = 0; i <= body.Length; i++)
            {
                char c = i < body.Length ? body[i] : ',';
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    if (EnumItemRx().Match(body[from..i]) is { Success: true } it)
                    {
                        var expr = it.Groups[2];
                        long? value = !expr.Success ? next : Expr.Parse(expr.Value) is { } node ? Names.Value(node, f) : null;
                        var piece = starts.Last(s => s.At <= m.Groups[1].Index + from + it.Groups[1].Index).P;
                        Names.Add(new(it.Groups[1].Value, NameKind.Label, value, f, piece));
                        next = value + 1;
                    }
                    from = i + 1;
                }
            }
        }
    }

    /// <summary>C 정수 글자(<see cref="CNum.Value"/>: 10진, 0x 16진, 앞자리 0 = 8진, 접미사 u·l).</summary>
    static long? CNumber(string s) => CNum.Value(s);

    /// <summary>개수 표지 이름 꼴: <c>…_COUNT</c>, <c>…_COUNT_</c>.</summary>
    internal static bool IsCountName(string name) =>
        name.EndsWith("_COUNT", StringComparison.Ordinal) || name.EndsWith("_COUNT_", StringComparison.Ordinal);

    /// <summary>파일의 enum 마다 그 안 열거자들(파일 차례).</summary>
    internal static List<List<Statement>> EnumGroups(SourceFile f)
    {
        var groups = new List<List<Statement>>();
        List<Statement>? cur = null;
        foreach (var p in f.Pieces)
        {
            if (p.Zone == null) { cur = null; continue; }
            if (p is not Statement { Shape: StatementShape.Enumerator } st) continue;
            if (cur == null) groups.Add(cur = []);
            cur.Add(st);
        }
        return groups;
    }

    /// <summary>
    /// 그 enum 의 개수 표지 = 값 없는 끝 열거자 <c>…_COUNT</c>·<c>…_COUNT_</c>(없으면 null).
    /// 새 열거자는 그 앞에 넣고, 프로파일·ID 목록에는 넣지 않는다.
    /// </summary>
    internal static Statement? CountMark(IReadOnlyList<Statement> group) =>
        group.Count > 0 && group[^1] is { Args.Count: 0 } last && IsCountName(last.Name) ? last : null;

    /// <summary>enum 열거자: 값 = 적은 식, 없으면 앞 값 + 1(enum 마다 0 부터). 개수 표지는 상수다.</summary>
    void Enumerators(string fileName, Func<string, NameKind> kind)
    {
        if (File(fileName) is not { } f) return;
        foreach (var group in EnumGroups(f))
        {
            var mark = CountMark(group);
            long? next = 0;
            foreach (var st in group)
            {
                long? v = st.Args.Count > 0 ? Names.Value(st.Args[0].Node, f) : next;
                Names.Add(new(st.Name, ReferenceEquals(st, mark) ? NameKind.Constant : kind(st.Name), v, f, st));
                next = v + 1;
            }
        }
    }

    /// <summary><c>#define 이름 값</c> 의 종류: 입력·핀 1 활성·미연결·출력·별칭(숫자·상수).</summary>
    NameDef Classify(Statement st, SourceFile f)
    {
        var node = st.Args[0].Node;
        if (node is CallNode { Name: "IN_ACTIVE_HIGH" or "IN_INVERTED", Args.Count: 1 } h)
            return new(st.Name, NameKind.InputHigh, Names.Value(h.Args[0].Node), f, st);
        if (node is NameNode n && Names.Find(n.Name) is { } target)
        {
            var k = target.Kind switch
            {
                NameKind.Input or NameKind.InputHigh or NameKind.IoNone or NameKind.Output => target.Kind,
                _ => NameKind.Alias,
            };
            return new(st.Name, k, target.Value, f, st);
        }
        return new(st.Name, NameKind.Alias, Names.Value(node, f), f, st);
    }
}
