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
}
