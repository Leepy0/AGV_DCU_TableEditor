using System.Text;
using TableEditor.Engine.Grammar;

namespace TableEditor.Engine.Text;

/// <summary>
/// 읽은 파일 하나 = 조각 목록(바꾸지 않는 값). 안 고친 조각의 원문을 이으면 읽은 글자가 그대로 나온다(사양 5.5절).
/// UTF-8 이 아니거나 BOM 이 있거나 구역 표지 짝이 안 맞으면(R-2) 통째 원문 한 조각·읽기 전용이다.
/// </summary>
public sealed class SourceFile
{
    static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    static readonly UTF8Encoding Loose = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public string Name { get; }
    public IReadOnlyList<Piece> Pieces { get; }

    /// <summary>읽기 전용인 까닭(<c>BOM</c>, <c>UTF-8 아님</c>, <c>R-2 …</c>). null 이면 쓸 수 있다.</summary>
    public string? ReadOnlyReason { get; }

    readonly byte[] original;

    SourceFile(string name, IReadOnlyList<Piece> pieces, string? readOnlyReason, byte[] original)
    {
        Name = name;
        Pieces = pieces;
        ReadOnlyReason = readOnlyReason;
        this.original = original;
    }

    public bool ReadOnly => ReadOnlyReason != null;

    public static SourceFile Read(string path) => Parse(Path.GetFileName(path), File.ReadAllBytes(path));

    /// <summary>바이트를 파일 이름의 구역 표(사양 5.2절)로 읽는다.</summary>
    public static SourceFile Parse(string name, byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Whole(name, bytes, "BOM");
        string text;
        try { text = Strict.GetString(bytes); }
        catch (DecoderFallbackException) { return Whole(name, bytes, "UTF-8 아님"); }

        var (pieces, error) = Reader.Run(text, FileZones.For(name));
        return pieces == null ? Whole(name, bytes, error) : new SourceFile(name, pieces, null, bytes);
    }

    static SourceFile Whole(string name, byte[] bytes, string? reason)
    {
        var text = Loose.GetString(bytes);
        int lines = Math.Max(1, text.Count(c => c == '\n') + (text.EndsWith('\n') ? 0 : 1));
        return new SourceFile(name, [new Piece(PieceKind.Raw, text, 1, lines, null)], reason, bytes);
    }

    public string ToText() => string.Concat(Pieces.Select(p => p.Text));

    /// <summary>쓸 바이트. 읽기 전용 파일은 읽은 바이트 그대로.</summary>
    public byte[] ToBytes() => ReadOnly ? (byte[])original.Clone() : Strict.GetBytes(ToText());

    public IEnumerable<Statement> Statements => Pieces.OfType<Statement>();

    /// <summary>표 구역 안 원문 조각(R-1 대상).</summary>
    public IEnumerable<Piece> RawInZones => Pieces.Where(p => p.Kind == PieceKind.Raw && p.Zone != null);
}
