using TableEditor.Engine.Grammar;

namespace TableEditor.Engine.Text;

/// <summary>사이트 폴더의 읽기 대상 파일(사양 2.3절)을 읽는다. 없는 파일은 건너뛴다. 다른 파일은 열지 않는다.</summary>
public static class SiteFolder
{
    public static IReadOnlyList<SourceFile> Read(string folder) =>
        FileZones.Files
            .Select(f => Path.Combine(folder, f.FileName))
            .Where(File.Exists)
            .Select(SourceFile.Read)
            .ToList();
}
