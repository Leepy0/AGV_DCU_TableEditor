using TableEditor.Engine.Text;

namespace TableEditor.Engine.Model;

/// <summary>
/// 표 구역 하나(여는 문장 ~ 닫는 문장). Body = 그 사이 조각(문장·주석·빈 줄·원문). 조각을 가리킬 뿐 사본이 아니다.
/// </summary>
public sealed class ZoneBlock
{
    public SourceFile File { get; }
    public Statement Begin { get; }
    public Statement? End { get; }
    public IReadOnlyList<Piece> Body { get; }
    public IReadOnlyList<Statement> Statements { get; }

    internal ZoneBlock(SourceFile file, Statement begin, IReadOnlyList<Piece> body, Statement? end)
    {
        File = file;
        Begin = begin;
        Body = body;
        End = end;
        Statements = body.OfType<Statement>().ToList();
    }

    public string Open => Begin.Name;

    /// <summary>여는 문장의 이름 인자(표 이름·배열 이름).</summary>
    public string Name => Begin.Args.Count > 0 ? Begin.Args[0].Text : "";

    /// <summary>구역 안 원문 블록이 있다(R-1: 가림·사슬 검사를 건너뛴다).</summary>
    public bool HasRaw => Body.Any(p => p.Kind == PieceKind.Raw);

    /// <summary>화면 줄 번호 = 구역 안 문장 순번(1부터). 구역 밖이면 0.</summary>
    public int Ordinal(Statement st)
    {
        for (int i = 0; i < Statements.Count; i++) if (ReferenceEquals(Statements[i], st)) return i + 1;
        return 0;
    }
}

/// <summary>프로파일 번호(<c>site.h</c> 열거자 <c>PF_…</c>).</summary>
public sealed record ProfileDef(string Name, long? Number, Statement St);

/// <summary>프로파일 표 줄. Load = 적재 칸 글자(없으면 빈 글자).</summary>
public sealed record ProfileRow(Statement St, string Profile, long? Type, long? First, long? Last)
{
    public string Load => St.Args.Count > 4 ? St.Name + ":" + Squash(St.Args[4].Text) : "";

    internal static string Squash(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    /// <summary>이 줄이 그 타입을 받는가(0 = 타입 무관).</summary>
    public bool TakesType(long type) => Type == 0 || Type == type;
}

/// <summary>태그 규칙(TAG, TAG_IF, TAG_RANGE, TAG_RANGE_IF).</summary>
public sealed record TagRule(Statement St)
{
    public bool IsRange => St.Name.StartsWith("TAG_RANGE", StringComparison.Ordinal);
    public bool HasCond => St.Name.EndsWith("_IF", StringComparison.Ordinal);
    int P => IsRange ? 2 : 1;
    public Arg FirstArg => St.Args[0];
    public Arg LastArg => St.Args[IsRange ? 1 : 0];
    public Arg ProfileArg => St.Args[P];
    public Arg? Cond => HasCond ? St.Args[P + 1] : null;

    /// <summary>첫 동작 인자의 자리(St.Args 안). 동작 넣기·지우기·옮기기의 기준.</summary>
    public int ActionStart => P + (HasCond ? 2 : 1);
    public IReadOnlyList<Arg> Actions => St.Args.Skip(ActionStart).ToList();
    public string Profile => ProfileArg.Text;
    public bool AnyProfile => Profile is "ANY_PROFILE" or "0";

    /// <summary>조건 글자(공백 뺀 것). 없으면 빈 글자.</summary>
    public string CondText => Cond == null ? "" : ProfileRow.Squash(Cond.Text);
}

/// <summary>시퀀스 표(SEQ_BEGIN) 또는 ACT 표(ACT_BEGIN). Steps = 구역 안 문장.</summary>
public sealed record SeqTable(ZoneBlock Zone)
{
    public string Name => Zone.Name;
    public bool IsAct => Zone.Open == "ACT_BEGIN";
    public IReadOnlyList<Statement> Steps => Zone.Statements;
}

/// <summary>대응표 줄(SEQ_MAP·ACT_MAP): 프로파일, ID, 표 이름.</summary>
public sealed record MapRow(Statement St, ZoneBlock Zone)
{
    public bool IsAct => St.Name == "ACT_MAP";
    public string Profile => St.Args[0].Text;
    public bool AnyProfile => Profile is "0" or "ANY_PROFILE";
    public string Id => St.Args[1].Text;
    public string Table => St.Args[2].Text;
}

/// <summary>수동 사슬: MAN 줄 여럿 + MAN_ELSE 한 줄(C 의 if / else if / else 하나).</summary>
public sealed record ManChain(IReadOnlyList<Statement> Rows)
{
    public string Profile => Rows[0].Args[0].Text;
    public string Mode => Rows[0].Args[1].Text;
    public bool Closed => Rows[^1].Name == "MAN_ELSE";
}
