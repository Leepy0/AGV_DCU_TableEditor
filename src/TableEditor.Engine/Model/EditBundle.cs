using System.Text.RegularExpressions;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// 한 동작이 여러 파일을 고치는 명령(사양 5.4절 표). 한 명령 = 한 번의 다시 읽기라서 하나라도 막히면 아무 파일도 안 바뀐다.
/// 이름 바꾸기·지우기 전에 사이트 폴더의 모든 <c>.c</c>·<c>.h</c> 에서 그 이름을 찾아 원문 블록·원문 파일에서 쓰이면 막는다(N-6).
/// </summary>
public static partial class BundleEdit
{
    // ---- 프로파일 ----

    /// <summary>프로파일 추가: <c>site.h</c> enum 끝(앞 줄에 쉼표), <c>profiles.c</c> 표 끝에 <c>PROFILE</c> 줄.</summary>
    public static EditResult AddProfile(Site site, string name, long type, long first, long last)
    {
        if (NewName(site, name, "PF_") is { } why) return EditResult.Fail(why);
        var zone = site.ZonesOf("PROFILE_TABLE_BEGIN").FirstOrDefault();
        if (zone == null) return EditResult.Fail("프로파일 표 없음");
        var lastPf = site.File("site.h")?.Statements.LastOrDefault(s => s.Shape == StatementShape.Enumerator && s.Name.StartsWith("PF_", StringComparison.Ordinal));
        if (lastPf == null) return EditResult.Fail("site.h 프로파일 enum 없음");
        var sp = new Splice(site);
        AppendEnumerator(sp, lastPf, name);
        SiteEdit.InsertInto(sp, zone, zone.Statements.Count, [new NewStatement("PROFILE", [name, type.ToString(), first.ToString(), last.ToString()])]);
        return sp.Commit();
    }

    /// <summary>프로파일 이름 바꾸기: 정의(<c>site.h</c>)와 모든 표의 사용처.</summary>
    public static EditResult RenameProfile(Site site, string oldName, string newName)
    {
        if (site.Names.Find(oldName) is not { Kind: NameKind.Profile, Core: false, Def: Statement { Shape: StatementShape.Enumerator } })
            return EditResult.Fail($"프로파일 아님 · {oldName}");
        if (NewName(site, newName, "PF_") is { } why) return EditResult.Fail(why);
        if (RawUse(site, oldName) is { } at) return EditResult.Fail($"N-6 원문에서 씀 · {at}");
        return Rename(site, [(oldName, newName)], []);
    }

    /// <summary>
    /// 프로파일 삭제: 정의와 그 프로파일의 <c>profiles.c</c> 줄을 지운다. 다른 줄(태그·시퀀스·수동 등)이 하나라도 쓰면 막는다.
    /// 뒤 프로파일의 번호가 하나씩 당겨진다(enum).
    /// </summary>
    public static EditResult DeleteProfile(Site site, string name)
    {
        if (site.Names.Find(name) is not { Kind: NameKind.Profile, Core: false, Def: Statement { Shape: StatementShape.Enumerator } def })
            return EditResult.Fail($"프로파일 아님 · {name}");
        var rx = Word(name);
        var own = new HashSet<Piece>(site.ProfileRows.Where(r => r.Profile == name).Select(r => (Piece)r.St).Append(def), ReferenceEqualityComparer.Instance);
        var uses = site.Files.SelectMany(f => f.Statements.Where(s => !own.Contains(s) && s.Args.Any(a => rx.IsMatch(a.Text))).Select(s => $"{f.Name}:{s.StartLine}")).ToList();
        if (uses.Count > 0) return EditResult.Fail($"쓰는 줄 {uses.Count} · {uses[0]}");
        if (RawUse(site, name) is { } at) return EditResult.Fail($"N-6 원문에서 씀 · {at}");
        var sp = new Splice(site);
        foreach (var p in own) sp.Delete(p);
        return sp.Commit();
    }

    // ---- 별칭·벨트 ----

