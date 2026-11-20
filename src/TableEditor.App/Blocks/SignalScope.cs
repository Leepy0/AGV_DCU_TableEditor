using TableEditor.Engine.Model;

namespace TableEditor.App.Blocks;

/// <summary>
/// 신호·시퀀스 목록을 걸러 보는 범위: 그 줄의 프로파일들(null = 전부). 신호는 IO 맵 <c>/* pf: … */</c> 지정이 겹치거나 지정이 없는 것만 보인다.
/// <see cref="ShowAll"/> = 목록 끝 "전체" 토글(창 하나라 전역).
/// </summary>
public sealed class SignalScope(Site site, IReadOnlySet<string>? profiles)
{
    public static bool ShowAll { get; set; }

    public const string AllOn = "✓ 전체", AllOff = "전체";

    public Site Site { get; } = site;
    public IReadOnlySet<string>? Profiles { get; } = profiles;

    /// <summary>태그 동작 칸: 시퀀스 ID 를 <c>ID → 표</c> 로 묶어 보인다.</summary>
    public bool Seq { get; set; }

    public static SignalScope All(Site s) => new(s, null);

    /// <summary>프로파일 글(이름·번호·ANY_PROFILE·0)로: 전체면 걸러 보지 않는다.</summary>
    public static SignalScope Of(Site s, string? profile)
    {
        if (profile == null) return All(s);
        var p = profile.Trim();
        if (p is "0" or "ANY_PROFILE" or "") return All(s);
        if (long.TryParse(p, out var n)) p = s.Profiles.FirstOrDefault(x => x.Number == n)?.Name ?? p;
        return new(s, new HashSet<string>([p]));
    }

    public static bool IsPin(NameDef d) => !d.Core && d.Kind is NameKind.Input or NameKind.InputHigh or NameKind.IoNone or NameKind.Output;

    /// <summary>이 신호는 범위 밖(지정이 있고 겹치지 않는다). 토글과 상관없이.</summary>
    public bool Excludes(NameDef d) => Profiles != null && IsPin(d) && d.Profiles is { } p && !p.Any(Profiles.Contains);

    public bool Hides(NameDef d) => !ShowAll && Excludes(d);

    /// <summary>토글이 뜻을 가진다: 걸러 볼 범위가 있고 가려질 신호가 있다(또는 지금 전체를 켰다).</summary>
    public bool CanToggle => Profiles != null && Site.Names.All.Any(Excludes);
}
