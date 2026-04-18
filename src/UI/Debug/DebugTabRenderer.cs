using System.Numerics;
using ImGuiNET;
using OpenTK.Graphics.OpenGL4;

public sealed partial class MainWindow
{
    private DesktopCaptureWorker? _dxgiWorker;
    private int _dxgiPreviewTexture;
    private int _dxgiPreviewWidth;
    private int _dxgiPreviewHeight;
    private int _dxgiLastPreviewFrameId;
    private byte[] _dxgiUploadBuffer = Array.Empty<byte>();
    private string _dxgiStatus = "未启动";
    private readonly RealtimePerfStats _dxgiPerfStats = new();
    private PerfSnapshot _dxgiPerfSnapshot;
    private readonly List<double> _dxgiSampleBuffer = new(256);
    private double _dxgiPreviewRefreshAccumulatorMs;
    private bool _dxgiPreviewEnabled = true;

    private int _onnxDebugSelectedModelIndex;
    private OnnxDmlWorker? _onnxWorker;
    private string _onnxStatus = "未启动";
    private OnnxInferenceSnapshot _onnxSnapshot;
    private int _onnxLastFrameId;
    private byte[] _onnxUploadBuffer = Array.Empty<byte>();
    private string? _onnxActiveModelPath;
    private int _dxgiActiveCaptureWidth;
    private int _dxgiActiveCaptureHeight;
    private bool _smartCoreVisionManaged;

    private void DrawDxgiTab()
    {
        DrawCapturePanel(
            "DXGI 屏幕捕获",
            _dxgiStatus,
            _dxgiPerfSnapshot,
            _dxgiWorker is not null,
            OnStartDxgiClicked,
            OnStopDxgiClicked,
            ref _dxgiPreviewEnabled,
            _dxgiPreviewTexture,
            _dxgiPreviewWidth,
            _dxgiPreviewHeight);

        ImGui.Separator();
        ImGui.Text("运行优先级");
        ImGui.Text($"进程优先级: {RuntimePerformance.GetProcessPriorityText()}");
        ImGui.Text($"DXGI GPU 优先级: {RuntimePerformance.DxgiGpuPriorityStatus}");

        ImGui.Separator();
        DrawOnnxDmlEpTab();

        ImGui.Separator();
        DrawDebugGamepadCombo();

        ImGui.Separator();
        DrawViGEmVirtualGamepadPanel();
    }