    static readonly HashSet<string> AliasFiles = new(["io_map.h", "custom_io.h", "tag_table.c", "act_ids.h"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^BELT\w+_(CW|CCW)$")]
    private static partial Regex BeltNameRx();

    /// <summary>별칭 이름 바꾸기(<c>#define 이름 값</c>: 입출력 별칭, 숫자 별칭): 정의와 모든 사용처.</summary>
    public static EditResult RenameAlias(Site site, string oldName, string newName)
    {
        var defs = site.Names.Get(oldName);
        if (defs.Count == 0 || defs.Any(d => d.Weak || d.Def is not Statement { Shape: StatementShape.Define } || !AliasFiles.Contains(d.File!.Name)
                || d.Kind is not (NameKind.Input or NameKind.InputHigh or NameKind.IoNone or NameKind.Output or NameKind.Alias)))
            return EditResult.Fail($"별칭 아님 · {oldName}");
        if (BeltNameRx().IsMatch(oldName) || BeltNameRx().IsMatch(newName)) return EditResult.Fail("벨트 이름은 벨트 바꾸기로");
        if (NewName(site, newName, null) is { } why) return EditResult.Fail(why);
        if (RawUse(site, oldName) is { } at) return EditResult.Fail($"N-6 원문에서 씀 · {at}");
        return Rename(site, [(oldName, newName)], []);
    }

    /// <summary>벨트 이름 바꾸기: <c>BELT&lt;이름&gt;_CW·_CCW</c> 정의와 벨트 칸(<c>BELT_ON( 1, … )</c>, <c>BELT( LIFT, … )</c> 등)의 사용처.</summary>
    public static EditResult RenameBelt(Site site, string oldName, string newName)
    {
        newName = newName.Trim();
        if (!site.Names.Belts.ContainsKey(oldName)) return EditResult.Fail($"없는 벨트 · {oldName}");
        if (!Regex.IsMatch(newName, @"^[A-Za-z0-9_]+$")) return EditResult.Fail($"이름 틀림 · {newName}");
        if (site.Names.Belts.ContainsKey(newName)) return EditResult.Fail($"N-4 이미 있음 · {newName}");
        string[] olds = [$"BELT{oldName}_CW", $"BELT{oldName}_CCW"], news = [$"BELT{newName}_CW", $"BELT{newName}_CCW"];
        foreach (var n in news) if (NewName(site, n, null) is { } why) return EditResult.Fail(why);
        if (RawUse(site, olds) is { } at) return EditResult.Fail($"N-6 원문에서 씀 · {at}");
        // 벨트 칸은 숫자일 수 있어 글자로 못 찾는다. 원문 블록에 벨트 문장이 있으면 막는다
        var beltRx = new Regex(@"\b(BELT_ON|BELT_OFF|WAIT_BELTS|BELT)\s*\(");
        foreach (var f in site.Files)
            if (f.Pieces.FirstOrDefault(p => p.Kind == PieceKind.Raw && p.Zone != null && beltRx.IsMatch(StripComments(p.Text))) is { } raw)
                return EditResult.Fail($"N-6 원문에서 씀 · {f.Name}:{raw.StartLine}");
        var uses = new Facts(site).Uses.Where(u => u.Spec.Kind == ArgKind.Belt && ReferenceEquals(u.Node, u.Loc.Node) && u.Loc.Text == oldName)
            .DistinctBy(u => (object)u.Loc, ReferenceEqualityComparer.Instance).Select(u => (u.St, u.Loc)).ToList();
        return Rename(site, olds.Zip(news).ToList(), uses.Select(u => (u.St, u.Loc, newName)).ToList());
    }

    // ---- 시퀀스 ----

    /// <summary>사이트 시퀀스 ID 추가: <c>seq_custom.h</c> 의 <c>SEQ_</c> enum 끝.</summary>
    public static EditResult AddSeqId(Site site, string name)
    {
        if (NewName(site, name, "SEQ_") is { } why) return EditResult.Fail(why);
        var f = site.File("seq_custom.h");
        if (f == null) return EditResult.Fail("seq_custom.h 없음");
        Statement? last = null;
        bool hasSeq = false;
        foreach (var p in f.Pieces)
        {
            if (p.Zone == null) { if (hasSeq) break; last = null; continue; }
            if (p is not Statement { Shape: StatementShape.Enumerator } st) continue;
            last = st;
            hasSeq |= st.Name.StartsWith("SEQ_", StringComparison.Ordinal) && !st.Name.EndsWith("_COUNT", StringComparison.Ordinal);
        }
        if (!hasSeq || last == null) return EditResult.Fail("seq_custom.h SEQ_ enum 없음");
        var sp = new Splice(site);
        AppendEnumerator(sp, last, name);
        return sp.Commit();
    }

    /// <summary>라벨 추가: 그 파일 <c>SEQ_LABELS( … )</c> 끝.</summary>
    public static EditResult AddLabel(Site site, string fileName, string label)
    {
        var f = site.File(fileName);
        var st = f?.Statements.FirstOrDefault(s => s.Name == "SEQ_LABELS" && s.Shape == StatementShape.Macro);
        if (st == null) return EditResult.Fail($"SEQ_LABELS 없음 · {fileName}");
        if (!Form.Ident.IsMatch(label)) return EditResult.Fail($"이름 틀림 · {label}");
        if (NameDict.CoreNamesAndWords.Contains(label)) return EditResult.Fail($"N-5 코어 이름 · {label}");
        if (site.Names.Get(label).Any(d => d.Kind != NameKind.Label || d.File == f)) return EditResult.Fail($"N-4 이미 있음 · {label}");
        return SiteEdit.InsertArg(site, st, st.Args.Count, label);
    }

    /// <summary>시퀀스 표 추가: <c>seq_table.c</c> 의 <c>SEQ_MAP_BEGIN</c> 앞에 <c>SEQ_BEGIN( 이름 )</c> … <c>END_STOP</c> … <c>SEQ_END</c>, 대응표 끝에 <c>SEQ_MAP</c> 줄.</summary>
    public static EditResult AddSeqTable(Site site, string name, string profile, string seqId)
    {
        var map = site.ZonesOf("SEQ_MAP_BEGIN").FirstOrDefault(z => z.File.Name.Equals("seq_table.c", StringComparison.OrdinalIgnoreCase));
        if (map == null) return EditResult.Fail("seq_table.c 대응표 없음");
        if (NewName(site, name, null) is { } why) return EditResult.Fail(why);
        if (profile is not ("0" or "ANY_PROFILE") && site.Names.Find(profile) is not { Kind: NameKind.Profile }) return EditResult.Fail($"없는 프로파일 · {profile}");
        if (site.Names.Find(seqId) is not { Kind: NameKind.SeqId }) return EditResult.Fail($"없는 시퀀스 ID · {seqId}");
        var sp = new Splice(site);
        var eol = sp.Eol(map.Begin);
        sp.Insert(map.Begin, true, $"SEQ_BEGIN( {name} ){eol}    END_STOP{eol}SEQ_END{eol}{eol}");
        SiteEdit.InsertInto(sp, map, map.Statements.Count, [new NewStatement("SEQ_MAP", [profile, seqId, name])]);
        return sp.Commit();
    }

    // ---- 같이 쓰는 것 ----

    /// <summary>enum 끝에 열거자 하나. 끝 열거자에 쉼표가 없으면 붙인다(새 줄은 끝 열거자의 쉼표 꼴을 따른다).</summary>
    static void AppendEnumerator(Splice sp, Statement last, string name)
    {
        bool comma = last.Tail.TrimStart().StartsWith(',');
        if (!comma)
        {
            int ts = Form.TailStart(last);
            sp.Replace(last, last.Text[..ts] + "," + last.Text[ts..], mark: false);
        }
        sp.Insert(last, false, last.Indent + name + (comma ? "," : "") + sp.Eol(last));
    }

    /// <summary>새 이름으로 쓸 수 있는가(C 식별자, 머리 글자, 코어 이름·있는 이름과 안 겹침). 안 되면 까닭.</summary>
    static string? NewName(Site site, string name, string? prefix)
    {
        if (!Form.Ident.IsMatch(name)) return $"이름 틀림 · {name}";
        if (prefix != null && !name.StartsWith(prefix, StringComparison.Ordinal)) return $"이름 틀림 · {prefix} 로 시작";
        if (NameDict.CoreNamesAndWords.Contains(name)) return $"N-5 코어 이름 · {name}";
        if (site.Names.Get(name).Count > 0) return $"N-4 이미 있음 · {name}";
        if (RawUse(site, name) is { } at) return $"이미 씀 · {at}";
        return null;
    }

    static Regex Word(params string[] names) =>
        new(@"(?<![A-Za-z0-9_])(" + string.Join("|", names.Select(Regex.Escape)) + @")(?![A-Za-z0-9_])");

    [GeneratedRegex(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline)]
    private static partial Regex CommentRx();

    /// <summary>주석을 지운다(줄 수는 그대로).</summary>
    static string StripComments(string s) => CommentRx().Replace(s, m => new string('\n', m.Value.Count(c => c == '\n')));

    /// <summary>
    /// N-6: 이름이 원문 블록(읽은 파일 안) 또는 읽지 않는 파일(사이트 폴더의 그 밖 <c>.c</c>·<c>.h</c>, 생성물 <c>*_packed.c</c> 제외)에서 쓰이는 곳. 없으면 null.
    /// </summary>
    internal static string? RawUse(Site site, params string[] names)
    {
        var rx = Word(names);
        foreach (var f in site.Files)
            foreach (var p in f.Pieces.Where(p => p.Kind == PieceKind.Raw))
            {
                var t = StripComments(p.Text);
                var m = rx.Match(t);
                if (m.Success) return $"{f.Name}:{p.StartLine + t.AsSpan(0, m.Index).Count('\n')}";
            }
        if (site.Folder.Length > 0 && Directory.Exists(site.Folder))
            foreach (var path in Directory.GetFiles(site.Folder))
            {
                var n = Path.GetFileName(path);
                var ext = Path.GetExtension(n).ToLowerInvariant();
                if (ext is not (".c" or ".h") || FileZones.IsReadTarget(n) || n.EndsWith("_packed.c", StringComparison.OrdinalIgnoreCase)) continue;
                var t = StripComments(File.ReadAllText(path));
                var m = rx.Match(t);
                if (m.Success) return $"{n}:{1 + t.AsSpan(0, m.Index).Count('\n')}";
            }
        return null;
    }

    /// <summary>
    /// 이름 바꾸기: 모든 문장의 인자 안 낱말(식별자 단위)과 define·열거자의 이름. 꼬리 주석·독립 주석은 그대로.
    /// extra = 글자로 못 찾는 칸(벨트 번호 등): (문장, 인자, 새 글자).
    /// </summary>
    static EditResult Rename(Site site, IReadOnlyList<(string Old, string New)> pairs, IReadOnlyList<(Statement St, Arg Arg, string New)> extra)
    {
        var rx = Word(pairs.Select(p => p.Old).ToArray());
        var map = pairs.ToDictionary(p => p.Old, p => p.New, StringComparer.Ordinal);
        var sp = new Splice(site);
        int n = 0;
        foreach (var f in site.Files)
            foreach (var st in f.Statements)
            {
                var spans = new List<(int, int, string)>();
                if (st.Shape != StatementShape.Macro && map.TryGetValue(st.Name, out var nn))
                {
                    int at = st.Shape == StatementShape.Define ? Regex.Match(st.Text, @"^[ \t]*#[ \t]*define[ \t]+").Length : st.Indent.Length;
                    spans.Add((at, st.Name.Length, nn));
                }
                foreach (var a in st.Args)
                    foreach (Match m in rx.Matches(a.Text)) spans.Add((a.Offset + m.Index, m.Length, map[m.Value]));
                foreach (var e in extra.Where(e => ReferenceEquals(e.St, st))) spans.Add((e.Arg.Offset, e.Arg.Text.Length, e.New));
                if (spans.Count == 0) continue;
                if (f.ReadOnly) return EditResult.Fail($"읽기 전용 · {f.Name}");
                sp.Replace(st, Form.Spans(st.Text, spans));
                n++;
            }
        return n == 0 ? EditResult.Fail("바꿀 곳 없음") : sp.Commit();
    }
}
