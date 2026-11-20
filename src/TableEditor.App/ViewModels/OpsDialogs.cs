using System.Windows.Input;
using TableEditor.App.Mvvm;

namespace TableEditor.App.ViewModels;

/// <summary>[저장] 사유 한 줄. 빈 글이면 저장 단추가 눌리지 않는다. Enter = 저장.</summary>
public sealed class ReasonVm : DialogVm
{
    public override string Title => "저장";
    string t = "";
    public string Text { get => t; set => Set(ref t, value); }
    public ICommand Ok { get; }
    public ReasonVm() => Ok = new Command(() => Close("ok"), () => Text.Trim().Length > 0);
}

/// <summary>작성자(git config user.name·user.email). 이름은 설정의 이름과 같이 쓴다.</summary>
public sealed class AuthorVm : DialogVm
{
    public override string Title => "작성자";
    string name, mail;
    public string Name { get => name; set => Set(ref name, value); }
    public string Mail { get => mail; set => Set(ref mail, value); }
    public ICommand Ok { get; }

    public AuthorVm(string? name, string? mail)
    {
        this.name = name ?? "";
        this.mail = mail ?? "";
        Ok = new Command(() => Close("ok"), () => Name.Trim().Length > 0 && Mail.Trim().Length > 0);
    }
}

/// <summary>팝업 값 줄: 라벨 + 값(고정폭이면 해시·경로).</summary>
public sealed record Fact(string Label, string Value, bool Mono = false);

/// <summary>확인 팝업(값 위주): 제목, 라벨·값 줄, 단추(같은 크기, 기본 포커스 = 취소).</summary>
public sealed class FactsVm(string title, IReadOnlyList<Fact> facts, IReadOnlyList<AskButton> buttons) : DialogVm
{
    public override string Title => title;
    public IReadOnlyList<Fact> Facts => facts;
    public IReadOnlyList<AskButton> Buttons => buttons;
    public ICommand Press => new Command(p => Close(p as string));
}

/// <summary>받기 충돌(사양 8.4절 5): 충돌 파일 + [내 것 보관하고 원격 받기][그대로 두기]. 기본 포커스 = 그대로 두기.</summary>
public sealed class ConflictVm(IReadOnlyList<string> files) : DialogVm
{
    public override string Title => "충돌";
    public IReadOnlyList<string> Files => files;
    public string CountText => $"파일 {files.Count}";
    public ICommand Keep => new Command(() => Close("keep"));
}

/// <summary>코어 버전 고르기(사양 8.6절 2). 목록은 연 뒤에 받는다. 연결 안 되면 "오프라인".</summary>
public sealed class VersionsVm : DialogVm
{
    public override string Title => "코어 버전 변경";
    public string Current { get; }

    public VersionsVm(string current)
    {
        Current = current;
        Ok = new Command(() => Close("ok"), () => Selected != null);
    }

    IReadOnlyList<string> versions = [];
    public IReadOnlyList<string> Versions { get => versions; private set => Set(ref versions, value); }

    string? selected;
    public string? Selected { get => selected; set { Set(ref selected, value); CommandManager.InvalidateRequerySuggested(); } }

    bool loading = true, offline;
    public bool IsLoading { get => loading; private set => Set(ref loading, value); }
    public bool IsOffline { get => offline; private set => Set(ref offline, value); }
    public bool IsEmpty => !IsLoading && !IsOffline && Versions.Count == 0;

    public ICommand Ok { get; }

    /// <summary>목록 받기. null = 연결 안 됨.</summary>
    public async Task LoadAsync(Func<Task<IReadOnlyList<string>?>> list)
    {
        var got = await list();
        IsOffline = got == null;
        Versions = got ?? [];
        Selected = Versions.FirstOrDefault(v => v != Current);
        IsLoading = false;
        Raise(nameof(IsEmpty));
    }
}