    private void DrawDebugGamepadCombo()
    {
        var gamepads = GetConnectedGamepadOptions();
        var hasGamepads = gamepads.Length > 0;
        _debugSelectedGamepadIndex = hasGamepads
            ? (_debugSelectedGamepadIndex >= 0 && _debugSelectedGamepadIndex < gamepads.Length ? _debugSelectedGamepadIndex : 0)
            : -1;

        ImGui.Text("输入设备(SDL3)");
        ImGui.SameLine();
        ImGui.TextDisabled(hasGamepads ? "已就绪" : "未检测到手柄");

        var style = ImGui.GetStyle();
        var refreshButtonWidth = ImGui.CalcTextSize("刷新").X + style.FramePadding.X * 2f;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - refreshButtonWidth - style.ItemSpacing.X);
        ImGui.Combo("##DebugInputDeviceCombo", ref _debugSelectedGamepadIndex, gamepads, gamepads.Length);
        ImGui.SameLine();
        if (ImGui.Button("刷新##DebugInputDeviceRefresh"))
        {
            RefreshDebugInputDevices();
        }
    }

    private void DrawOnnxDmlEpTab()
    {
        ImGui.Text("ONNX Runtime (DirectML EP) 推理调试");
        ImGui.Separator();
        ImGui.Text($"状态: {_onnxStatus}");

        if (ImGui.Button("刷新模型"))
        {
            RefreshOnnxModels();
        }

        if (_onnxModels.Count == 0)
        {
            ImGui.Text("Models 目录中未找到可用的 json+onnx 模型配置。");
            return;
        }

        ImGui.Text("模型选择");
        ImGui.SameLine();
        DrawDebugModelCombo("##DebugModelCombo");

        var selected = _onnxModels[Math.Clamp(_onnxDebugSelectedModelIndex, 0, _onnxModels.Count - 1)];
        ImGui.Text($"输入尺寸: {selected.InputWidth}x{selected.InputHeight}");
        ImGui.Text($"conf_thres: {selected.ConfThreshold:0.###}");
        ImGui.Text($"iou_thres: {selected.IouThreshold:0.###}");
        ImGui.Text($"classes: {selected.ClassesRaw}");

        if (_onnxWorker is null)
        {
            if (ImGui.Button("启动推理"))
            {
                StartOnnxInference(selected);
            }

            ImGui.SameLine();
            ImGui.TextDisabled("xywh");
        }
        else
        {
            if (ImGui.Button("停止推理"))
            {
                StopOnnxInference("已停止");
            }
        }

        ImGui.Separator();
        ImGui.Text($"推理帧率: {_onnxSnapshot.InferenceFps:0.0} fps");
        ImGui.Text($"推理耗时均值: {_onnxSnapshot.AvgInferenceMs:0.00} ms");
        ImGui.Text($"推理耗时 P95: {_onnxSnapshot.P95InferenceMs:0.00} ms");
        ImGui.Text($"推理耗时 P99: {_onnxSnapshot.P99InferenceMs:0.00} ms");
        ImGui.Text($"检测框数量: {_onnxSnapshot.DetectionCount}");
        ImGui.Text($"输出摘要: {_onnxSnapshot.OutputSummary}");
        var probe = _onnxWorker?.GetDebugProbe() ?? default;
        if (probe.HasValue)
        {
            ImGui.Text($"raw4: {probe.Raw0:0.00}, {probe.Raw1:0.00}, {probe.Raw2:0.00}, {probe.Raw3:0.00}");
            ImGui.Text($"score: {probe.Score:0.000} (obj {probe.Objectness:0.000} * cls {probe.ClassScore:0.000})");
            ImGui.TextColored(new Vector4(0.20f, 0.85f, 1.00f, 1.00f), "xywh");
            ImGui.SameLine();
            ImGui.TextUnformatted("/");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(1.00f, 0.60f, 0.20f, 1.00f), "xyxy");
            ImGui.SameLine();
            ImGui.TextUnformatted(" overlay active on preview");
        }
    }

    private void DrawViGEmVirtualGamepadPanel()
    {
        ImGui.Text("虚拟手柄(ViGEm)");
        ImGui.Separator();

        if (_viGEmMappingWorker is null)
        {
            ImGui.Text("状态: 未创建");
            if (!string.IsNullOrWhiteSpace(_smartCoreMappingState.LastError))
            {
                ImGui.Text($"错误: {_smartCoreMappingState.LastError}");
            }
            return;
        }

        ImGui.Text($"状态: {_viGEmMappingWorker.Status}");
        ImGui.SameLine();
        ImGui.TextDisabled(_viGEmMappingWorker.IsConnected ? "已连接" : "未连接");

        if (ImGui.Button("重新连接"))
        {
            _smartCoreMappingState.LastError = string.Empty;
            _viGEmMappingWorker.ConnectVirtualGamepad();
        }

        ImGui.SameLine();
        if (ImGui.Button("断开并释放"))
        {
            _smartCoreMappingState.LastError = string.Empty;
            _viGEmMappingWorker.DisconnectVirtualGamepad();
        }

        if (!string.IsNullOrWhiteSpace(_smartCoreMappingState.LastError))
        {
            ImGui.Text($"错误: {_smartCoreMappingState.LastError}");
        }
    }

    // Debug 页面模型选择必须保持独立：
    // - 不读取 Configs/*.json 中的 model 字段
    // - 不写入当前配置文件
    // - 不受“无配置文件时禁用主页模型选择”的约束
    // 该方法仅维护调试页自己的 _onnxDebugSelectedModelIndex。
    private void DrawDebugModelCombo(string id)
    {
        if (_onnxModels.Count == 0)
        {
            ImGui.BeginDisabled();
            ImGui.SetNextItemWidth(-1);
            ImGui.Combo(id, ref _onnxDebugSelectedModelIndex, "无可用模型\0");
            ImGui.EndDisabled();
            return;
        }

        _onnxDebugSelectedModelIndex = Math.Clamp(_onnxDebugSelectedModelIndex, 0, _onnxModels.Count - 1);
        var selected = _onnxModels[_onnxDebugSelectedModelIndex];

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo(id, selected.DisplayName))
        {
            for (var i = 0; i < _onnxModels.Count; i++)
            {
                var isSelected = i == _onnxDebugSelectedModelIndex;
                if (ImGui.Selectable(_onnxModels[i].DisplayName, isSelected))
                {
                    _onnxDebugSelectedModelIndex = i;
                }

                if (isSelected)
                {
                    ImGui.SetItemDefaultFocus();
                }
            }

            ImGui.EndCombo();
        }
    }

    private void StartOnnxInference(OnnxModelConfig model)
    {
        _smartCoreVisionManaged = false;
        StartOnnxInference(model, model.InputWidth, model.InputHeight, ensureCaptureRunning: true);
    }

    private void StartOnnxInference(OnnxModelConfig model, int captureWidth, int captureHeight, bool ensureCaptureRunning)
    {
        try
        {
            StopOnnxInference("重启推理");
            if (ensureCaptureRunning && _dxgiWorker is null)
            {
                StartDxgiCapture(captureWidth, captureHeight);
            }

            _dxgiWorker?.SetCaptureRegion(captureWidth, captureHeight);
            _dxgiActiveCaptureWidth = Math.Max(1, captureWidth);
            _dxgiActiveCaptureHeight = Math.Max(1, captureHeight);
            _onnxLastFrameId = 0;
            _onnxUploadBuffer = Array.Empty<byte>();
            _onnxWorker = new OnnxDmlWorker(model);
            _onnxActiveModelPath = model.OnnxPath;
            _onnxStatus = "推理中";
        }
        catch (Exception ex)
        {
            _onnxStatus = $"启动失败: {ex.GetType().Name}: {ex.Message}";
            _onnxWorker = null;
            _onnxActiveModelPath = null;
        }
    }

    private void StopOnnxInference(string status)
    {
        _onnxWorker?.Dispose();
        _onnxWorker = null;
        _onnxActiveModelPath = null;
        _onnxStatus = status;
        _onnxSnapshot = default;
    }

    private void SyncSmartCoreVisionPipeline()
    {
        if (!_smartCoreMappingState.IsEnabled)
        {
            if (_smartCoreVisionManaged)
            {
                StopOnnxInference("鏅烘収鏍稿績宸插仠姝?");
                StopDxgiCapture("鏅烘収鏍稿績宸插仠姝?");
                _smartCoreVisionManaged = false;
            }

            return;
        }

        if (!TryGetHomeSelectedModel(out var model))
        {
            if (_smartCoreVisionManaged)
            {
                StopOnnxInference("鏅烘収鏍稿績缂哄皯妯″瀷");
                StopDxgiCapture("鏅烘収鏍稿績缂哄皯妯″瀷");
            }

            _smartCoreVisionManaged = false;
            return;
        }

        var captureSize = Math.Max(1, _homeViewState.SnapOuterRange);
        var requiresDxgiRestart = _dxgiWorker is null
            || _dxgiActiveCaptureWidth != captureSize
            || _dxgiActiveCaptureHeight != captureSize;
        if (requiresDxgiRestart)
        {
            StartDxgiCapture(captureSize, captureSize);
        }
        else
        {
            _dxgiWorker?.SetCaptureRegion(captureSize, captureSize);
        }

        var requiresOnnxRestart = _onnxWorker is null
            || !string.Equals(_onnxActiveModelPath, model.OnnxPath, StringComparison.OrdinalIgnoreCase);
        if (requiresOnnxRestart)
        {
            StartOnnxInference(model, captureSize, captureSize, ensureCaptureRunning: false);
        }

        _smartCoreVisionManaged = true;
    }

    private bool TryGetHomeSelectedModel(out OnnxModelConfig model)
    {
        if (_onnxTopSelectedModelIndex >= 0 && _onnxTopSelectedModelIndex < _onnxModels.Count)
        {
            model = _onnxModels[_onnxTopSelectedModelIndex];
            return true;
        }

        model = default;
        return false;
    }

    private void PumpOnnxFromDxgi()
    {
        if (_onnxWorker is null || _dxgiWorker is null)
        {
            return;
        }

        if (_dxgiWorker.TryCopyLatestFrame(ref _onnxUploadBuffer, ref _onnxLastFrameId, out var width, out var height, out var error))
        {
            _onnxWorker.SubmitFrame(_onnxUploadBuffer, width, height, _onnxLastFrameId);
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            StopOnnxInference($"推理输入错误: {error}");
        }
    }

    private void DrawCapturePanel(
        string title,
        string status,
        PerfSnapshot perfSnapshot,
        bool isRunning,
        Action onStart,
        Action onStop,
        ref bool previewEnabled,
        int previewTexture,
        int previewWidth,
        int previewHeight)
    {
        ImGui.Text(title);
        ImGui.Separator();
        ImGui.Text($"状态: {status}");
        ImGui.Text("捕获性能统计(1秒刷新)");
        ImGui.Text($"捕获轮询频率: {perfSnapshot.CapturePollHz:0.0} Hz");
        ImGui.Text($"捕获帧率: {perfSnapshot.CapturedFps:0.0} fps");
        ImGui.Text($"捕获耗时均值: {perfSnapshot.AvgCaptureMs:0.00} ms");
        ImGui.Text($"捕获耗时 P95: {perfSnapshot.P95CaptureMs:0.00} ms");
        ImGui.Text($"捕获耗时 P99: {perfSnapshot.P99CaptureMs:0.00} ms");
        ImGui.Text($"捕获成功率: {perfSnapshot.CaptureSuccessRate:0.0}%");

        if (!isRunning)
        {
            if (ImGui.Button("启动捕获"))
            {
                onStart();
            }
        }
        else
        {
            if (ImGui.Button("停止捕获"))
            {
                onStop();
            }
        }

        ImGui.SameLine();
        ImGui.Text($"窗口大小: {ClientSize.X} x {ClientSize.Y}");
        ImGui.SameLine();
        ImGui.Checkbox($"显示预览##{title}", ref previewEnabled);

        ImGui.Separator();
        ImGui.Text("预览:");

        if (!previewEnabled)
        {
            ImGui.Text("预览已关闭");
            return;
        }

        if (previewTexture != 0 && previewWidth > 0 && previewHeight > 0)
        {
            var previewSize = new Vector2(previewWidth, previewHeight);
            var previewPos = ImGui.GetCursorScreenPos();
            ImGui.Image((IntPtr)previewTexture, previewSize, new Vector2(0, 0), new Vector2(1, 1));
            DrawOnnxDebugOverlay(previewPos, previewSize);
        }
        else
        {
            ImGui.Text("暂无画面");
        }
    }

    private void DrawOnnxDebugOverlay(Vector2 imagePos, Vector2 imageSize)
    {
        var probe = _onnxWorker?.GetDebugProbe() ?? default;
        if (!probe.HasValue || probe.InputWidth <= 0 || probe.InputHeight <= 0)
        {
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        DrawLabeledRect(drawList, BuildRectFromXywh(probe, imagePos, imageSize), ImGui.GetColorU32(new Vector4(0.20f, 0.85f, 1.00f, 1.00f)), "xywh");
        DrawLabeledRect(drawList, BuildRectFromXyxy(probe, imagePos, imageSize), ImGui.GetColorU32(new Vector4(1.00f, 0.60f, 0.20f, 1.00f)), "xyxy");
    }

    private static void DrawLabeledRect(ImDrawListPtr drawList, (Vector2 Min, Vector2 Max) rect, uint color, string label)
    {
        if (rect.Max.X <= rect.Min.X || rect.Max.Y <= rect.Min.Y)
        {
            return;
        }

        drawList.AddRect(rect.Min, rect.Max, color, 0f, ImDrawFlags.None, 2f);
        var textSize = ImGui.CalcTextSize(label);
        var textBgMax = new Vector2(rect.Min.X + textSize.X + 8f, rect.Min.Y + textSize.Y + 4f);
        drawList.AddRectFilled(rect.Min, textBgMax, color);
        drawList.AddText(new Vector2(rect.Min.X + 4f, rect.Min.Y + 2f), ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 1f)), label);
    }

    private static (Vector2 Min, Vector2 Max) BuildRectFromXywh(OnnxDebugProbe probe, Vector2 imagePos, Vector2 imageSize)
    {
        var x1 = probe.Raw0 - probe.Raw2 * 0.5f;
        var y1 = probe.Raw1 - probe.Raw3 * 0.5f;
        var x2 = probe.Raw0 + probe.Raw2 * 0.5f;
        var y2 = probe.Raw1 + probe.Raw3 * 0.5f;
        return MapRectToPreview(x1, y1, x2, y2, probe, imagePos, imageSize);
    }

    private static (Vector2 Min, Vector2 Max) BuildRectFromXyxy(OnnxDebugProbe probe, Vector2 imagePos, Vector2 imageSize)
    {
        return MapRectToPreview(probe.Raw0, probe.Raw1, probe.Raw2, probe.Raw3, probe, imagePos, imageSize);
    }

    private static (Vector2 Min, Vector2 Max) MapRectToPreview(float x1, float y1, float x2, float y2, OnnxDebugProbe probe, Vector2 imagePos, Vector2 imageSize)
    {
        var minX = MathF.Min(x1, x2);
        var minY = MathF.Min(y1, y2);
        var maxX = MathF.Max(x1, x2);
        var maxY = MathF.Max(y1, y2);
        var scaleX = imageSize.X / probe.InputWidth;
        var scaleY = imageSize.Y / probe.InputHeight;

        minX = Math.Clamp(minX, 0f, probe.InputWidth);
        minY = Math.Clamp(minY, 0f, probe.InputHeight);
        maxX = Math.Clamp(maxX, 0f, probe.InputWidth);
        maxY = Math.Clamp(maxY, 0f, probe.InputHeight);

        return (
            imagePos + new Vector2(minX * scaleX, minY * scaleY),
            imagePos + new Vector2(maxX * scaleX, maxY * scaleY));
    }

    private void OnStartDxgiClicked()
    {
        _smartCoreVisionManaged = false;
        StartDxgiCapture();
    }

    private void OnStopDxgiClicked()
    {
        _smartCoreVisionManaged = false;
        StopDxgiCapture("已停止");
    }

    private void StartDxgiCapture()
    {
        StartDxgiCapture(320, 320);
    }

    private void StartDxgiCapture(int captureWidth, int captureHeight)
    {
        try
        {
            StopDxgiCapture("重启捕获");
            _dxgiWorker = new DesktopCaptureWorker();
            _dxgiWorker.SetCaptureRegion(captureWidth, captureHeight);
            _dxgiActiveCaptureWidth = Math.Max(1, captureWidth);
            _dxgiActiveCaptureHeight = Math.Max(1, captureHeight);
            _dxgiStatus = "捕获中";
            _dxgiPerfStats.Reset();
            _dxgiPerfSnapshot = default;
            _dxgiLastPreviewFrameId = 0;
            _dxgiPreviewRefreshAccumulatorMs = 0.0;
        }
        catch (Exception ex)
        {
            _dxgiStatus = $"启动失败: {ex.Message}";
            _dxgiWorker = null;
        }
    }

    private void StopDxgiCapture(string status)
    {
        _dxgiWorker?.Dispose();
        _dxgiWorker = null;
        _dxgiStatus = status;
        _dxgiPreviewRefreshAccumulatorMs = 0.0;
        _dxgiLastPreviewFrameId = 0;
        _dxgiUploadBuffer = Array.Empty<byte>();
        _dxgiPreviewWidth = 0;
        _dxgiPreviewHeight = 0;
        _dxgiActiveCaptureWidth = 0;
        _dxgiActiveCaptureHeight = 0;
        if (_dxgiPreviewTexture != 0)
        {
            GL.DeleteTexture(_dxgiPreviewTexture);
            _dxgiPreviewTexture = 0;
        }
    }

    private void UpdateDxgiPreview(float frameDeltaSeconds)
    {
        if (_dxgiWorker is null)
        {
            return;
        }

        _dxgiPreviewRefreshAccumulatorMs += Math.Max(frameDeltaSeconds, 0f) * 1000.0;
        if (_dxgiPreviewRefreshAccumulatorMs < 20.0)
        {
            return;
        }
        _dxgiPreviewRefreshAccumulatorMs = 0.0;

        if (_dxgiWorker.TryCopyLatestFrame(ref _dxgiUploadBuffer, ref _dxgiLastPreviewFrameId, out var width, out var height, out var error))
        {
            EnsureDxgiPreviewTexture(width, height);
            GL.BindTexture(TextureTarget.Texture2D, _dxgiPreviewTexture);
            GL.TexSubImage2D(
                TextureTarget.Texture2D,
                0,
                0,
                0,
                width,
                height,
                OpenTK.Graphics.OpenGL4.PixelFormat.Bgra,
                PixelType.UnsignedByte,
                _dxgiUploadBuffer);
        }
        else if (!string.IsNullOrWhiteSpace(error))
        {
            StopDxgiCapture($"捕获错误: {error}");
        }
    }

    private void EnsureDxgiPreviewTexture(int width, int height)
    {
        if (_dxgiPreviewTexture != 0 && width == _dxgiPreviewWidth && height == _dxgiPreviewHeight)
        {
            return;
        }

        if (_dxgiPreviewTexture != 0)
        {
            GL.DeleteTexture(_dxgiPreviewTexture);
            _dxgiPreviewTexture = 0;
        }

        _dxgiPreviewTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, _dxgiPreviewTexture);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(
            TextureTarget.Texture2D,
            0,
            PixelInternalFormat.Rgba,
            width,
            height,
            0,
            OpenTK.Graphics.OpenGL4.PixelFormat.Bgra,
            PixelType.UnsignedByte,
            IntPtr.Zero);

        _dxgiPreviewWidth = width;
        _dxgiPreviewHeight = height;
    }
}

