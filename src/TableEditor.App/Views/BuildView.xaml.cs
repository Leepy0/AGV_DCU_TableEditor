using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TableEditor.App.ViewModels;

namespace TableEditor.App.Views;

/// <summary>출력 줄 끝 따라가기: 맨 아래에 있으면 새 줄을 따라가고, 위로 올리면 풀린다(다시 내리면 다시 따라간다).</summary>
public partial class BuildView : UserControl
{
    bool follow = true;
    ScrollViewer? sv;
    INotifyCollectionChanged? watched;

    public BuildView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Watch();
        Out.Loaded += (_, _) =>
        {
            sv = Find<ScrollViewer>(Out);
            if (sv != null) sv.ScrollChanged += OnScroll;
        };
    }

    void Watch()
    {
        if (watched != null) watched.CollectionChanged -= OnLines;
        watched = (DataContext as BuildVm)?.Lines;
        if (watched != null) watched.CollectionChanged += OnLines;
    }

    void OnLines(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset) { follow = true; return; }
        if (follow && Out.Items.Count > 0) sv?.ScrollToEnd();
    }

    void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        // 내용이 늘어난 것이 아니라 사람이 움직였을 때만 따라가기를 바꾼다
        if (e.ExtentHeightChange == 0) follow = sv!.VerticalOffset >= sv.ScrollableHeight - 1;
    }

    static T? Find<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (Find<T>(c) is { } x) return x;
        }
        return null;
    }
}
