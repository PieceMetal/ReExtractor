using System.Linq;
using System.IO;
using System.Collections.Generic;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace ReExtractor.Gui;

public static class FeedbackService
{
    public sealed record PublicComment(string Id,string Message,string Nickname,DateTimeOffset CreatedAt);
    public sealed record PublicPage(List<PublicComment> Items,int Page,int Total);
    public static async Task<PublicPage> ReadCommentsAsync(int page){
        using var response=await Client.GetAsync(Endpoint.Replace("/feedback","/comments")+"?page="+page);
        response.EnsureSuccessStatusCode();using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root=data.RootElement;var items=new List<PublicComment>();
        foreach(var x in root.GetProperty("items").EnumerateArray())items.Add(new(x.GetProperty("id").GetString()!,x.GetProperty("message").GetString()!,x.GetProperty("nickname").GetString()??"R友",DateTimeOffset.FromUnixTimeMilliseconds(x.GetProperty("created_at").GetInt64())));
        return new(items,root.GetProperty("page").GetInt32(),root.GetProperty("total").GetInt32());
    }
    private static readonly Lazy<string> DeviceId = new(() => {
        var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ReExtractor");
        Directory.CreateDirectory(directory);
        var file=Path.Combine(directory,"feedback-device-id.txt");
        if(File.Exists(file)&&Guid.TryParse(File.ReadAllText(file).Trim(),out var existing))return existing.ToString();
        var id=Guid.NewGuid().ToString();File.WriteAllText(file,id);return id;
    });
    public static string UserName => "R友 " + (Convert.ToUInt64(DeviceId.Value.Replace("-", "")[..12], 16) % 100000000).ToString("D8");
    public const string Endpoint = "https://reextractor-feedback.qq594357260.chatgpt.site/api/feedback";
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(25) };
    public static async Task ReportMirrorAsync(IEnumerable<(string Id,string Message)> items)
    {
        using var response=await Client.PostAsJsonAsync(Endpoint.Replace("/feedback","/comment-mirror"),new {
            deviceId=DeviceId.Value,version=typeof(FeedbackService).Assembly.GetName().Version?.ToString(3)??"",
            items=items.Select(x=>new{id=x.Id,message=x.Message}).ToArray()
        });
        response.EnsureSuccessStatusCode();
    }

    public static async Task<HashSet<string>> HiddenIdsAsync(IReadOnlyList<string> ids){
        var hidden=new HashSet<string>();
        foreach(var chunk in ids.Chunk(100)){
            using var response=await Client.PostAsJsonAsync(Endpoint+"/visibility",new{deviceId=DeviceId.Value,ids=chunk});response.EnsureSuccessStatusCode();
            using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach(var item in data.RootElement.GetProperty("hiddenIds").EnumerateArray())if(item.GetString() is string id)hidden.Add(id);
        }return hidden;
    }
    public static async Task<HashSet<string>> HiddenMessagesAsync(IReadOnlyList<string> messages)
    {
        var hidden=new HashSet<string>();
        foreach(var chunk in messages.Chunk(100)){
            using var response=await Client.PostAsJsonAsync(Endpoint+"/visibility",new{deviceId=DeviceId.Value,messages=chunk});
            response.EnsureSuccessStatusCode();
            using var data=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach(var item in data.RootElement.GetProperty("hidden").EnumerateArray())if(item.GetString() is string text)hidden.Add(text);
        }
        return hidden;
    }
    public static async Task SendAsync(string id, string message, IReadOnlyList<FeedbackAttachment>? files = null)
    {
        var category = message.StartsWith("【功能建议】") ? "功能建议" : message.StartsWith("【导出失败】") ? "导出异常" : "使用问题";
        using var response = await Client.PostAsJsonAsync(Endpoint, new
        {
            id, message, category, deviceId=DeviceId.Value, nickname=UserName,
            attachments = (files ?? System.Array.Empty<FeedbackAttachment>()).Select(a => new { name = a.Name, type = a.Type, data = Convert.ToBase64String(a.Bytes) }).ToArray(),
            version = typeof(FeedbackService).Assembly.GetName().Version?.ToString(3) ?? ""
        });
        if (!response.IsSuccessStatusCode)
        {
            var error=response.StatusCode==System.Net.HttpStatusCode.Forbidden?"此设备已被禁止提交反馈。":(int)response.StatusCode==426?"请更新工具后再发送反馈。":$"发送失败（{(int)response.StatusCode}），请稍后重试。";
            throw new InvalidOperationException(error);
        }
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!result.RootElement.TryGetProperty("received", out var received) || received.ValueKind != JsonValueKind.True
            || !result.RootElement.TryGetProperty("id", out var returnedId) || returnedId.GetString() != id)
            throw new InvalidOperationException("未收到有效确认，请稍后重试。");
    }
}
