using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Styling;
using Avalonia.Threading;
using ReExtractor.Gui;
Environment.SetEnvironmentVariable("REEXTRACTOR_DATA_DIR", Path.GetFullPath("artifacts/feedback-ui-test/" + Guid.NewGuid()));
AppBuilder.Configure<Application>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
Application.Current!.Styles.Add(new FluentTheme());
Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
var panel = new FeedbackPanel();
typeof(FeedbackPanel).GetField("_sendFeedback",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(panel,
    (Func<string,string,IReadOnlyList<FeedbackAttachment>,Task>)((id,text,files)=>{
        if(files.Count!=2||files[0].Type!="image/png"||files[1].Type!="text/plain")throw new Exception("Incorrect submitted attachments");
        return Task.CompletedTask;
    }));
typeof(FeedbackPanel).GetField("_readComments",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(panel,
    (Func<int,Task<FeedbackService.PublicPage>>)(page=>Task.FromResult(new FeedbackService.PublicPage(new(){new("public-other","其他人的公开评论","R友 12345678",DateTimeOffset.Now)},1,1))));
var window = new Window { Width = 380, Height = 850, Content = panel };
window.Show();
Dispatcher.UIThread.RunJobs();
var add = typeof(FeedbackPanel).GetMethod("AddAttachment",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
add.Invoke(panel,new object[]{new FeedbackAttachment("test.png","image/png",CreateTestPng())});
panel.CurrentLogProvider=()=>"Synthetic log attachment test";
typeof(FeedbackPanel).GetMethod("OnAttachLogClicked",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(panel,new object?[]{null,new RoutedEventArgs()});
if(new FeedbackPanel().FindControl<StackPanel>("AttachmentCards")!.Children.Count!=2)throw new Exception("Attachments not persisted");
var input = panel.FindControl<TextBox>("DraftInput")!;
input.Text = "【附件联调测试】合成图片与日志，无真实屏幕或运行数据。";
Dispatcher.UIThread.RunJobs();
// Verify the attachment layout at the narrow sidebar size, before sending clears it.
window.Width=320;window.Height=720;Dispatcher.UIThread.RunJobs();
var cards=panel.FindControl<StackPanel>("AttachmentCards")!;
foreach(var card in cards.Children.Cast<Border>()){
 var row=(Grid)card.Child!;
 if(card.Bounds.Height>76)throw new Exception("Attachment card too tall");
 var remove=(Button)row.Children[2];
 if(remove.Bounds.Width<28||remove.Bounds.Right>row.Bounds.Width+1)throw new Exception("Remove button clipped");
}
Directory.CreateDirectory("artifacts/feedback-reference");
using(var frame=window.CaptureRenderedFrame())frame!.Save("artifacts/feedback-reference/attachments-compact.png");
var firstRemove=(Button)((Grid)((Border)cards.Children[0]).Child!).Children[2];
firstRemove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
if(cards.Children.Count!=1||new FeedbackPanel().FindControl<StackPanel>("AttachmentCards")!.Children.Count!=1)throw new Exception("Remove or persisted removal failed");
// Restore the image first to retain the send assertion's original ordering.
var logRemove=(Button)((Grid)((Border)cards.Children[0]).Child!).Children[2];logRemove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
add.Invoke(panel,new object[]{new FeedbackAttachment("test.png","image/png",CreateTestPng())});
typeof(FeedbackPanel).GetMethod("OnAttachLogClicked",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(panel,new object?[]{null,new RoutedEventArgs()});
var restored = new FeedbackPanel();
if (restored.FindControl<TextBox>("DraftInput")!.Text != input.Text) throw new Exception("Draft not restored");
panel.FindControl<Button>("SendFeedbackButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
var deadline = DateTime.UtcNow.AddSeconds(35);
while (!panel.FindControl<Button>("SendFeedbackButton")!.IsEnabled && DateTime.UtcNow < deadline)
{
    Dispatcher.UIThread.RunJobs();
    Thread.Sleep(50);
}
Dispatcher.UIThread.RunJobs();
if (!panel.FindControl<TextBlock>("FeedbackStatus")!.Text!.StartsWith("已发送"))
    throw new Exception(panel.FindControl<TextBlock>("FeedbackStatus")!.Text);
if(!panel.FindControl<StackPanel>("DraftCards")!.GetVisualDescendants().OfType<SelectableTextBlock>().Any(x=>x.Text=="其他人的公开评论"))throw new Exception("Public comment missing");
var readField=typeof(FeedbackPanel).GetField("_readComments",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
var sync=typeof(FeedbackPanel).GetMethod("SyncVisibilityAsync",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
readField.SetValue(panel,(Func<int,Task<FeedbackService.PublicPage>>)(_=>Task.FromResult(new FeedbackService.PublicPage(new(),1,0))));
((Task)sync.Invoke(panel,null)!).GetAwaiter().GetResult();
if(panel.FindControl<StackPanel>("DraftCards")!.Children.OfType<Border>().Any())throw new Exception("Cleared comments remain");
if(panel.FindControl<StackPanel>("DraftCards")!.Children.Count!=0)throw new Exception("Empty feed displays pagination");
window.Width=320;window.Height=600;window.UpdateLayout();Dispatcher.UIThread.RunJobs();Console.WriteLine($"Window={window.Bounds} panel={panel.Bounds}");
var sendControl=panel.FindControl<Button>("SendFeedbackButton")!;
var sendPosition=sendControl.TranslatePoint(new Point(0,sendControl.Bounds.Height),panel);
if(sendPosition is null||sendPosition.Value.Y>panel.Bounds.Height)throw new Exception("Send button outside panel");
using(var emptyFrame=window.CaptureRenderedFrame())emptyFrame!.Save("artifacts/feedback-reference/empty-review.png");

readField.SetValue(panel,(Func<int,Task<FeedbackService.PublicPage>>)(_=>Task.FromException<FeedbackService.PublicPage>(new Exception("Simulated offline"))));
((Task)sync.Invoke(panel,null)!).GetAwaiter().GetResult();
if(panel.FindControl<StackPanel>("DraftCards")!.Children.OfType<Border>().Any())throw new Exception("Offline restored old comments");
var saved = new FeedbackPanel();
if (saved.FindControl<StackPanel>("DraftCards")!.Children.OfType<Border>().Any() || saved.FindControl<TextBox>("DraftInput")!.Text != "") throw new Exception("Sent feedback not restored");Directory.CreateDirectory("artifacts/feedback-reference");
using (var bitmap = window.CaptureRenderedFrame()) bitmap!.Save("artifacts/feedback-reference/sidebar.png");
var main = new MainWindow();
var launcher = main.FindControl<Button>("FeedbackLauncher")!;
var sidebar = main.FindControl<FeedbackPanel>("FeedbackSidebar")!;
main.WindowState=WindowState.Normal;main.Width=1000;main.Height=740;main.Show();Dispatcher.UIThread.RunJobs();
var menu=main.FindControl<Menu>("MainMenu")!;
var menuItems=menu.Items.Cast<MenuItem>().ToList();
var feedbackIndex=menuItems.FindIndex(x=>x.Name=="FeedbackMenuItem");
if(feedbackIndex<0||menuItems[feedbackIndex+1].Header?.ToString()!="检查更新…")throw new Exception("Feedback menu order incorrect");
if(launcher.HorizontalAlignment!=Avalonia.Layout.HorizontalAlignment.Right||launcher.VerticalAlignment!=Avalonia.Layout.VerticalAlignment.Bottom)throw new Exception("Floating feedback position incorrect");
var dragStart=launcher.TranslatePoint(new Point(launcher.Bounds.Width/2,launcher.Bounds.Height/2),main)!.Value;
var beforeDrag=launcher.Margin;
main.MouseDown(dragStart,Avalonia.Input.MouseButton.Left);
main.MouseMove(dragStart+new Vector(-100,-80));
main.MouseUp(dragStart+new Vector(-100,-80),Avalonia.Input.MouseButton.Left);
Dispatcher.UIThread.RunJobs();
if(sidebar.IsVisible||launcher.Margin.Right<beforeDrag.Right+90||launcher.Margin.Bottom<beforeDrag.Bottom+70)throw new Exception("Dragging failed or opened sidebar");
var clickPoint=launcher.TranslatePoint(new Point(launcher.Bounds.Width/2,launcher.Bounds.Height/2),main)!.Value;
main.MouseDown(clickPoint,Avalonia.Input.MouseButton.Left);main.MouseUp(clickPoint,Avalonia.Input.MouseButton.Left);Dispatcher.UIThread.RunJobs();
if(!sidebar.IsVisible)throw new Exception("Click after drag failed");
launcher.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitAnimation();Dispatcher.UIThread.RunJobs();
using(var frame=main.CaptureRenderedFrame())frame!.Save("artifacts/feedback-reference/chat-launcher.png");
launcher.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitAnimation();
if (!sidebar.IsVisible || launcher.IsVisible) throw new Exception("Open toggle failed");
launcher.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));WaitAnimation();
if (sidebar.IsVisible || !launcher.IsVisible) throw new Exception("Close toggle failed");
Console.WriteLine("PASS: animated sidebar, draft recovery, attachment preview, persistence, mocked send and sent recovery, sidebar open/close");
main.Close();window.Close();







static byte[] CreateTestPng(){
 using var bitmap=new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(8,8),new Vector(96,96),Avalonia.Platform.PixelFormat.Bgra8888,Avalonia.Platform.AlphaFormat.Opaque);
 using var stream=new MemoryStream();bitmap.Save(stream);return stream.ToArray();
}


static void WaitAnimation(){var end=DateTime.UtcNow.AddMilliseconds(260);while(DateTime.UtcNow<end){Dispatcher.UIThread.RunJobs();Thread.Sleep(10);}Dispatcher.UIThread.RunJobs();}
