using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ReExtractor.Gui;

public partial class MainWindow
{
    // Explicit CLI replay for reproducing animation previews without desktop input.
    private sealed class PreviewSession
    {
        public string ListPath { get; set; } = "";
        public string GameDirectory { get; set; } = "";
        public string[] MeshPaths { get; set; } = [];
        public string MotionListPath { get; set; } = "";
        public int BaseId { get; set; }
        public int? AdditiveId { get; set; }

    }

    private async Task LoadPreviewSessionAsync(string sessionPath)
    {
        try
        {
            var session = JsonSerializer.Deserialize<PreviewSession>(await File.ReadAllTextAsync(sessionPath))
                ?? throw new InvalidDataException("预览配置为空");
            if (session.MeshPaths.Length == 0 || !Directory.Exists(session.GameDirectory))
                throw new InvalidDataException("预览配置中的模型或目录无效");
            var managedPath = await new FileListManagerService().ImportAsync(session.ListPath);
            RefreshManagedLists(managedPath);
            GameDirBox.Text = session.GameDirectory;
            await LoadPakFilesAsync(FindPakFiles(session.GameDirectory));
            if (_pak == null) throw new InvalidDataException(ActionStatus.Text);
            await LoadModelPathsAsync(session.MeshPaths);
            if (!Viewport.HasMesh || _previewMeshPaths.Count != session.MeshPaths.Length)
                throw new InvalidDataException("模型未完整加载：" + ActionStatus.Text);
            var (_, motions) = await LoadMotionListAsync(session.MotionListPath, 0);
            var index = motions.ToList().FindIndex(m => m.MotionNumber == session.BaseId);
            if (index < 0) throw new InvalidDataException("找不到基础动作编号 " + session.BaseId);
            SetMotionListUi(session.MotionListPath, motions, index);
            if (SelectedBaseMotion?.MotionNumber != session.BaseId)
                throw new InvalidDataException("指定轨道已被用途筛选排除，请在高级原始轨道检查中查看；不会替换成其他动作：" + session.BaseId);
            _syncingMotionUi = true;
            try
            {
                if (session.AdditiveId is { } addId)
                {
                    var addIndex = _relatedPreviewMotions.ToList().FindIndex(m => m.MotionNumber == addId);
                    if (addIndex < 0) throw new InvalidDataException("找不到附加动作编号 " + addId);
                    AdditiveMotionCombo.SelectedIndex = addIndex + 1;
                }
                else AdditiveMotionCombo.SelectedIndex = 0;
                // Selecting an additive clip applies it fully; no separate strength control.
            }
            finally { _syncingMotionUi = false; }
            UpdateBlendPreviewDescription();
            await LoadSelectedMotionAsync(null);
            if (!Viewport.HasAnimation || _loadedMotionSource != SelectedBaseMotion?.SourceIndex
                || _loadedAdditiveSource != (SelectedAdditiveMotion?.SourceIndex ?? -1))
                throw new InvalidDataException("动画未加载：" + ActionStatus.Text);
            Title += " · 动画分层本地测试";
            await File.WriteAllTextAsync(sessionPath + ".result.json", JsonSerializer.Serialize(new {
                Ready = true, BaseId = SelectedBaseMotion?.MotionNumber,
                AdditiveId = SelectedAdditiveMotion?.MotionNumber,
                Meshes = _previewMeshPaths, Playing = Viewport.IsPlaying, Viewport.Duration,
                Status = ActionStatus.Text, Profile = BlendPreviewDescription.Text
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ActionStatus.Text = "预览配置加载失败：" + ex.Message;
            try { await File.WriteAllTextAsync(sessionPath + ".result.json",
                JsonSerializer.Serialize(new { Ready = false, Error = ex.ToString() })); }
            catch (IOException) { }
        }
    }
}