internal readonly struct PerfSnapshot
{
    public readonly double CapturePollHz;
    public readonly double CapturedFps;
    public readonly double AvgCaptureMs;
    public readonly double P95CaptureMs;
    public readonly double P99CaptureMs;
    public readonly double CaptureSuccessRate;

    public PerfSnapshot(
        double capturePollHz,
        double capturedFps,
        double avgCaptureMs,
        double p95CaptureMs,
        double p99CaptureMs,
        double captureSuccessRate)
    {
        CapturePollHz = capturePollHz;
        CapturedFps = capturedFps;
        AvgCaptureMs = avgCaptureMs;
        P95CaptureMs = p95CaptureMs;
        P99CaptureMs = p99CaptureMs;
        CaptureSuccessRate = captureSuccessRate;
    }
}

internal sealed class RealtimePerfStats
{
    private double _windowSeconds;

    private long _capturePollCount;
    private long _captureSuccessCount;
    private double _captureMsSum;
    private readonly List<double> _captureMsSamples = new(2000);
    private CaptureTelemetry _lastTelemetry;
    private bool _hasLastTelemetry;

    public void Reset()
    {
        _windowSeconds = 0;
        _capturePollCount = 0;
        _captureSuccessCount = 0;
        _captureMsSum = 0;
        _captureMsSamples.Clear();
        _lastTelemetry = default;
        _hasLastTelemetry = false;
    }

