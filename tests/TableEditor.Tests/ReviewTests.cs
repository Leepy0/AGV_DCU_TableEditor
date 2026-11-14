using System.Text;
using TableEditor.Engine.Check;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

/// <summary>
/// 작업 8(독립 검토)에서 찾은 결함의 재현 시험: 읽기·편집·저장. 이름은 모두 지어낸 것. 디스크를 쓰는 시험은 <c>$env:TEMP</c> 아래에서.
/// </summary>
static class ReviewTests
{
    static readonly UTF8Encoding Utf8 = new(false);
    static Action<bool, string> check = (_, _) => { };
    static string root = "";

    public static void Run(Action<bool, string> c, string repoRoot)
    {
        check = c;
        root = repoRoot;
        SlashComment();
        IfZero();
        UndoAfterSave();
        OutsideUnedited();
        StaleDraft();
        CountMark();
        NotWritable();
        TempPlace();
        SubFolderUse();
        TagPlace();
    }

    // ---- 도움 ----

    static string Copy(string name)
    {
        var top = Path.Combine(Path.GetTempPath(), "tedit_review_tests", name);
        if (Directory.Exists(top))
        {
            foreach (var f in Directory.GetFiles(top, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(top, true);
        }
        var dir = Directory.CreateDirectory(Path.Combine(top, "app", "site", "T900_DEMO")).FullName;
        foreach (var f in Directory.GetFiles(Path.Combine(root, "tests", "fixtures", "T900_DEMO")))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    static SourceFile F(string name, string text) => SourceFile.Parse(name, Utf8.GetBytes(text));

    static bool Same(SourceFile f, string text) => f.ToBytes().AsSpan().SequenceEqual(Utf8.GetBytes(text));

    static Statement St(Site s, string file, string start) =>
        s.File(file)!.Statements.Single(x => x.Text.TrimStart().StartsWith(start, StringComparison.Ordinal));

    static List<string> Tags(SourceFile f) => f.Statements.Where(s => s.Zone != null).Select(s => s.Args[0].Text).ToList();

    static List<string> Dead(Site s) => Checker.Run(s).Where(f => f.Rule is "T-10" or "T-11").Select(f => f.ToString()).ToList();

    const string Begin = "TAG_TABLE_BEGIN( site_tags )\n";
    const string End = "TAG_TABLE_END\n";

    static string Tag(int n) => $"    TAG( {n}, ANY_PROFILE, AGV_STOP )";

    // ---- 7: // 주석이 \ 로 끝나면 C 는 다음 줄까지 주석 ----

    static void SlashComment()
    {
        var t = Begin + Tag(1) + " // 메모 \\\n" + Tag(2) + "\n" + Tag(3) + "\n" + End;
        var f = F("tag_table.c", t);
        check(Same(f, t), "7 꼬리 // \\: 왕복");
        check(Tags(f).SequenceEqual(["3"]), "7 꼬리 // \\: 이어진 줄은 규칙이 아니다 · " + string.Join(",", Tags(f)));
        var raw = f.RawInZones.ToList();
        check(raw.Count == 1 && raw[0].StartLine == 2 && raw[0].EndLine == 3, "7 꼬리 // \\: 두 줄이 원문 한 조각");

        var t2 = Begin + "    // 메모 \\\r\n" + Tag(2) + "\r\n" + Tag(3) + "\r\n" + End;
        var f2 = F("tag_table.c", t2);
        check(Same(f2, t2) && Tags(f2).SequenceEqual(["3"]) && f2.RawInZones.Count() == 1, "7 독립 // \\(CRLF): 이어진 줄은 원문");

        var t3 = Begin + Tag(1) + " // 메모 \\ \t\n" + Tag(2) + "\n" + End;
        check(Tags(F("tag_table.c", t3)).Count == 0 && Same(F("tag_table.c", t3), t3), "7 \\ 뒤 공백: 모름 → 원문");

        var t4 = Begin + Tag(1) + " // 경로 C:\\tmp\n" + Tag(2) + " /* a\\ */\n" + End;
        check(Tags(F("tag_table.c", t4)).SequenceEqual(["1", "2"]), "7 가운데 \\ 와 블록 주석 안 \\ 는 그대로 문장");

        var io = "#define A IN_EXT_1 // x \\\n#define B IN_EXT_2\n#define C IN_EXT_3\n";
        check(F("io_map.h", io).Statements.Select(s => s.Name).SequenceEqual(["C"]) && Same(F("io_map.h", io), io), "7 define 꼬리 // \\");

        var s = Site.FromFiles([F("tag_table.c", Begin + Tag(1) + " // 메모\n" + Tag(2) + "    /* 둘 */\n" + End)]);
        var r1 = SiteEdit.SetComment(s, s.TagRules[0].St, "폴더 D:\\표\\");
        check(!r1.Ok && r1.Error!.Contains("주석 글"), "7 새 주석 글 끝 \\ 막음(// 꼬리) · " + r1.Error);
        var r2 = SiteEdit.SetComment(s, s.TagRules[1].St, "끝 \\");
        check(!r2.Ok && r2.Error!.Contains("주석 글"), "7 새 주석 글 끝 \\ 막음(블록 꼬리) · " + r2.Error);
        var zone = s.ZonesOf("TAG_TABLE_BEGIN").Single();
        var r3 = SiteEdit.Insert(s, zone, 0, [new NewStatement("TAG", ["5", "ANY_PROFILE", "AGV_STOP"], "x\\")]);
        check(!r3.Ok && r3.Error!.Contains("주석 글"), "7 새 문장의 주석 글 끝 \\ 막음 · " + r3.Error);
        check(SiteEdit.SetComment(s, s.TagRules[0].St, "D:\\표 폴더").Ok, "7 가운데 \\ 는 된다");
    }

    // ---- 20: 구역 안 #if 0 … #endif ----

    static void IfZero()
    {
        var t = Begin + Tag(1) + "\n#if 0\n" + Tag(7) + "\n#if X\n" + Tag(8) + "\n#endif\n#else\n" + Tag(6) + "\n#endif\n" + Tag(9) + "\n" + End;
        var f = F("tag_table.c", t);
        check(Same(f, t) && !f.ReadOnly, "20 #if 0: 왕복");
        check(Tags(f).SequenceEqual(["1", "9"]), "20 #if 0 … #endif 사이는 규칙이 아니다 · " + string.Join(",", Tags(f)));
        var raw = f.RawInZones.ToList();
        check(raw.Count == 1 && raw[0].StartLine == 3 && raw[0].EndLine == 10, "20 #if 0 덩이가 원문 한 조각");
        var s = Site.FromFiles([f]);
        var plan = TagEdit.PlanAdd(s, "7", ["ANY_PROFILE"], null, TagRunMode.PerLine);
        check(plan.Error == null && !plan.Rows[0].Exists && plan.Lines.Count == 1, "20 죽은 덩이 안 태그는 '있음'이 아니다");

        var t1 = Begin + "#  if  0   /* 끔 */\r\n" + Tag(7) + "\r\n# endif\r\n" + Tag(9) + "\r\n" + End;
        check(Tags(F("tag_table.c", t1)).SequenceEqual(["9"]) && Same(F("tag_table.c", t1), t1), "20 # if 0 공백·주석 꼴");
        var t2 = Begin + "#if 1\n" + Tag(7) + "\n#endif\n#if 01\n" + Tag(8) + "\n#endif\n" + End;
        check(Tags(F("tag_table.c", t2)).SequenceEqual(["7", "8"]), "20 #if 1·#if 01 은 그대로 읽는다");
        var t3 = Begin + "#if 0\n" + Tag(7) + "\n" + End;
        var f3 = F("tag_table.c", t3);
        check(f3.ReadOnly && f3.ReadOnlyReason!.StartsWith("R-2", StringComparison.Ordinal) && Same(f3, t3), "20 #endif 없는 #if 0: R-2 읽기 전용");
        var e = "enum {\n    PF_A = 1,\n#if 0\n    PF_OLD,\n#endif\n    PF_B\n};\n";
        check(F("site.h", e).Statements.Select(x => x.Name).SequenceEqual(["PF_A", "PF_B"]) && Same(F("site.h", e), e), "20 enum 안 #if 0");
    }

    // ---- 2: 저장 뒤 되돌리기 ----

    static void UndoAfterSave()
    {
        var dir = Copy("undo");
        var path = Path.Combine(dir, "tag_table.c");
        var original = File.ReadAllBytes(path);
        var s0 = Site.Open(dir);
        var h = new EditHistory(s0);
        var st = St(s0, "tag_table.c", "TAG ( 2,");
        h.Push(SiteEdit.SetArg(s0, st, st.Args[0], "4"));
        var sv = SaveFiles.Save(h.Current);
        check(sv.Status == SaveStatus.Saved, "2 첫 저장");
        h.MarkSaved(sv.Site);
        h.Undo();
        check(h.Dirty, "2 되돌린 뒤 Dirty");
        var sv2 = SaveFiles.Save(h.Current);
        check(sv2.Status == SaveStatus.Saved && sv2.Written.SequenceEqual(["tag_table.c"]), $"2 되돌린 것을 저장 · {sv2.Status} {sv2.Reason}");
        check(File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "2 디스크 = 되돌린 표");
        if (sv2.Status == SaveStatus.Saved) h.MarkSaved(sv2.Site);

        // 다시 하기 쪽도 같은 기록을 본다
        h.Redo();
        var sv3 = SaveFiles.Save(h.Current);
        check(sv3.Status == SaveStatus.Saved && File.ReadAllText(path, Utf8).Contains("TAG ( 4,"), $"2 다시 한 것을 저장 · {sv3.Status} {sv3.Reason}");
        if (sv3.Status == SaveStatus.Saved) h.MarkSaved(sv3.Site);

        // 저장 → 되돌리기 → 다른 줄 고치기: 거짓 충돌 없음
        h.Undo();
        var cur = h.Current;
        var st3 = St(cur, "tag_table.c", "TAG ( 3,");
        h.Push(SiteEdit.SetArg(cur, st3, st3.Args[0], "7"));
        var sv4 = SaveFiles.Save(h.Current);
        check(sv4.Status == SaveStatus.Saved, $"2 되돌리고 다른 줄 고쳐 저장: 충돌 아님 · {sv4.Status} {string.Join(",", sv4.Conflicts)}");
        var text = File.ReadAllText(path, Utf8);
        check(text.Contains("TAG ( 2,") && text.Contains("TAG ( 7,") && !text.Contains("TAG ( 4,"), "2 디스크 = 지금 화면의 표");
    }

    // ---- 8·19: 내가 안 고친 파일이 바깥에서 바뀜 ----

    static void OutsideUnedited()
    {
        var dir = Copy("outside2");
        var s = Site.Open(dir);
        var e = SiteEdit.SetComment(s, St(s, "tag_table.c", "TAG ( 2,"), "내 것").Site!;
        var siteH = Path.Combine(dir, "site.h");
        var tagBefore = File.ReadAllBytes(Path.Combine(dir, "tag_table.c"));
        File.AppendAllText(siteH, "/* 남의 것 */\n", Utf8);
        var theirs = File.ReadAllBytes(siteH);
        var r = SaveFiles.Save(e);
        check(r.Status == SaveStatus.Conflict && r.Conflicts.SequenceEqual(["site.h"]), $"8 안 고친 파일의 바깥 변경을 알림 · {r.Status} {string.Join(",", r.Conflicts)}");
        check(File.ReadAllBytes(Path.Combine(dir, "tag_table.c")).AsSpan().SequenceEqual(tagBefore), "8 알릴 때 아무것도 안 씀");
        var o = SaveFiles.Save(e, overwrite: true);
        check(o.Status == SaveStatus.Saved && o.Written.SequenceEqual(["tag_table.c"]) && File.ReadAllBytes(siteH).AsSpan().SequenceEqual(theirs),
            $"8 내 것으로 덮기: 내가 고친 파일만 쓴다 · {o.Status} {o.Reason}");

        // 바깥 변경 때문에 쓴 뒤의 표가 검사 오류면 R-3 으로 되돌린다
        var dir2 = Copy("outside3");
        var s2 = Site.Open(dir2);
        var e2 = SiteEdit.SetComment(s2, St(s2, "tag_table.c", "TAG ( 2,"), "내 것").Site!;
        var h2 = Path.Combine(dir2, "site.h");
        File.WriteAllText(h2, File.ReadAllText(h2, Utf8).Replace("    PF_CART,\n    PF_BOX\n", "    PF_CART\n"), Utf8);
        var before = File.ReadAllBytes(Path.Combine(dir2, "tag_table.c"));
        var bad = SaveFiles.Save(e2, overwrite: true);
        check(bad.Status == SaveStatus.Failed && bad.Reason!.StartsWith("R-3", StringComparison.Ordinal), $"19 쓴 뒤 검사 오류가 생기면 R-3 · {bad.Status} {bad.Reason}");
        check(File.ReadAllBytes(Path.Combine(dir2, "tag_table.c")).AsSpan().SequenceEqual(before), "19 R-3: 쓴 파일 복구");
    }

    // ---- 9: 낡은 초안 ----

    static void StaleDraft()
    {
        var dir = Copy("stale");
        var s = Site.Open(dir);
        var st = St(s, "tag_table.c", "TAG ( 2,");
        var e = SiteEdit.SetArg(s, st, st.Args[0], "4").Site!;
        Drafts.Write(e);
        var path = Path.Combine(dir, "tag_table.c");
        File.WriteAllText(path, File.ReadAllText(path, Utf8).Replace("AGV_SPEED_MMIN( 20 )", "AGV_SPEED_MMIN( 99 )"), Utf8);
        var d = Drafts.Read(Site.Open(dir));
        check(d != null && d.Stale.SequenceEqual(["tag_table.c"]), "9 초안 낡음");
        if (d == null) return;
        var r = SaveFiles.Save(d.Site);
        check(r.Status == SaveStatus.Conflict && r.Conflicts.SequenceEqual(["tag_table.c"]), $"9 낡은 초안 저장 = 충돌 · {r.Status}");
        check(File.ReadAllText(path, Utf8).Contains("AGV_SPEED_MMIN( 99 )"), "9 바깥 변경이 남아 있다");
        var o = SaveFiles.Save(d.Site, overwrite: true);
        check(o.Status == SaveStatus.Saved && File.ReadAllText(path, Utf8).Contains("TAG ( 4,"), $"9 내 것으로 덮기는 된다 · {o.Status} {o.Reason}");

        // 낡지 않은 초안은 그대로 저장된다
        var dir2 = Copy("fresh");
        var s2 = Site.Open(dir2);
        var st2 = St(s2, "tag_table.c", "TAG ( 2,");
        Drafts.Write(SiteEdit.SetArg(s2, st2, st2.Args[0], "4").Site!);
        var d2 = Drafts.Read(Site.Open(dir2))!;
        check(d2.Stale.Count == 0 && SaveFiles.Save(d2.Site).Status == SaveStatus.Saved, "9 낡지 않은 초안 저장");
    }

    // ---- 3: 개수 표지 열거자 ----

    static void CountMark()
    {
        const string Prof = "PROFILE_TABLE_BEGIN( site_profiles )\n    PROFILE( PF_A, 0, 1, 9 )\n    PROFILE( PF_B, 0, 10, 19 )\nPROFILE_TABLE_END\n";
        foreach (var mark in new[] { "PF_COUNT_", "PF_COUNT", "DEMO_PF_COUNT" })
        {
            var s = Site.FromFiles([F("site.h", $"enum {{\n    PF_A = 1,\n    PF_B,   /* b */\n    {mark}\n}};\n"), F("profiles.c", Prof)]);
            check(s.Profiles.Select(p => p.Name).SequenceEqual(["PF_A", "PF_B"]), $"3 {mark} 는 프로파일이 아니다 · " + string.Join(",", s.Profiles.Select(p => p.Name)));
            check(s.Names.Find(mark) is { Kind: NameKind.Constant, Value: 3 }, $"3 {mark} = 상수 3");
            var r = BundleEdit.AddProfile(s, "PF_NEW", 0, 20, 29);
            check(r.Ok && r.Site!.File("site.h")!.ToText() == $"enum {{\n    PF_A = 1,\n    PF_B,   /* b */\n    PF_NEW,\n    {mark}\n}};\n",
                $"3 {mark} 앞에 넣는다 · {r.Error} · " + r.Site?.File("site.h")!.ToText().Replace("\n", "|"));
            check(r.Ok && r.Site!.Names.Find("PF_NEW")?.Value == 3 && r.Site!.Names.Find(mark)?.Value == 4, $"3 {mark}: 새 프로파일 3, 표지 4");
            check(!BundleEdit.DeleteProfile(s, mark).Ok && !BundleEdit.RenameProfile(s, mark, "PF_X").Ok, $"3 {mark} 는 지우거나 이름을 못 바꾼다");
        }
        // 값이 있거나 끝이 아니면 표지가 아니다
        var v = Site.FromFiles([F("site.h", "enum {\n    PF_A = 1,\n    PF_COUNT = 9\n};\n"), F("profiles.c", Prof)]);
        var rv = BundleEdit.AddProfile(v, "PF_NEW", 0, 20, 29);
        check(rv.Ok && rv.Site!.File("site.h")!.ToText() == "enum {\n    PF_A = 1,\n    PF_COUNT = 9,\n    PF_NEW\n};\n", "3 값 있는 끝 열거자는 표지가 아니다 · " + rv.Error);

        var q = Site.FromFiles([F("seq_custom.h", "enum {\n    SEQ_DEMO_A = SEQ_SITE_BASE,\n    SEQ_DEMO_COUNT_\n};\n\nenum {\n    CALL_DEMO_COUNT = 0\n};\n")]);
        check(q.Names.Of(NameKind.SeqId).Where(d => !d.Core).Select(d => d.Name).SequenceEqual(["SEQ_DEMO_A"]), "3 SEQ_…_COUNT_ 는 시퀀스 ID 가 아니다");
        var rq = BundleEdit.AddSeqId(q, "SEQ_DEMO_B");
        check(rq.Ok && rq.Site!.File("seq_custom.h")!.ToText().StartsWith("enum {\n    SEQ_DEMO_A = SEQ_SITE_BASE,\n    SEQ_DEMO_B,\n    SEQ_DEMO_COUNT_\n};\n", StringComparison.Ordinal),
            "3 시퀀스 ID 도 표지 앞 · " + rq.Error + " · " + rq.Site?.File("seq_custom.h")!.ToText().Replace("\n", "|"));
    }

    // ---- 12: 쓰기 X 파일 ----

    static void NotWritable()
    {
        var dir = Copy("writable");
        var s = Site.Open(dir);
        var f = s.File("tag_custom.h")!;
        var r = SiteEdit.SetComment(s, f.Statements.First(), "x");
        check(!r.Ok && r.Error!.Contains("쓰기 금지") && r.Error.Contains("tag_custom.h"), "12 tag_custom.h 문장 고치기 막음 · " + r.Error);
        var raw = SiteEdit.ReplaceText(s, f.Pieces.First(p => p.Kind == PieceKind.Raw), "#include \"x.h\"");
        check(!raw.Ok && raw.Error!.Contains("쓰기 금지"), "12 tag_custom.h 원문 고치기 막음 · " + raw.Error);
        check(SiteEdit.SetComment(s, s.File("site.h")!.Statements.First(), "x").Ok, "12 쓰기 O 파일은 된다");
    }

    // ---- 23: 임시 파일 자리 ----

    static void TempPlace()
    {
        var dir = Copy("temp");
        var app = Path.GetDirectoryName(Path.GetDirectoryName(dir))!;
        var tmp = Path.Combine(app, "build", "table_editor", "tmp");
        var s = Site.Open(dir);
        var a = SiteEdit.SetComment(s, St(s, "tag_table.c", "TAG ( 2,"), "a").Site!;
        var b = SiteEdit.SetComment(a, St(a, "manual_table.c", "MAN_OBS_B"), "b").Site!;
        int inSite = -1, inTmp = -1, n = 0;
        SaveFiles.AfterSwap = _ =>
        {
            if (++n != 1) return;
            inSite = Directory.GetFiles(dir).Count(p => p.EndsWith(".tedit_tmp", StringComparison.Ordinal));
            inTmp = Directory.Exists(tmp) ? Directory.GetFiles(tmp).Length : 0;
        };
        SaveResult r;
        try { r = SaveFiles.Save(b); }
        finally { SaveFiles.AfterSwap = null; }
        check(r.Status == SaveStatus.Saved && n == 2, $"23 두 파일 저장 · {r.Status} {r.Reason}");
        check(inSite == 0 && inTmp == 1, $"23 임시 파일은 사이트 폴더 밖(build/table_editor/tmp) · 사이트 {inSite}, tmp {inTmp}");
        check(!Directory.Exists(tmp) || Directory.GetFiles(tmp).Length == 0, "23 저장 뒤 임시 파일 없음");

        // 저장소 없이 사이트 폴더만: 부모 폴더
        var loose = Path.Combine(Path.GetTempPath(), "tedit_review_tests", "loose", "T900_DEMO");
        if (Directory.Exists(loose)) Directory.Delete(loose, true);
        Directory.CreateDirectory(loose);
        foreach (var f in Directory.GetFiles(dir)) File.Copy(f, Path.Combine(loose, Path.GetFileName(f)));
        var ls = Site.Open(loose);
        var le = SiteEdit.SetComment(ls, St(ls, "tag_table.c", "TAG ( 3,"), "c").Site!;
        var seen = new List<string>();
        SaveFiles.AfterSwap = _ => seen.AddRange(Directory.GetFiles(loose, "*.tedit_tmp"));
        try { r = SaveFiles.Save(le); }
        finally { SaveFiles.AfterSwap = null; }
        check(r.Status == SaveStatus.Saved && seen.Count == 0 && !Directory.GetFiles(Path.GetDirectoryName(loose)!, "*.tedit_tmp").Any(), $"23 사이트 폴더만: 저장, 찌꺼기 없음 · {r.Status} {r.Reason}");
    }

    // ---- 18: 하위 폴더의 사용처 ----

    static void SubFolderUse()
    {
        var dir = Copy("sub");
        Directory.CreateDirectory(Path.Combine(dir, "sub", "deep"));
        File.WriteAllText(Path.Combine(dir, "sub", "deep", "x.c"), "/* PF_CART */\nint x = PF_BOX;\n", Utf8);
        var s = Site.Open(dir);
        var r = BundleEdit.RenameProfile(s, "PF_BOX", "PF_CRATE");
        check(!r.Ok && r.Error!.Contains("N-6") && r.Error.Contains("sub/deep/x.c:2"), "18 하위 폴더에서 쓰면 N-6 · " + r.Error);
        check(BundleEdit.RenameProfile(s, "PF_CART", "PF_WAGON").Ok, "18 하위 폴더 주석 안 이름은 막지 않음");
    }

    // ---- 11: 태그 넣는 자리가 죽은 줄을 만들지 않는다 ----

    static void TagPlace()
    {
        var siteH = F("site.h", "enum {\n    PF_A = 1,\n    PF_B\n};\n");
        var prof = F("profiles.c", "PROFILE_TABLE_BEGIN( site_profiles )\n    PROFILE( PF_A, 0, 1, 9 )\n    PROFILE( PF_B, 0, 10, 19 )\nPROFILE_TABLE_END\n");
        Site Make(string body) => Site.FromFiles([siteH, prof, F("tag_table.c", Begin + body + End)]);
        List<string> Order(Site s) => s.TagRules.Select(r => r.St.Name + " " + string.Join(",", r.St.Args.Take(r.ActionStart).Select(a => a.Text))).ToList();

        // 가: ANY 범위 규칙이 덮는 태그의 프로파일 줄 → 범위 줄 앞
        var a = Make("    TAG( 90, ANY_PROFILE, AGV_STOP )\n    TAG_RANGE( 100, 110, ANY_PROFILE, AGV_STOP )\n    TAG( 200, PF_A, AGV_STOP )\n");
        var ra = TagEdit.Add(a, "105", ["PF_A"], null, TagRunMode.PerLine);
        check(ra.Ok && Dead(ra.Site!).Count == 0, "11 가: 죽은 줄 없음 · " + ra.Error + string.Join(" | ", ra.Ok ? Dead(ra.Site!) : []));
        check(ra.Ok && Order(ra.Site!).SequenceEqual(["TAG 90,ANY_PROFILE", "TAG 105,PF_A", "TAG_RANGE 100,110,ANY_PROFILE", "TAG 200,PF_A"]), "11 가: 범위 줄 앞 · " + string.Join(" / ", ra.Ok ? Order(ra.Site!) : []));

        // 나: 새 ANY 범위가 있는 프로파일 줄을 덮는다 → 그 줄들 뒤. 같이 넣는 프로파일 줄은 새 범위 앞
        var b = Make("    TAG( 30, ANY_PROFILE, AGV_STOP )\n    TAG( 40, PF_A, AGV_STOP )\n    TAG( 41, PF_B, AGV_STOP )\n    TAG( 50, ANY_PROFILE, AGV_STOP )\n");
        var rb = TagEdit.Add(b, "39-42", ["PF_A", "ANY_PROFILE"], null, TagRunMode.Range);
        check(rb.Ok && Dead(rb.Site!).Count == 0, "11 나: 죽은 줄 없음 · " + rb.Error + string.Join(" | ", rb.Ok ? Dead(rb.Site!) : []));
        if (rb.Ok)
        {
            var o = Order(rb.Site!);
            int any = o.IndexOf("TAG_RANGE 39,42,ANY_PROFILE");
            check(any > o.IndexOf("TAG 41,PF_B") && any > o.IndexOf("TAG 40,PF_A") && any > o.IndexOf("TAG 39,PF_A") && any > o.IndexOf("TAG_RANGE 41,42,PF_A") && any < o.IndexOf("TAG 50,ANY_PROFILE"),
                "11 나: 새 ANY 범위는 덮이는 줄 뒤 · " + string.Join(" / ", o));
        }

        // 다: 조건 없는 줄이 있는 태그·프로파일에 조건 줄 → 조건 없는 줄 앞
        var c = Make("    TAG( 200, PF_A, AGV_STOP )\n    TAG( 200, ANY_PROFILE, AGV_STOP )\n    TAG_IF( 300, PF_A, IF_FORWARD, AGV_STOP )\n");
        var rc = TagEdit.Add(c, "200", ["PF_A"], c.TagRules[2], TagRunMode.PerLine);
        check(rc.Ok && Dead(rc.Site!).Count == 0, "11 다: 죽은 줄 없음 · " + rc.Error + string.Join(" | ", rc.Ok ? Dead(rc.Site!) : []));
        check(rc.Ok && Order(rc.Site!).Take(2).SequenceEqual(["TAG_IF 200,PF_A,IF_FORWARD", "TAG 200,PF_A"]), "11 다: 조건 줄이 조건 없는 줄 앞 · " + string.Join(" / ", rc.Ok ? Order(rc.Site!) : []));

        // 라: IF_ID 범위가 포함되는 줄(T-11) → 넓은 범위 줄 앞
        var d = Make("    TAG_IF( 300, PF_A, IF_ID( 1, 9 ), AGV_STOP )\n    TAG_IF( 400, PF_A, IF_ID( 5, 6 ), AGV_STOP )\n");
        var rd = TagEdit.Add(d, "300", ["PF_A"], d.TagRules[1], TagRunMode.PerLine);
        check(rd.Ok && Dead(rd.Site!).Count == 0, "11 라: 죽은 줄 없음 · " + rd.Error + string.Join(" | ", rd.Ok ? Dead(rd.Site!) : []));

        // 마: 번호 바꾸기도 같은 자리 규칙
        var m = Make("    TAG( 90, PF_A, AGV_STOP )\n    TAG_RANGE( 100, 110, ANY_PROFILE, AGV_STOP )\n");
        var rm = TagEdit.Renumber(m, [m.TagRules[0]], 105);
        check(rm.Ok && Dead(rm.Site!).Count == 0 && Order(rm.Site!).SequenceEqual(["TAG 105,PF_A", "TAG_RANGE 100,110,ANY_PROFILE"]), "11 마: 번호 바꾸기 뒤 죽은 줄 없음 · " + rm.Error);

        // 바: fixture 에 범위 추가
        var s = Site.Open(Copy("place"));
        check(Dead(s).Count == 0, "11 바: fixture 는 죽은 줄 0");
        var rs = TagEdit.Add(s, "39-42, 1", ["PF_BELT", "ANY_PROFILE"], null, TagRunMode.Range);
        check(rs.Ok && Dead(rs.Site!).Count == 0, "11 바: fixture 범위 추가 뒤 죽은 줄 없음 · " + rs.Error + string.Join(" | ", rs.Ok ? Dead(rs.Site!).Take(3) : []));
    }
}
