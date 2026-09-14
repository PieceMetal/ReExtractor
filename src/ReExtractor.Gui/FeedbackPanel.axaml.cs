using System;
using System.Collections.Generic;
using System.IO;

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ReExtractor.Gui;

public partial class FeedbackPanel : UserControl
{
    public event Action? CloseRequested;
    public sealed class DraftState
    {
        public string Input { get; set; } = "";
        public List<string> Saved { get; set; } = new();
        public HashSet<string> HiddenSent { get; set; } = new();
        public List<string> SentIds { get; set; } = new();
        public HashSet<string> HiddenIds { get; set; } = new();
        public List<DateTimeOffset?> SentAt { get; set; } = new();
        public List<string> Sent { get; set; } = new();
        public List<FeedbackAttachment> Attachments { get; set; } = new();
        public string PendingId { get; set; } = "";
        public string PendingText { get; set; } = "";
    }

    private readonly Func<string, string, System.Collections.Generic.IReadOnlyList<FeedbackAttachment>, System.Threading.Tasks.Task> _sendFeedback = FeedbackService.SendAsync;
    private DraftState _state = new();
    private bool _ready;
    private string DraftPath => Path.Combine(Environment.GetEnvironmentVariable("REEXTRACTOR_DATA_DIR") is null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ReExtractor") : AppPaths.DataDirectory, "feedback-drafts.json");

    public FeedbackPanel()
    {
        InitializeComponent();
        DraftInput.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnFeedbackPasteKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        try
        {
            var legacyPath=Path.Combine(AppPaths.DataDirectory,"feedback-drafts.json");
            if(!File.Exists(DraftPath)&&File.Exists(legacyPath)){
                Directory.CreateDirectory(Path.GetDirectoryName(DraftPath)!);
                File.Copy(legacyPath,DraftPath);
            }
            if (File.Exists(DraftPath))
                _state = JsonSerializer.Deserialize<DraftState>(File.ReadAllText(DraftPath)) ?? new();
            _state.Saved ??= new();
            _state.Sent ??= new();
            _state.SentIds ??= new();_state.HiddenIds ??= new();
            while(_state.SentIds.Count<_state.Sent.Count)_state.SentIds.Add("");
            _state.SentAt ??= new();
            while(_state.SentAt.Count<_state.Sent.Count)_state.SentAt.Add(null);
            _state.HiddenSent ??= new();
            _state.Attachments ??= new();
        }
        catch (Exception ex) { ShowStatus("草稿读取失败：" + ex.Message); }
        DraftInput.Text = _state.Input;
        RefreshCards();
        RefreshAttachments();
        _ready = true;
        var syncTimer=new Avalonia.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};
        syncTimer.Tick+=async (_,_)=>await SyncVisibilityAsync();
        AttachedToVisualTree+=(_,_)=>syncTimer.Start();DetachedFromVisualTree+=(_,_)=>syncTimer.Stop();

    }

