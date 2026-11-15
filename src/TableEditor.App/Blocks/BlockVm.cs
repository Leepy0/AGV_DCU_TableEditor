using System.Windows.Input;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Text;

namespace TableEditor.App.Blocks;

/// <summary>블록·칩 안의 값 하나. Label = 앞 작은 라벨, Small = 뒤 작은 글(초, 단위).</summary>
public sealed record Part(string? Label, string Value, string? Small)
{
    public override string ToString() => string.Join(" ", new[] { Label, Value, Small }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>
/// 선택 줄 칸 하나의 바탕: 라벨, 인자(고칠 자리), 문법 칸. 꼴 칸(<see cref="TableEditor.Engine.Model.ActionForms"/>)은
/// Value = 지금 꼴 낱말, Write = 고른 낱말 → 인자 전체 새 글자.
/// </summary>
public sealed record FieldInfo(string Label, Arg Arg, ArgSpec Spec)
{
    public string? Value { get; init; }
    public Func<string, string>? Write { get; init; }
}

/// <summary>조건 칩: 머리 + 값들. Text = 값을 한 줄로(칸이 좁으면 말줄임), Full = 머리 + 값(툴팁).</summary>
public sealed record Chip(string Head, IReadOnlyList<Part> Parts)
{
    public string Text => string.Join(" ", Parts.Select(p => p.Label == null ? p.Value : $"{p.Label} {p.Value}"));
    public string Full => Text.Length == 0 ? Head : $"{Head} {Text}";
}

/// <summary>
/// 블록 하나(매크로 한 문장, 사양 9.3절). Head = 한글 머리 이름, Cat = 팔레트 분류(띠 색),
/// Raw = 원문 블록(문법 표에 없는 꼴, 첫 줄만 보인다), Mark = 칸에 걸린 검사 결과의 등급(테 색).
/// Index = 줄 안 동작 순번(원문 줄이면 -1).
/// </summary>
public sealed class BlockVm : Observable
{
    public required string Head { get; init; }
    public required string Cat { get; init; }
    public required IReadOnlyList<Part> Parts { get; init; }
    public required IReadOnlyList<FieldInfo> Fields { get; init; }
    public required int Cells { get; init; }
    public bool Raw { get; init; }
    public Severity? Mark { get; init; }
    public int Index { get; init; } = -1;

    /// <summary>문장 안 이 블록의 인자(C 글자 강조 자리). 원문 줄이면 null.</summary>
    public Arg? Arg { get; init; }

    /// <summary>이 블록이 든 줄(태그 화면의 줄 VM). 보기 사본은 null.</summary>
    public object? Row { get; set; }

    bool picked;
    public bool IsPicked { get => picked; set => Set(ref picked, value); }

    public ICommand? Pick { get; set; }

    /// <summary>화면 읽기 프로그램의 블록 이름: 머리 + 값.</summary>
    public override string ToString() => string.Join(" ", Parts.Select(p => p.ToString()).Prepend(Head).Where(s => s.Length > 0));

    /// <summary>고르기 없는 사본(태그 보기처럼 읽기만 하는 곳).</summary>
    public BlockVm Copy() => new() { Head = Head, Cat = Cat, Parts = Parts, Fields = Fields, Cells = Cells, Raw = Raw, Mark = Mark, Arg = Arg, Index = Index };
}
