using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using TableEditor.App.Blocks;
using TableEditor.App.Mvvm;
using TableEditor.Engine.Grammar;
using TableEditor.Engine.Model;
using TableEditor.Engine.Text;

namespace TableEditor.App.ViewModels;

/// <summary>
/// 선택 줄 칸 하나의 편집기(사양 9.3절 칸 종류): 이름 드롭다운 / 고정 낱말 드롭다운 / 숫자 / 글.
/// 드롭다운은 고르면 바로, 숫자·글은 Enter 나 포커스를 잃을 때 명령이 된다. 범위 밖 숫자는 빨간 테로 남고 확정하지 않는다.
/// </summary>
public sealed class FieldVm : Observable
{
    readonly IFieldHost owner;
    readonly NameDict names;

    public FieldInfo Info { get; }
    public int Index { get; }
    public string Label => Info.Label;
    public FieldEditor Editor { get; }

    /// <summary>읽기 전용 칸(시퀀스의 이름·낱말·라벨 칸): 상자 모양 글만.</summary>
    public bool Locked { get; }
    public bool Mono => Editor is FieldEditor.Number or FieldEditor.Text;
    public bool IsList => !Locked && Editor is FieldEditor.Names or FieldEditor.Words;
    public bool IsBox => !Locked && !IsList;

    /// <summary>읽기 전용 칸의 글. 파라미터는 FND 이름.</summary>
    public string Shown => Info.Spec.Kind == ArgKind.CoreParam && CoreConstants.Params.FirstOrDefault(p => p.Core == text) is { } p ? p.Fnd : text;

    public ICommand CommitCmd { get; }
    public ICommand RevertCmd { get; }

    /// <summary>드롭다운 항목(분류가 둘 이상이면 묶음 머리가 붙는다).</summary>
    public ICollectionView? Items { get; }

    public FieldVm(IFieldHost owner, NameDict names, FieldInfo info, int index, bool locked = false)
    {
        this.owner = owner;
        this.names = names;
        Info = info;
        Index = index;
        Locked = locked;
        Editor = Choices.EditorOf(info.Spec);
        text = Original;
        CommitCmd = new Command(Commit);
        RevertCmd = new Command(Revert);
        if (IsList)
        {
            var list = Choices.For(info.Spec, names).ToList();
            if (!list.Any(c => c.Name == text)) list.Insert(0, new Choice(text, null, list.FirstOrDefault()?.Group ?? ""));
            var view = new ListCollectionView(list);
            if (list.Select(c => c.Group).Distinct().Count() > 1) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Choice.Group)));
            Items = view;
            picked = list.First(c => c.Name == text);
        }
    }

    /// <summary>처음 글: 꼴 칸이면 꼴 낱말, 아니면 인자 글자.</summary>
    string Original => Info.Value ?? BlockBuilder.Collapse(Info.Arg.Text);

    string text;
    /// <summary>칸 글(상자). 바꾸는 동안은 검사만 하고, <see cref="Commit"/> 때 명령이 된다.</summary>
    public string Text
    {
        get => text;
        set { if (Set(ref text, value)) { Raise(nameof(Small)); Raise(nameof(Invalid)); } }
    }

    Choice? picked;
    /// <summary>드롭다운에서 고른 항목. 바뀌면 바로 명령.</summary>
    public Choice? Picked
    {
        get => picked;
        set
        {
            if (value == null || !Set(ref picked, value)) return;
            text = value.Name;
            Commit();
        }
    }

    bool hot;
    /// <summary>강조 테(포커스를 받았거나 방금 고친 칸). C 글자의 같은 자리도 강조한다.</summary>
    public bool IsHot { get => hot; set => Set(ref hot, value); }

    /// <summary>숫자 칸 값: 숫자 글자 또는 값 있는 이름. 모르면 null.</summary>
    long? Value()
    {
        var t = text.Trim();
        return CNum.Value(t) ?? names.Find(t)?.Value;
    }

    /// <summary>
    /// 빨간 테: 칸 글이 인자 하나로 못 쓰는 글(<see cref="SiteEdit.BadArg"/>: 빈 글, 쉼표·괄호로 칸을 넘음, 앞자리 0)이거나,
    /// 숫자 칸이 숫자가 아니거나 범위 밖.
    /// </summary>
    public bool Invalid
    {
        get
        {
            if (IsBox && SiteEdit.BadArg(text) != null) return true;
            if (Editor != FieldEditor.Number) return false;
            if (Value() is not long v) return true;
            var s = Info.Spec;
            return s.Max > s.Min && (v < s.Min || v > s.Max) || s.Unit == "도" && v % 5 != 0 && owner.IsTagArea;
        }
    }

    /// <summary>뒤 작은 글: 각도 °, 틱 옆 초, 단위.</summary>
    public string? Small => Info.Spec.Unit switch
    {
        "도" => "°",
        "틱" => Value() is long v ? (v / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : null,
        "cm" or "초" or "m/min" => Info.Spec.Unit,
        _ => null,
    };

    /// <summary>포커스: 이 칸만 강조.</summary>
    public void Focus() => owner.Hot(this);

    /// <summary>글자가 바뀌었고 틀리지 않았으면 인자 바꾸기 명령.</summary>
    public void Commit()
    {
        if (Locked || Invalid) return;
        var t = text.Trim();
        if (t == Original) return;
        owner.SetField(this, t);
    }

    /// <summary>Esc·확인 팝업 취소: 원래 글로(드롭다운이면 원래 항목으로).</summary>
    public void Revert()
    {
        Text = Original;
        if (IsList && Items?.Cast<Choice>().FirstOrDefault(c => c.Name == Original) is { } c) { picked = c; Raise(nameof(Picked)); }
    }
}
