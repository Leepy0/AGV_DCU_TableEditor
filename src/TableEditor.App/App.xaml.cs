using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TableEditor.App.ViewModels;
using TableEditor.App.Views;

namespace TableEditor.App;

/// <summary>
/// 여는 법: <c>--site &lt;사이트 폴더&gt;</c>(깃·빌드 꺼짐) 또는 <c>--repo &lt;앱 루트&gt; [--site &lt;사이트 이름&gt;]</c>.
/// 인자가 없으면 설정의 마지막 저장소, 그것도 없으면 빈 화면.
/// 캡처: <c>--shot &lt;png&gt; [--tab &lt;탭&gt;] [--select &lt;줄&gt;] [--view]</c> → 1280×900 창 내용을 배율 2 로 쓰고 끝낸다.
/// </summary>
public partial class App : Application
{
    sealed record Args(string? Repo, string? Site, string? Shot, string? Tab, int? Select, bool View);

    static Args Parse(string[] a)
    {
        string? repo = null, site = null, shot = null, tab = null;
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
            }
        }
        return new(repo, site, shot, tab, select, view);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, x) =>
        {
            // 죽기 전에 까닭을 남긴다(설정 폴더의 crash.log)
            try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(Settings.FilePath)!, "crash.log"), $"{DateTime.Now:s} {x.Exception}\n"); }
            catch (IOException) { }
            x.Handled = true;
            Shutdown(3);
        };
        var a = Parse(e.Args);
        var settings = Settings.Load();
        var vm = new MainVm();
        bool shot = a.Shot != null;
        var w = new MainWindow(vm, settings, remember: !shot);
        MainWindow = w;
        if (shot)
        {
            // 화면 밖에 띄워 1280×900 내용만 그린다
            w.SizeToContent = SizeToContent.WidthAndHeight;
            w.Root.Width = 1280;
            w.Root.Height = 900;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            (w.Left, w.Top) = (-32000, -32000);
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }
        w.Show();

        await vm.TryOpen(() =>
            a.Repo != null ? vm.OpenRepoAsync(a.Repo, a.Site)
            : a.Site != null ? vm.OpenSiteAsync(a.Site)
            : settings.LastRepo is { } r && Directory.Exists(r) ? vm.OpenRepoAsync(r)
            : Task.CompletedTask);
        if (a.Repo != null && !shot && !vm.IsEmpty) { settings.LastRepo = vm.Repo; settings.Save(); }

        if (a.Tab != null && vm.Tabs.FirstOrDefault(t => t.Name == a.Tab) is { } tab) vm.Tab = tab;
        if (a.Select is int n) vm.Tag?.SelectOrdinal(n);
        if (a.View && vm.Tag != null) vm.Tag.IsLook = true;

        if (shot)
        {
            await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            w.UpdateLayout();
            await w.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Save(w.Root, a.Shot!);
            Shutdown();
        }
    }

    /// <summary>요소를 배율 2 PNG 로.</summary>
    static void Save(FrameworkElement el, string path)
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
}
