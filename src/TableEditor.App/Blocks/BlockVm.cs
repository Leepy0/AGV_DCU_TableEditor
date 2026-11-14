using System.Windows.Input;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Check;
using TableEditor.Engine.Text;

namespace TableEditor.App.Blocks;

/// <summary>블록·칩 안의 값 하나. Label = 앞 작은 라벨, Small = 뒤 작은 글(초, 단위).</summary>
public sealed record Part(string? Label, string Value, string? Small);

/// <summary>선택 줄 칸의 칸 하나. Mono = 숫자 칸(고정폭).</summary>
public sealed record Field(string Label, string Value, string? Small, bool Mono);

/// <summary>조건 칩: 머리 + 값들. Text = 값을 한 줄로(칸이 좁으면 말줄임), Full = 머리 + 값(툴팁).</summary>
public sealed record Chip(string Head, IReadOnlyList<Part> Parts)
{
    public string Text => string.Join(" ", Parts.Select(p => p.Label == null ? p.Value : $"{p.Label} {p.Value}"));
    public string Full => Text.Length == 0 ? Head : $"{Head} {Text}";
}

/// <summary>
/// 블록 하나(매크로 한 문장, 사양 9.3절). Head = 한글 머리 이름, Cat = 팔레트 분류 키,
/// Raw = 원문 블록(문법 표에 없는 꼴, 첫 줄만 보인다), Mark = 칸에 걸린 검사 결과의 등급(테 색).
/// </summary>
public sealed class BlockVm : Observable
{
    public required string Head { get; init; }
    public required string Cat { get; init; }
    public required IReadOnlyList<Part> Parts { get; init; }
    public required IReadOnlyList<Field> Fields { get; init; }
    public required int Cells { get; init; }
    public bool Raw { get; init; }
    public Severity? Mark { get; init; }

    /// <summary>문장 안 이 블록의 인자(C 글자 강조 자리). 원문 줄이면 null.</summary>
    public Arg? Arg { get; init; }

    bool picked;
    public bool IsPicked { get => picked; set => Set(ref picked, value); }

    public ICommand? Pick { get; set; }

    /// <summary>고르기 없는 사본(태그 보기처럼 읽기만 하는 곳).</summary>
    public BlockVm Copy() => new() { Head = Head, Cat = Cat, Parts = Parts, Fields = Fields, Cells = Cells, Raw = Raw, Mark = Mark, Arg = Arg };
}