    public void PushSample(float deltaTimeSeconds, CaptureTelemetry telemetry, List<double> captureSamples)
    {
        var clampedDelta = Math.Max(deltaTimeSeconds, 1f / 1000f);
        _windowSeconds += clampedDelta;
        for (var i = 0; i < captureSamples.Count; i++)
        {
            _captureMsSamples.Add(captureSamples[i]);
        }

        if (!_hasLastTelemetry)
        {
            _lastTelemetry = telemetry;
            _hasLastTelemetry = true;
            return;
        }

        var pollDelta = telemetry.PollCount - _lastTelemetry.PollCount;
        var successDelta = telemetry.SuccessCount - _lastTelemetry.SuccessCount;
        var captureMsDelta = telemetry.TotalCaptureMs - _lastTelemetry.TotalCaptureMs;
        if (pollDelta > 0)
        {
            _capturePollCount += pollDelta;
            _captureSuccessCount += Math.Max(0, successDelta);
            _captureMsSum += Math.Max(0.0, captureMsDelta);
        }

        _lastTelemetry = telemetry;
    }

    public bool TryBuildSnapshot(out PerfSnapshot snapshot)
    {
        if (_windowSeconds < 1.0)
        {
            snapshot = default;
            return false;
        }

        var capturePollHz = _capturePollCount / _windowSeconds;
        var capturedFps = _captureSuccessCount / _windowSeconds;
        var avgCaptureMs = _captureSuccessCount > 0 ? _captureMsSum / _captureSuccessCount : 0.0;
        var p95CaptureMs = Percentile(_captureMsSamples, 0.95);
        var p99CaptureMs = Percentile(_captureMsSamples, 0.99);
        var successRate = _capturePollCount > 0
            ? (double)_captureSuccessCount / _capturePollCount * 100.0
            : 0.0;

        snapshot = new PerfSnapshot(
            capturePollHz,
            capturedFps,
            avgCaptureMs,
            p95CaptureMs,
            p99CaptureMs,
            successRate);

        Reset();
        return true;
    }

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0.0;
        }

        values.Sort();
        var rank = percentile * (values.Count - 1);
        var low = (int)Math.Floor(rank);
        var high = (int)Math.Ceiling(rank);
        if (low == high)
        {
            return values[low];
        }

        var weight = rank - low;
        return values[low] * (1.0 - weight) + values[high] * weight;
    }
}
