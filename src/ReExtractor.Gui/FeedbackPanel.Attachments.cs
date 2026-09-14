using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Input;
using Avalonia.Platform.Storage;
namespace ReExtractor.Gui;
public sealed record FeedbackAttachment(string Name,string Type,byte[] Bytes);
public partial class FeedbackPanel
{
    public Func<string>? CurrentLogProvider { get; set; }
    private readonly DateTime _openedAt=DateTime.Now;
    private List<FeedbackAttachment> _attachments => _state.Attachments;
    private readonly List<Bitmap> _previews=new();
    private async void OnSelectImageClicked(object? sender,RoutedEventArgs e)
    {
        var top=TopLevel.GetTopLevel(this);if(top==null)return;
        try{
            var files=await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{
                Title="选择反馈图片",AllowMultiple=true,
                FileTypeFilter=new[]{new FilePickerFileType("PNG / JPEG 图片"){Patterns=new[]{"*.png","*.jpg","*.jpeg"}}}
            });
            foreach(var file in files){
                if(!AttachmentActions.IsEnabled)break;
                using var stream=await file.OpenReadAsync();using var output=new MemoryStream();
                var buffer=new byte[8192];int count;
                while((count=await stream.ReadAsync(buffer))>0){
                    output.Write(buffer,0,count);if(output.Length>2*1024*1024)break;
                }
                var name=file.Name.Length>100?file.Name[^100..]:file.Name;
                AddAttachment(new(name,Path.GetExtension(name).Equals(".png",StringComparison.OrdinalIgnoreCase)?"image/png":"image/jpeg",output.ToArray()));
            }
        }catch(Exception){ShowStatus("图片读取失败，请重新选择。");}
    }
    private void OnFeedbackPasteKeyDown(object? sender,KeyEventArgs e)
    {
        if(e.Key!=Key.V||(e.KeyModifiers&KeyModifiers.Control)==0||!AttachmentActions.IsEnabled)return;
        try{
            var png=FeedbackScreenshot.ReadPng();
            if(png==null)return; // Let the text box perform ordinary text paste.
            e.Handled=true;
            AddAttachment(new("粘贴图片-"+DateTime.Now.ToString("HHmmss")+".png","image/png",png));
        }catch(Exception){e.Handled=true;ShowStatus("剪贴板图片读取失败，请重新复制或选择图片。");}
    }
    private void OnAttachLogClicked(object? sender,RoutedEventArgs e)
    {
        var text=CurrentLogProvider?.Invoke()??"";
        try{
            var recent=Directory.Exists(AppPaths.LogsDirectory)?new DirectoryInfo(AppPaths.LogsDirectory).GetFiles("fbx-*.log").Where(f=>f.LastWriteTime>=_openedAt).OrderByDescending(f=>f.LastWriteTime).FirstOrDefault():null;
            if(recent!=null){
                using var stream=new FileStream(recent.FullName,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                if(stream.Length>256*1024)stream.Seek(-256*1024,SeekOrigin.End);
                using var reader=new StreamReader(stream);
                text+="\n\n--- "+recent.Name+" ---\n"+reader.ReadToEnd();
            }
            if(string.IsNullOrWhiteSpace(text)){ShowStatus("本次运行暂无日志可附带。");return;}
            if(text.Length>120000)text="[仅附最近日志]\n"+text[^120000..];
            var bytes=Encoding.UTF8.GetBytes(text);
            var old=_attachments.FirstOrDefault(a=>a.Type=="text/plain");
            if(old!=null)_attachments.Remove(old);
            AddAttachment(new("运行日志-"+DateTime.Now.ToString("HHmmss")+".log","text/plain",bytes));
        }catch(Exception){ShowStatus("日志读取失败，请稍后重试。");}
    }
    private void AddAttachment(FeedbackAttachment attachment)
    {
        if(_attachments.Count>=3||attachment.Bytes.Length>2*1024*1024||_attachments.Sum(a=>a.Bytes.Length)+attachment.Bytes.Length>4*1024*1024){ShowStatus("最多 3 个附件，单个不超过 2 MB，总计不超过 4 MB。");return;}
        if(attachment.Type.StartsWith("image/"))
        {
            try { using var input=new MemoryStream(attachment.Bytes);using var check=new Bitmap(input);if((long)check.PixelSize.Width*check.PixelSize.Height>30000000)throw new InvalidDataException(); }
            catch {ShowStatus("图片数据无法读取，请重新复制或选择。");return;}
        }
        _attachments.Add(attachment);_state.PendingId="";Persist();RefreshAttachments();ShowStatus("附件已添加，点击发送才会上传。");
    }
    private void RefreshAttachments()
    {
        AttachmentCards.Children.Clear();foreach(var image in _previews)image.Dispose();_previews.Clear();
        foreach(var attachment in _attachments){
            var row=new Grid{ColumnDefinitions=new ColumnDefinitions("48,*,32"),ColumnSpacing=10};
            var preview=new Border{Width=48,Height=48,CornerRadius=new CornerRadius(6),Background=Avalonia.Media.Brush.Parse("#182230"),ClipToBounds=true};
            if(attachment.Type.StartsWith("image/")){
                using var stream=new MemoryStream(attachment.Bytes);var image=new Bitmap(stream);_previews.Add(image);
                preview.Child=new Image{Source=image,Width=48,Height=48,Stretch=Avalonia.Media.Stretch.Uniform};
            }else{
                preview.Child=new TextBlock{Text="LOG",FontSize=12,FontWeight=Avalonia.Media.FontWeight.SemiBold,Foreground=Avalonia.Media.Brush.Parse("#8ebcf5"),HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center};
            }
            row.Children.Add(preview);
            var info=new StackPanel{Spacing=4,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center};Grid.SetColumn(info,1);
            var name=new TextBlock{Text=attachment.Name,FontSize=13,MaxLines=1,TextTrimming=Avalonia.Media.TextTrimming.CharacterEllipsis};
            ToolTip.SetTip(name,attachment.Name);info.Children.Add(name);
            info.Children.Add(new TextBlock{Text=(attachment.Type.StartsWith("image/")?"图片":"运行日志")+" · "+Math.Max(1,Math.Ceiling(attachment.Bytes.Length/1024d))+" KB",FontSize=12,Foreground=Avalonia.Media.Brush.Parse("#9caec4")});
            row.Children.Add(info);
            var remove=new Button{Content="×",Width=30,Height=30,MinWidth=0,MinHeight=0,Padding=new Thickness(0),FontSize=20,Background=Avalonia.Media.Brushes.Transparent,Foreground=Avalonia.Media.Brush.Parse("#b5c5d8"),HorizontalContentAlignment=Avalonia.Layout.HorizontalAlignment.Center,VerticalContentAlignment=Avalonia.Layout.VerticalAlignment.Center,VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center};
            ToolTip.SetTip(remove,"移除 "+attachment.Name);Avalonia.Automation.AutomationProperties.SetName(remove,"移除 "+attachment.Name);Grid.SetColumn(remove,2);
            remove.Click+=(_,_)=>{_attachments.Remove(attachment);_state.PendingId="";Persist();RefreshAttachments();};
            row.Children.Add(remove);
            AttachmentCards.Children.Add(new Border{Background=Avalonia.Media.Brush.Parse("#242e3b"),BorderBrush=Avalonia.Media.Brush.Parse("#354457"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(7),Padding=new Thickness(10,8),Child=row});
        }
    }
}
