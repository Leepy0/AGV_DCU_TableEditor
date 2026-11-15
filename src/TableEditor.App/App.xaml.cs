using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TableEditor.App.ViewModels;
using TableEditor.App.Views;

namespace TableEditor.App;

/// <summary>
/// 여는 법: <c>--site &lt;사이트 폴더&gt;</c>(깃·빌드 꺼짐) 또는 <c>--repo &lt;앱 루트&gt; [--site &lt;사이트 이름&gt;]</c>.
/// 인자가 없으면 설정의 마지막 저장소, 그것도 없으면 빈 화면. 같은 폴더를 두 창이 열지 못한다(이름 붙인 뮤텍스).
/// 캡처: <c>--shot &lt;png&gt; [--tab &lt;탭&gt;] [--select &lt;줄&gt;] [--view] [--fake-update start|tick]</c> → 1280×900 창 내용을 배율 2 로 쓰고 끝낸다.
/// <c>--fake-update</c> = 지금 판을 0.0.1 로 낮춰 업데이트 팝업(start)·토스트(tick)를 띄운다(네트워크 없음).
/// 줄 수: <c>--counts &lt;파일&gt;</c> → 화면 줄 수를 쓰고 끝낸다(실제 사이트 확인).
/// 자체 시험: <c>--selftest &lt;작업 폴더&gt; [--shots &lt;폴더&gt;]</c> → 종료 코드 0 = 통과(<see cref="SelfTest"/>).
/// </summary>
public partial class App : Application
{
    sealed record Args(string? Repo, string? Site, string? Shot, string? Tab, int? Select, bool View, string? SelfTest, string? Shots, string? Counts, string? FakeUpdate);

    static Args Parse(string[] a)
    {
        string? repo = null, site = null, shot = null, tab = null, selftest = null, shots = null, counts = null, fake = null;
        int? select = null;
        bool view = false;
        for (int i = 0; i < a.Length; i++)
        {
            string? Next() => i + 1 < a.Length ? a[++i] : null;
            switch (a[i])
            {
                case "--repo": repo = Next(); break;
                case "--site": site = Next(); break;
                case "--shot": shot = Next(); break;
                case "--tab": tab = Next(); break;
                case "--select": select = int.TryParse(Next(), out var n) ? n : null; break;
                case "--view": view = true; break;
                case "--selftest": selftest = Next(); break;
                case "--shots": shots = Next(); break;
                case "--counts": counts = Next(); break;
                case "--fake-update": fake = Next(); break;
            }
        }
        return new(repo, site, shot, tab, select, view, selftest, shots, counts, fake);
    }

