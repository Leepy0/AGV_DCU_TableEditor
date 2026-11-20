using System.IO;
using System.Text.Json;

namespace TableEditor.App;

/// <summary>
/// <c>%LOCALAPPDATA%\AGV_DCU_TableEditor\settings.json</c>(설계서 6절): 창, 저장소 폴더, 앱·코어 주소, 도구 경로, 이름, 고급 모드.
/// 앱 주소는 기본값이 없다(공개 저장소에 사내 주소를 적지 않는다). 읽기 실패는 기본값.
/// </summary>
public sealed class Settings
{
    public const string CoreUrlDefault = "https://github.com/Leepy0/AGV_DCU_Core.git";

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }
    public string? LastRepo { get; set; }
    public string AppUrl { get; set; } = "";
    public string CoreUrl { get; set; } = CoreUrlDefault;
    public string? GccPath { get; set; }
    public string? Cl2000Path { get; set; }
    public string UserName { get; set; } = "";
    public bool Advanced { get; set; }

    /// <summary>시작·1시간마다 새 버전 확인.</summary>
    public bool UpdateAuto { get; set; } = true;
    /// <summary>되돌린 버전: 자동 확인으로 다시 알리지 않는다.</summary>
    public string? UpdateSkip { get; set; }
    /// <summary>업데이트 확인·받기가 마지막에 된 길(Proxy·Direct). 다음 확인이 이 길부터.</summary>
    public string? UpdateRoute { get; set; }

    public static string FilePath => PathOf;

    static string PathOf => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AGV_DCU_TableEditor", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>캡처·자체 시험은 설정 파일을 읽지도 쓰지도 않는다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Volatile { get; private set; }

    public static Settings Load()
    {
        Settings s;
        try { s = File.Exists(PathOf) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
        // 예전 설정의 상대 경로는 예전 기준(설치 폴더 = 그때 일하는 폴더)으로 푼다. 지금은 일하는 폴더가 설정 폴더라 기준이 다르다
        (s.LastRepo, s.GccPath, s.Cl2000Path) = (Abs(s.LastRepo), Abs(s.GccPath), Abs(s.Cl2000Path));
        return s;
    }

    /// <summary>경로를 절대 경로로(상대 경로는 설치 폴더 기준). 비었거나 틀린 글이면 그대로.</summary>
    public static string? Abs(string? p)
    {
        if (string.IsNullOrWhiteSpace(p) || Path.IsPathFullyQualified(p)) return p;
        try { return Path.GetFullPath(p, AppContext.BaseDirectory); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return p; }
    }

    public static Settings InMemory() => new() { Volatile = true };

    public void Save()
    {
        if (Volatile) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf)!);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
