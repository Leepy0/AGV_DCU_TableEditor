using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

/// <summary>화면 손질 시험: 동작 꼴 바꾸기(TAPE), 표 거르기(호기·프로파일), 칸 고치기 반영.</summary>
static class UxTests
{
    static Action<bool, string> check = (_, _) => { };

    public static void Run(Action<bool, string> c, string root)
    {
        check = c;
        var site = Site.Open(Path.Combine(root, "tests", "fixtures", "T900_DEMO"));
        Forms(site);
        Filter(site);
    }

    static Statement Tag(Site s, string start) =>
        s.File("tag_table.c")!.Statements.Single(x => x.Text.TrimStart().StartsWith(start, StringComparison.Ordinal));

    /// <summary>태그 규칙 줄의 동작 하나를 꼴 낱말(바로·cm·TAPE·TAPE_GD) w 로 바꾼 글자.</summary>
    static string Switch(Site s, Arg action, string w)
    {
        var call = (CallNode?)(action.Node as CallNode);
        var name = call?.Name ?? ((NameNode)action.Node).Name;
        var args = call?.Args ?? [];
        var rows = GrammarTable.Find(name, Place.TagAction).Where(r => r.TagCells > 0).ToList();
        var from = Facts.Fit(rows, args)!;
        var forms = ActionForms.Of(from);
        var to = forms.First(e => (ActionForms.Word(e) is { Length: > 0 } x ? x : "바로") == w);
        return ActionForms.Convert(to, from, args, spec => spec.Kind == ArgKind.Word ? spec.Words![0] : "0");
    }

    static void Forms(Site s)
    {
        var seq = GrammarTable.Find("SEQ_START", Place.TagAction).First();
        var stop = GrammarTable.Find("AGV_STOP", Place.TagAction).First();
        check(ActionForms.Of(seq).Select(ActionForms.Word).SequenceEqual(["", "cm", "TAPE", "TAPE_GD"]), "SEQ_START 꼴 넷");
        check(ActionForms.Of(stop).Select(ActionForms.Word).SequenceEqual(["", "cm", "TAPE", "TAPE_GD", "cm 센서"]), "AGV_STOP 꼴 다섯");
        check(ActionForms.Of(GrammarTable.Find("AGV_TURN", Place.TagAction).First()).Count == 1, "AGV_TURN 꼴 하나");

        // TAG( 40, PF_BELT, SEQ_START( LOAD ), … ): 바로 → TAPE → TAPE_GD → cm → 바로, 시퀀스는 그대로
        var st = Tag(s, "TAG( 40, PF_BELT");
        var r = new TagRule(st);
        var a = r.Actions[0];
        check(Switch(s, a, "TAPE") == "SEQ_START( TAPE, LOAD )", "SEQ_START 바로 → TAPE " + Switch(s, a, "TAPE"));
        var e = SiteEdit.SetArg(s, st, a, Switch(s, a, "TAPE"));
        check(e.Ok, "TAPE 쓰기");
        var s2 = e.Site!;
        var st2 = (Statement)e.Touched[0];
        check(st2.Text.Contains("SEQ_START( TAPE, LOAD ), AGV_FORBACK( TOGGLE )", StringComparison.Ordinal), "그 인자만: " + st2.Text.Trim());
        check(Checker.Run(s2).All(f => f.Severity != Severity.Error), "TAPE 오류 0");
        var a2 = new TagRule(st2).Actions[0];
        check(Switch(s2, a2, "TAPE_GD") == "SEQ_START( TAPE_GD, LOAD )", "TAPE → TAPE_GD");
        check(Switch(s2, a2, "cm") == "SEQ_START( 0, LOAD )", "TAPE → cm " + Switch(s2, a2, "cm"));
        check(Switch(s2, a2, "바로") == "SEQ_START( LOAD )", "TAPE → 바로");

        // TAG( 41, PF_BELT, SEQ_START( 20, UNLOAD ) ): cm → TAPE 는 거리를 버린다
        var a3 = new TagRule(Tag(s, "TAG( 41, PF_BELT")).Actions[0];
        check(Switch(s, a3, "TAPE") == "SEQ_START( TAPE, UNLOAD )", "cm → TAPE");

        // AGV_STOP: 바로 → TAPE, cm(30) → 센서 꼴은 거리를 옮긴다
        var stopBare = new TagRule(Tag(s, "TAG ( 1,")).Actions[0];
        check(Switch(s, stopBare, "TAPE") == "AGV_STOP( TAPE )", "AGV_STOP 바로 → TAPE " + Switch(s, stopBare, "TAPE"));
        var stop30 = new TagRule(Tag(s, "TAG ( 6,")).Actions[0];
        check(Switch(s, stop30, "TAPE_GD") == "AGV_STOP( TAPE_GD )", "AGV_STOP cm → TAPE_GD");
        check(Switch(s, stop30, "cm 센서").StartsWith("AGV_STOP( 30, ", StringComparison.Ordinal), "AGV_STOP cm → 센서 거리 옮김 " + Switch(s, stop30, "cm 센서"));
        var fl = new TagRule(Tag(s, "TAG( 40, PF_CART")).Actions[0];
        check(Switch(s, fl, "cm") == "AGV_STOP( 0 )", "센서 FL → cm 은 FL 안 옮김 " + Switch(s, fl, "cm"));
        check(Switch(s, new TagRule(Tag(s, "TAG ( 8,")).Actions[0], "바로") == "AGV_STOP", "TAPE → 바로");
    }

    /// <summary>거르기: 태그 화면(태그 번호)과 시퀀스 화면(호기)이 같은 <see cref="RowFilter.Match"/> 를 쓴다.</summary>
    static void Filter(Site s)
    {
        check(s.ProfilesAt(5).SequenceEqual(["PF_BELT"]) && s.ProfilesAt(25).SequenceEqual(["PF_CART"]) && s.ProfilesAt(50).Count == 0, "호기 → 프로파일");

        // 시퀀스 대응표를 호기·프로파일로(나무와 같은 규칙: 전체 줄은 모든 호기)
        List<string> Seqs(string text, string profile) => s.SeqMap.Where(m =>
        {
            var key = m.AnyProfile ? RowFilter.All : m.Profile;
            return RowFilter.Match(text, profile, key, m.AnyProfile, n => m.AnyProfile || s.ProfilesAt(n).Contains(key));
        }).Select(m => m.Table).ToList();
        check(Seqs("", RowFilter.All).Count == s.SeqMap.Count, "거르기 없음 = 모두");
        check(Seqs("12", RowFilter.All).SequenceEqual(["lift_load", "lift_unload", "shuttle"]), "호기 12 " + string.Join(",", Seqs("12", RowFilter.All)));
        check(Seqs("", "PF_BOX").SequenceEqual(["box_turn_unload", "shuttle"]), "PF_BOX " + string.Join(",", Seqs("", "PF_BOX")));
        check(Seqs("12", "PF_BOX").SequenceEqual(["shuttle"]), "호기 12 + PF_BOX = 전체 줄만");
        check(Seqs("abc", RowFilter.All).Count == 0, "숫자 아님 = 없음");

        // 태그 화면 꼴: 원문 줄(프로파일 없음)은 프로파일을 고르면 빠진다
        check(RowFilter.Match("", "PF_BELT", null, false, _ => true) == false && RowFilter.Match("", RowFilter.All, null, false, _ => true), "원문 줄");
        check(RowFilter.Match(" 40 ", "PF_LIFT", "ANY_PROFILE", true, n => n == 40), "태그 40 + 전체 줄");
    }
}