    static string CrashLog => Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "crash.log");

    static void Log(object e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLog)!);
            File.AppendAllText(CrashLog, $"{DateTime.Now:s} {e}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public App()
    {
        // 개발 PC 에서 하드웨어 그리기가 창 안을 흰 바탕으로만 남겼다(UI 는 살아 있음). 표 화면이라 소프트웨어 그리기로 충분하다.
        // shortcut: 모든 PC 에서 소프트웨어 그리기. 원인(드라이버·원격 세션)이 밝혀지면 그 경우만으로 좁힌다
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        // 죽기 전에 까닭을 남긴다(설정 폴더의 crash.log). 화면 스레드의 예외는 남기고 끝낸다.
        AppDomain.CurrentDomain.UnhandledException += (_, x) => Log(x.ExceptionObject);
        DispatcherUnhandledException += (_, x) =>
        {
            Log(x.Exception);
            x.Handled = true;
            Shutdown(3);
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var a = Parse(e.Args);
        bool shot = a.Shot != null || a.SelfTest != null || a.Counts != null;
        var settings = shot ? Settings.InMemory() : Settings.Load();
        var vm = new MainVm(settings) { DraftCheck = !shot };
        var w = new MainWindow(vm, settings, remember: !shot);
        MainWindow = w;
        if (shot)
        {
            // 화면 밖에 띄워 1280×900 내용만 그린다(바인딩은 실제 창 그대로)
            w.SizeToContent = SizeToContent.WidthAndHeight;
            w.Root.Width = 1280;
            w.Root.Height = 900;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            (w.Left, w.Top) = (-32000, -32000);
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }
        w.Show();

        if (a.SelfTest != null)
        {
            int code = await new SelfTest(w, vm, a.SelfTest, a.Shots).RunAsync();
            Shutdown(code);
            return;
        }

        var folder = a.Repo ?? a.Site ?? (settings.LastRepo is { } last && Directory.Exists(last) ? last : null);
        if (!shot && folder != null && !ClaimFolder(folder))
        {
            await AlreadyOpenAsync(vm);
            Shutdown(4);
            return;
        }

        await vm.TryOpen(() =>
            a.Repo != null ? vm.OpenRepoAsync(a.Repo, a.Site)
            : a.Site != null ? vm.OpenSiteAsync(a.Site)
            : folder != null ? vm.OpenRepoAsync(folder)
            : Task.CompletedTask);
        if (a.Repo != null && !shot && !vm.IsEmpty) { settings.LastRepo = vm.Repo; settings.Save(); }
        if (!shot && vm.HasRepo) _ = vm.StartupAsync(); // 작성자·연결 확인·대기 올리기(사양 8.1·8.3절)

        if (a.Tab != null && vm.Tabs.FirstOrDefault(t => t.Name == a.Tab) is { } tab) vm.Tab = tab;
        if (a.Select is int n) vm.Tag.SelectOrdinal(n);
        if (a.View) vm.Tag.IsLook = true;
        if (a.FakeUpdate != null)
        {
            // 캡처에 이 PC 경로가 들지 않게(설정은 메모리에만)
            (settings.GccPath, settings.Cl2000Path) = (@"D:\tools\gcc\bin\gcc.exe", @"C:\ti\ti-cgt-c2000_18.12.2.LTS\bin\cl2000.exe");
            vm.Config.Reload();
            vm.Update.Fake(a.FakeUpdate);
        }
        else if (!shot) _ = vm.Update.StartAsync(); // 새 판 확인(시작 팝업·1시간 토스트)

        if (a.Counts != null)
        {
            // 화면에 보이는 줄 수(실제 사이트 확인용): 프로파일 줄, 수동 줄, 시퀀스 표, 대응표 줄, 검사 오류·경고
            File.WriteAllLines(a.Counts, [
                $"프로파일 {vm.Profile.Rows.Count(r => r.Row != null)}",
                $"수동 {vm.Manual.Cards.Sum(c => c.Rows.Count) + vm.Manual.Inputs.Count}",
                $"시퀀스 표 {vm.Site?.Seqs.Count ?? 0} · 나무 {vm.Seq.Tree.Count(t => t.IsTable)}",
                $"대응표 {vm.Map.Rows.Count(r => r.Row != null)}",
                $"IO 별칭 {vm.Io.CountText}",
                $"오류 {vm.ErrorCount} · 경고 {vm.WarnCount}",
            ]);
            if (a.Shot == null) { Shutdown(); return; }
        }

        if (a.Shot != null)
        {
            await Idle(w);
            Save(w.Root, a.Shot);
            Shutdown();
        }
    }

    /// <summary>다 그릴 때까지 기다린다.</summary>
    public static async Task Idle(Window w)
    {
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        w.UpdateLayout();
        await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>요소를 배율 2 PNG 로.</summary>
    public static void Save(FrameworkElement el, string path)
    {
        const double scale = 2;
        var bmp = new RenderTargetBitmap((int)(el.ActualWidth * scale), (int)(el.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(el);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = File.Create(path);
        enc.Save(f);
    }

    // ---- 같은 폴더 두 창 막기 ----

    static Mutex? held;

    /// <summary>이 폴더를 이 창이 잡는다. 다른 창이 잡고 있으면 false(설계서 6절).</summary>
    public static bool ClaimFolder(string folder)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(folder).TrimEnd('\\', '/').ToUpperInvariant())))[..32];
        var m = new Mutex(true, @"Local\AGV_DCU_TableEditor_" + key, out bool created);
        if (!created) { m.Dispose(); return false; }
        held?.ReleaseMutex();
        held?.Dispose();
        held = m;
        return true;
    }

    public static Task AlreadyOpenAsync(MainVm vm) =>
        vm.Show(new AskVm("이미 열림", ["다른 창"], [new AskButton("ok", "닫기", Primary: true, Default: true)]));
}
