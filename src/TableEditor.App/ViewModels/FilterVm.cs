using TableEditor.App.Mvvm;
using TableEditor.Engine.Model;

namespace TableEditor.App.ViewModels;

/// <summary>
/// 표 화면 거르기 막대(<see cref="Views.FilterBar"/>)의 VM: 번호 칸 + 프로파일. 태그·시퀀스 화면이 같이 쓴다.
/// 값이 바뀌면 <see cref="Changed"/>. 맞는지는 <see cref="RowFilter.Match"/>.
/// </summary>
public sealed class FilterVm : Observable
{
    public event Action? Changed;

    string text = "";
    public string Text { get => text; set { if (Set(ref text, value ?? "")) Changed?.Invoke(); } }

    string profile = RowFilter.All;
    public string Profile { get => profile; set { if (Set(ref profile, value ?? RowFilter.All)) Changed?.Invoke(); } }

    IReadOnlyList<string> profiles = [RowFilter.All];
    /// <summary>드롭다운: 전체 + 프로파일. 고른 것이 빠지면 전체로(알림 없이: 부른 쪽이 다시 거른다).</summary>
    public IReadOnlyList<string> Profiles
    {
        get => profiles;
        set
        {
            Set(ref profiles, value);
            if (!value.Contains(profile)) { profile = RowFilter.All; Raise(nameof(Profile)); }
        }
    }

    public long? Number => RowFilter.Number(Text);
    public bool IsSet => Text.Trim().Length > 0 || Profile != RowFilter.All;

    public bool Match(string? rowProfile, bool any, Func<long, bool> covers) => RowFilter.Match(Text, Profile, rowProfile, any, covers);

    /// <summary>둘 다 비운다(알림 없이: 부른 쪽이 다시 거른다).</summary>
    public void Clear()
    {
        text = "";
        profile = RowFilter.All;
        Raise(nameof(Text));
        Raise(nameof(Profile));
    }
}