    private Func<int,System.Threading.Tasks.Task<FeedbackService.PublicPage>> _readComments=FeedbackService.ReadCommentsAsync;
    private FeedbackService.PublicPage? _publicComments;
    private int _commentPage=1;
    private bool _syncingVisibility;
    private async System.Threading.Tasks.Task SyncVisibilityAsync(){
        if(_syncingVisibility||!IsEffectivelyVisible)return;
        _syncingVisibility=true;
        try{
            _publicComments=await _readComments(_commentPage);
            _commentPage=_publicComments.Page;
            if(FeedbackStatus.Text?.Contains("公共评论连接失败")==true||FeedbackStatus.Text?.Contains("公共评论刷新失败")==true)FeedbackStatus.IsVisible=false;
            RefreshCards();

        }
        catch (Exception ex) {
            _publicComments=null;RefreshCards();
            ((TextBlock)DraftCards.Children[0]).Text="公共评论暂时无法读取，正在重试。";
            try { Directory.CreateDirectory(AppPaths.LogsDirectory); File.WriteAllText(Path.Combine(AppPaths.LogsDirectory,"feedback-sync-error.log"),DateTimeOffset.Now+Environment.NewLine+ex); } catch { }
            ShowStatus(FeedbackStatus.Text?.StartsWith("已发送")==true?"已发送；公共评论刷新失败，稍后重试。":"公共评论连接失败，正在重试。");
        }
        finally{_syncingVisibility=false;}
    }
    private void OnCloseClicked(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void OnCategoryClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            DraftInput.Text = $"【{button.Content}】" + DraftInput.Text;
            DraftInput.Focus();
        }
    }

    private void OnDraftChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _state.Input = DraftInput.Text ?? "";
        Persist();
    }

    private bool Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DraftPath)!);
            var temporary = DraftPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
            File.Move(temporary, DraftPath, true);
            return true;
        }
        catch (Exception ex) { ShowStatus("草稿保存失败：" + ex.Message); return false; }
    }

    private async void OnSendClicked(object? sender, RoutedEventArgs e)
    {
        var text = DraftInput.Text?.Trim() ?? "";
        if (text.Length == 0) { ShowStatus("请先填写问题描述"); return; }
        if (text.Length > 6000) { ShowStatus("反馈最多 6000 字，请精简后发送。"); return; }
        if (!SendFeedbackButton.IsEnabled) return;
        if (_state.PendingText != text || string.IsNullOrEmpty(_state.PendingId))
        {
            _state.PendingId = Guid.NewGuid().ToString();
            _state.PendingText = text;
        }
        _state.Input = DraftInput.Text ?? "";
        if (!Persist()) return;
        SendFeedbackButton.IsEnabled = false;
        SendFeedbackButton.Content = "发送中…";
        DraftInput.IsReadOnly = true;
        CategoryButtons.IsEnabled = false; AttachmentActions.IsEnabled = false; AttachmentCards.IsEnabled = false;
        try
        {
            await _sendFeedback(_state.PendingId, text, _attachments);
            _attachments.Clear(); RefreshAttachments();
            _state.SentIds.Add(_state.PendingId);
            _state.Sent.Add(text);
            _state.SentAt.Add(DateTimeOffset.Now);
            _state.Input = "";
            _state.PendingId = "";
            _state.PendingText = "";
            DraftInput.Text = "";
            if (Persist()) ShowStatus("已发送，感谢你的反馈。");
            RefreshCards();
            _commentPage=1;await SyncVisibilityAsync();
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDirectory);
                File.WriteAllText(Path.Combine(AppPaths.LogsDirectory, "feedback-error.log"),
                    DateTimeOffset.Now + Environment.NewLine + ex);
            }
            catch { /* Preserve the input even when diagnostics cannot be written. */ }
            ShowStatus(ex is System.Net.Http.HttpRequestException
                ? "网络安全连接失败，反馈内容已保留。请检查本机网络或联系管理员。"
                : ex is System.Threading.Tasks.TaskCanceledException
                    ? "发送超时，内容已保留，请稍后重试。"
                    : ex is InvalidOperationException && (ex.Message=="此设备已被禁止提交反馈。" || ex.Message=="请更新工具后再发送反馈。") ? ex.Message : "发送未成功，内容已保留，请稍后重试。");
        }
        finally
        {
            SendFeedbackButton.IsEnabled = true;
            SendFeedbackButton.Content = "发送";
            DraftInput.IsReadOnly = false;
            CategoryButtons.IsEnabled = true; AttachmentActions.IsEnabled = true; AttachmentCards.IsEnabled = true;
        }
    }
    private void ShowStatus(string text)
    {
        FeedbackStatus.Text = text;
        FeedbackStatus.IsVisible = true;
    }

    private bool IsSentHidden(int index)=>index<_state.SentIds.Count&&!string.IsNullOrEmpty(_state.SentIds[index])?_state.HiddenIds.Contains(_state.SentIds[index]):_state.HiddenSent.Contains(_state.Sent[index]);
    private void RefreshCards()
    {
        if(_publicComments is not null){
            EmptyState.IsVisible=_publicComments.Items.Count==0;
            DraftCards.Children.Clear();
            foreach(var entry in _publicComments.Items){
                var content=new StackPanel{Spacing=8};
                var header=new Grid{ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=8};
                header.Children.Add(new TextBlock{Text=string.IsNullOrWhiteSpace(entry.Nickname)?"R友":entry.Nickname,Foreground=Brush.Parse("#8cbcff"),FontSize=13,FontWeight=FontWeight.SemiBold});
                var time=new TextBlock{Text=entry.CreatedAt.ToLocalTime().ToString("yyyy/MM/dd HH:mm"),FontSize=11,Foreground=Brush.Parse("#98a6b8")};Grid.SetColumn(time,1);header.Children.Add(time);
                content.Children.Add(header);content.Children.Add(new SelectableTextBlock{Text=entry.Message,TextWrapping=TextWrapping.Wrap});
                DraftCards.Children.Add(new Border{Background=Brush.Parse("#263348"),CornerRadius=new CornerRadius(10),Padding=new Thickness(14),Child=content});
            }
            var pages=Math.Max(1,(_publicComments.Total+19)/20);
            if(pages<=1)return;
            var navigation=new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=8,HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center};
            var previous=new Button{Content="上一页",IsEnabled=_commentPage>1};
            var next=new Button{Content="下一页",IsEnabled=_commentPage<pages};
            previous.Click+=async(_,_)=>{if(_syncingVisibility)return;_commentPage--;await SyncVisibilityAsync();};
            next.Click+=async(_,_)=>{if(_syncingVisibility)return;_commentPage++;await SyncVisibilityAsync();};
            navigation.Children.Add(previous);navigation.Children.Add(new TextBlock{Text=$"{_commentPage}/{pages} · {_publicComments.Total} 条",VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center});navigation.Children.Add(next);DraftCards.Children.Add(navigation);
            return;
        }
        EmptyState.IsVisible=false;
        DraftCards.Children.Clear();
        DraftCards.Children.Add(new TextBlock{Text="正在读取公共评论…",Foreground=Brush.Parse("#98a6b8"),TextWrapping=TextWrapping.Wrap});
    }
}
