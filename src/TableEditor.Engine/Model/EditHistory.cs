namespace TableEditor.Engine.Model;

/// <summary>
/// 되돌리기·다시 하기 = 앞뒤 사이트(조각 목록)를 바꿔 끼우기(설계서 4.4절). 저장한 지점을 기억해 변경 수를 센다.
/// </summary>
public sealed class EditHistory
{
    readonly List<Site> sites;
    int index;
    int saved; // 저장한 지점. 되돌린 뒤 새 편집으로 그 지점이 버려지면 -1

    public EditHistory(Site site)
    {
        sites = [site];
        index = 0;
        saved = 0;
    }

    public Site Current => sites[index];
    public bool CanUndo => index > 0;
    public bool CanRedo => index < sites.Count - 1;

    /// <summary>저장한 지점에서 몇 걸음 떨어졌는가. 0 = 저장한 그대로. 저장 지점이 버려졌으면 처음부터의 걸음 + 1.</summary>
    public int Changes => saved >= 0 ? Math.Abs(index - saved) : index + 1;

    public bool Dirty => Changes != 0;

    /// <summary>편집 결과를 쌓는다(다시 하기 줄은 버린다). 성공한 결과만 넣는다.</summary>
    public Site Push(EditResult r)
    {
        if (!r.Ok) return Current;
        if (ReferenceEquals(r.Site, Current)) return Current;
        sites.RemoveRange(index + 1, sites.Count - index - 1);
        if (saved > index) saved = -1;
        sites.Add(r.Site!);
        index++;
        return Current;
    }

    public Site Undo()
    {
        if (CanUndo) index--;
        return Current;
    }

    public Site Redo()
    {
        if (CanRedo) index++;
        return Current;
    }

    /// <summary>
    /// 저장했다. 지금 자리를 저장 뒤 다시 읽은 사이트로 바꾸고 저장 지점으로 둔다.
    /// 앞뒤 단계도 그 디스크 기록을 같이 본다: 되돌린 뒤 저장하면 되돌린 표가 디스크에 쓰인다.
    /// </summary>
    public void MarkSaved(Site reread)
    {
        sites[index] = reread;
        saved = index;
        foreach (var s in sites) s.ShareDisk(reread);
    }
}
