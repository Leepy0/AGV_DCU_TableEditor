using System.Security.Cryptography;
using System.Text;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

Console.OutputEncoding = new UTF8Encoding(false);

if (args.Length > 0 && args[0] is "pack" or "build") return await TableEditor.Cli.OpsCommands.Run(args);
if (args.Length > 0 && args[0] == "check") return CheckCommand.Run(args[1..]);
if (args.Length > 0 && args[0] is "round" or "core-update" or "core-versions" or "git-log-check") return await TableEditor.Cli.GitCommands.Run(args);

if (args.Length != 2 || !Directory.Exists(args[1]))
{
    return Help();
}

return args[0] switch
{
    "roundtrip" => Roundtrip(args[1]),
    "stat" => Stat(args[1]),
    "headers" => Headers(args[1]),
    _ => Help(),
};

// 모든 명령을 한 줄씩. 인자 없이·모르는 명령으로 부르면 나온다
static int Help()
{
    Console.Error.WriteLine(string.Join(Environment.NewLine,
    [
        "tedit roundtrip <사이트 폴더>",
        "tedit stat <사이트 폴더>",
        "tedit headers <core 폴더>",
        "tedit check <사이트 폴더> [--core <core 폴더>]",
        "tedit pack <앱 루트> <사이트> [--gcc <경로>]",
        "tedit build <앱 루트> <사이트> [--field] [--name <이미지 이름>] [--gcc <경로>]",
        "tedit round <앱 루트> <사이트> --app-url <주소> [--gcc <경로>]",
        "tedit core-update <앱 루트> <사이트> <vX.Y.Z> --app-url <주소> [--from <주소>] [--gcc <경로>]",
        "tedit core-versions <앱 루트> [--core-url <주소>]",
        "tedit git-log-check <git.log> [--core-url <주소>]",
    ]));
    return 2;
}

// 읽고 그대로 쓴 바이트를 메모리에서 견준다(디스크에 쓰지 않는다)
static int Roundtrip(string folder)
{
    var files = SiteFolder.Read(folder);
    int bad = 0;
    foreach (var f in files)
    {
        var before = Hash(File.ReadAllBytes(Path.Combine(folder, f.Name)));
        var after = Hash(f.ToBytes());
        bool same = before == after;
        if (!same) bad++;
        var note = f.ReadOnly ? $"  읽기 전용({f.ReadOnlyReason})" : "";
        Console.WriteLine($"{(same ? "같음" : "다름")}  {f.Name,-16} {before[..16]}{note}");
    }
    Console.WriteLine($"파일 {files.Count}, 다름 {bad}, 표 구역 안 원문 {files.Sum(f => f.RawInZones.Count())}");
    return bad == 0 && files.Count > 0 ? 0 : 1;
}

static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

static int Stat(string folder)
{
    var files = SiteFolder.Read(folder);
    foreach (var f in files)
    {
        int Count(PieceKind k) => f.Pieces.Count(p => p.Kind == k);
        var note = f.ReadOnly ? $"  읽기 전용({f.ReadOnlyReason})" : "";
        Console.WriteLine($"{f.Name,-16} 문장 {Count(PieceKind.Statement),4}  주석 {Count(PieceKind.Comment),3}  빈 줄 {Count(PieceKind.Blank),3}  원문 {Count(PieceKind.Raw),3}{note}");
    }

    var all = files.SelectMany(f => f.Statements).ToList();
    int N(string name) => all.Count(st => st.Shape == StatementShape.Macro && st.Name == name);
    string Of(params string[] names) => string.Join(", ", names.Select(n => $"{n} {N(n)}"));
    int Sum(params string[] names) => names.Sum(N);

    var profiles = files.Where(f => f.Name == "site.h").SelectMany(f => f.Statements)
        .Count(st => st.Shape == StatementShape.Enumerator && st.Name.StartsWith("PF_", StringComparison.Ordinal));
    string[] pf = ["PROFILE", "PROFILE_LOAD", "PROFILE_LOAD_IF"];
    string[] tag = ["TAG", "TAG_IF", "TAG_RANGE", "TAG_RANGE_IF"];
    string[] man = ["MAN", "MAN_ELSE", "MAN_FB_SW", "MAN_OBS_B"];
    var seqSteps = all.Where(st => st.Zone == "SEQ_BEGIN").ToList();
    var actSteps = all.Where(st => st.Zone == "ACT_BEGIN").ToList();
    var labels = all.Where(st => st.Name == "SEQ_LABELS").Sum(st => st.Args.Count);

    Console.WriteLine();
    Console.WriteLine($"프로파일 {profiles}");
    Console.WriteLine($"프로파일 줄 {Sum(pf)} ({Of(pf)})");
    Console.WriteLine($"LOAD_IF {N("LOAD_IF")}");
    Console.WriteLine($"태그 규칙 {Sum(tag)} ({Of(tag)})");
    Console.WriteLine($"시퀀스 표 {N("SEQ_BEGIN")}, 표 안 문장 {seqSteps.Count} (SELECT_BEGIN·SELECT_END {seqSteps.Count(st => st.Name is "SELECT_BEGIN" or "SELECT_END")})");
    Console.WriteLine($"라벨 선언 {labels}");
    Console.WriteLine($"대응표 {N("SEQ_MAP")}");
    Console.WriteLine($"ACT 표 {N("ACT_BEGIN")}, 표 안 문장 {actSteps.Count}, ACT 대응표 {N("ACT_MAP")}");
    Console.WriteLine($"수동 줄 {Sum(man)} ({Of(man)})");
    Console.WriteLine($"표 구역 안 원문 {files.Sum(f => f.RawInZones.Count())}");
    Console.WriteLine($"읽기 전용 파일 {files.Count(f => f.ReadOnly)}");
    return files.Count > 0 && files.All(f => !f.ReadOnly) ? 0 : 1;
}

static int Headers(string coreFolder)
{
    var r = HeaderCheck.Run(coreFolder);
    Console.WriteLine($"#define 이름 {r.AllDefines}, DSL_ {r.Internal}, 포함 가드 {r.Guards}, 공개 {r.Header.Count}");
    Console.WriteLine($"문법 표 {GrammarTable.Names.Count}, 일치 {r.Header.Count - r.OnlyHeader.Count}");
    foreach (var n in r.OnlyHeader) Console.WriteLine($"헤더에만  {n}");
    foreach (var n in r.OnlyGrammar) Console.WriteLine($"문법 표에만  {n}");
    Console.WriteLine(r.Same ? "같음" : "다름");
    return r.Same ? 0 : 1;
}
