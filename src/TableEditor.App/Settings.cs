using System.IO;
using System.Text.Json;

namespace TableEditor.App;

/// <summary><c>%LOCALAPPDATA%\AGV_DCU_TableEditor\settings.json</c>(설계서 6절). 읽기 실패는 기본값.</summary>
public sealed class Settings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public bool Maximized { get; set; }
    public string? LastRepo { get; set; }

    public static string FilePath => PathOf;

    static string PathOf => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AGV_DCU_TableEditor", "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return File.Exists(PathOf) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathOf)) ?? new() : new(); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathOf)!);
            File.WriteAllText(PathOf, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
