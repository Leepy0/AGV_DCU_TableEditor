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

    public static string FilePath => PathOf;

    static string PathOf => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AGV_DCU_TableEditor", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>캡처·자체 시험은 설정 파일을 읽지도 쓰지도 않는다.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Volatile { get; private set; }

    public static Settings Load()
    {
        try { return File.Exists(PathOf) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
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
