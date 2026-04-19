using System.Diagnostics;
using System.Threading;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

internal readonly record struct ViGEmMappingSnapshot(
    bool IsConnected,
    bool RequestedEnabled,
    bool IsMappingActive,
    uint? SelectedInstanceId,
    string? LastError);

internal sealed class ViGEmMappingWorker : IDisposable
{
    private const double TargetLoopIntervalMs = 1000.0 / 500.0;
    private readonly object _sync = new();
    private readonly Thread _thread;
    private readonly SmartCoreAimAssistService _smartCoreAimAssistService = new();
    private bool _running = true;
    private SdlGamepadWorker? _sdlGamepadWorker;
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;
    private bool _isConnected;
    private bool _requestedEnabled;
    private bool _isMappingActive;
    private bool _hasSelectedGamepad;
    private uint _selectedGamepadInstanceId;
    private string _status = "未初始化";
    private string? _lastError;
    private SmartCoreAimAssistConfigState _aimAssistConfigState = SmartCoreAimAssistConfigState.Disabled;
    private SmartCoreDetectionState _aimAssistDetectionState = SmartCoreDetectionState.Empty;

    public ViGEmMappingWorker()
    {
        _thread = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "ViGEm-Mapping-Worker"
        };
        _thread.Start();
    }

    public void SetSdlGamepadWorker(SdlGamepadWorker? sdlGamepadWorker)
    {
        lock (_sync)
        {
            _sdlGamepadWorker = sdlGamepadWorker;
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _isConnected;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public void ConnectVirtualGamepad()
    {
        lock (_sync)
        {
            if (_isConnected)
            {
                _status = "已连接";
                return;
            }

            try
            {
                _client ??= new ViGEmClient();
                _controller ??= _client.CreateXbox360Controller();
                _controller.Connect();
                _isConnected = true;
                _status = "已连接";
                _lastError = null;
            }
            catch (Exception ex)
            {
                _isConnected = false;
                _status = $"连接失败: {ex.GetType().Name}: {ex.Message}";
                _lastError = _status;
                SafeDisposeController();
                SafeDisposeClient();
            }
        }
    }

    public void DisconnectVirtualGamepad()
    {
        lock (_sync)
        {
            if (!_isConnected && _controller is null)
            {
                _status = "已断开";
                return;
            }

            try
            {
                _controller?.Disconnect();
            }
            catch (Exception ex)
            {
                _status = $"断开失败: {ex.GetType().Name}: {ex.Message}";
                _lastError = _status;
            }
            finally
            {
                _isConnected = false;
                SafeDisposeController();
                SafeDisposeClient();
                _status = "已断开";
            }
        }
    }

    public string? GetLastError()
    {
        lock (_sync)
        {
            return _lastError;
        }
    }

    public ViGEmMappingSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return new ViGEmMappingSnapshot(
                _isConnected,
                _requestedEnabled,
                _isMappingActive,
                _hasSelectedGamepad ? _selectedGamepadInstanceId : null,
                _lastError);
        }
    }

    public void SetRequestedEnabled(bool requestedEnabled)
    {
        lock (_sync)
        {
            _requestedEnabled = requestedEnabled;
            if (!requestedEnabled)
            {
                _isMappingActive = false;
            }
        }
    }

    public void SetSelectedGamepad(uint? instanceId)
    {
        SdlGamepadWorker? sdlGamepadWorker;
        lock (_sync)
        {
            _hasSelectedGamepad = instanceId.HasValue;
            _selectedGamepadInstanceId = instanceId ?? 0;
            if (!_hasSelectedGamepad)
            {
                _isMappingActive = false;
            }

            sdlGamepadWorker = _sdlGamepadWorker;
        }

        sdlGamepadWorker?.SetSelectedGamepad(instanceId);
    }

    public void SetAimAssistConfig(in SmartCoreAimAssistConfigState state)
    {
        lock (_sync)
        {
            _aimAssistConfigState = state;
        }
    }

    public void SetAimAssistDetections(in SmartCoreDetectionState state)
    {
        lock (_sync)
        {
            _aimAssistDetectionState = state;
        }
    }

    private void WorkerMain()
    {
        var loopTimer = Stopwatch.StartNew();
        var nextLoopAtMs = 0.0;
        while (_running)
        {
            WaitForNextTick(loopTimer, ref nextLoopAtMs, TargetLoopIntervalMs);
            SdlGamepadWorker? sdlWorker;
            bool isConnected;
            bool requestedEnabled;
            bool hasSelectedGamepad;
            SmartCoreAimAssistConfigState aimAssistConfigState;
            SmartCoreDetectionState aimAssistDetectionState;
            lock (_sync)
            {
                sdlWorker = _sdlGamepadWorker;
                isConnected = _isConnected;
                requestedEnabled = _requestedEnabled;
                hasSelectedGamepad = _hasSelectedGamepad;
                aimAssistConfigState = _aimAssistConfigState;
                aimAssistDetectionState = _aimAssistDetectionState;
            }

            if (sdlWorker is null || !isConnected || !requestedEnabled || !hasSelectedGamepad)
            {
                lock (_sync)
                {
                    _isMappingActive = false;
                }
                continue;
            }

            if (!sdlWorker.TryGetLatestInput(out var input, out var sdlError))
            {
                if (!string.IsNullOrWhiteSpace(sdlError))
                {
                    lock (_sync)
                    {
                        _isMappingActive = false;
                        _lastError = sdlError;
                    }
                }

                continue;
            }

            var aimAssistResult = _smartCoreAimAssistService.Evaluate(new SmartCoreAimAssistContext(
                aimAssistConfigState.IsEnabled,
                aimAssistConfigState.IsMappingActive,
                aimAssistConfigState.SnapModeIndex,
                aimAssistConfigState.SnapOuterRange,
                aimAssistConfigState.SnapInnerRange,
                aimAssistConfigState.SnapOuterStrength,
                aimAssistConfigState.SnapInnerStrength,
                aimAssistConfigState.SnapStartStrength,
                aimAssistConfigState.SnapVerticalStrengthFactor,
                aimAssistConfigState.SnapHipfireStrengthFactor,
                aimAssistConfigState.SnapHeight,
                aimAssistConfigState.SnapInnerInterpolationTypeIndex,
                input,
                aimAssistDetectionState.Boxes));

            if (!TrySubmitState(
                    input.LeftX,
                    InvertStickY(input.LeftY),
                    CombineStickAxis(input.RightX, aimAssistResult.IsActive ? aimAssistResult.RightX : (short)0),
                    InvertStickY(CombineStickAxis(input.RightY, aimAssistResult.IsActive ? aimAssistResult.RightY : (short)0)),
                    ToXboxTrigger(input.LeftTrigger),
                    ToXboxTrigger(input.RightTrigger),
                    input.A,
                    input.B,
                    input.X,
                    input.Y,
                    input.Back,
                    input.Start,
                    input.Guide,
                    input.LeftShoulder,
                    input.RightShoulder,
                    input.LeftThumb,
                    input.RightThumb,
                    input.DpadUp,
                    input.DpadDown,
                    input.DpadLeft,
                    input.DpadRight,
                    out var mapError))
            {
                lock (_sync)
                {
                    _lastError = mapError;
                }
            }
            else
            {
                lock (_sync)
                {
                    _isMappingActive = true;
                    _lastError = null;
                }
            }

        }
    }

    private bool TrySubmitState(
        short leftX,
        short leftY,
        short rightX,
        short rightY,
        byte leftTrigger,
        byte rightTrigger,
        bool a,
        bool b,
        bool x,
        bool y,
        bool back,
        bool start,
        bool guide,
        bool leftShoulder,
        bool rightShoulder,
        bool leftThumb,
        bool rightThumb,
        bool dpadUp,
        bool dpadDown,
        bool dpadLeft,
        bool dpadRight,
        out string error)
    {
        lock (_sync)
        {
            error = string.Empty;
            if (!_isConnected || _controller is null)
            {
                error = "虚拟手柄未连接";
                return false;
            }

            try
            {
                _controller.SetAxisValue(Xbox360Axis.LeftThumbX, leftX);
                _controller.SetAxisValue(Xbox360Axis.LeftThumbY, leftY);
                _controller.SetAxisValue(Xbox360Axis.RightThumbX, rightX);
                _controller.SetAxisValue(Xbox360Axis.RightThumbY, rightY);
                _controller.SetSliderValue(Xbox360Slider.LeftTrigger, leftTrigger);
                _controller.SetSliderValue(Xbox360Slider.RightTrigger, rightTrigger);

                _controller.SetButtonState(Xbox360Button.A, a);
                _controller.SetButtonState(Xbox360Button.B, b);
                _controller.SetButtonState(Xbox360Button.X, x);
                _controller.SetButtonState(Xbox360Button.Y, y);
                _controller.SetButtonState(Xbox360Button.Back, back);
                _controller.SetButtonState(Xbox360Button.Start, start);
                _controller.SetButtonState(Xbox360Button.Guide, guide);
                _controller.SetButtonState(Xbox360Button.LeftShoulder, leftShoulder);
                _controller.SetButtonState(Xbox360Button.RightShoulder, rightShoulder);
                _controller.SetButtonState(Xbox360Button.LeftThumb, leftThumb);
                _controller.SetButtonState(Xbox360Button.RightThumb, rightThumb);
                _controller.SetButtonState(Xbox360Button.Up, dpadUp);
                _controller.SetButtonState(Xbox360Button.Down, dpadDown);
                _controller.SetButtonState(Xbox360Button.Left, dpadLeft);
                _controller.SetButtonState(Xbox360Button.Right, dpadRight);
                _controller.SubmitReport();
                return true;
            }
            catch (Exception ex)
            {
                error = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }
    }

    private static byte ToXboxTrigger(short raw)
    {
        var clamped = Math.Clamp((int)raw, 0, short.MaxValue);
        return (byte)(clamped * byte.MaxValue / short.MaxValue);
    }

    private static short CombineStickAxis(short baseValue, short offset)
    {
        var combined = (int)baseValue + offset;
        return (short)Math.Clamp(combined, short.MinValue, short.MaxValue);
    }

    private static short InvertStickY(short raw)
    {
        var inverted = -(int)raw;
        return (short)Math.Clamp(inverted, short.MinValue, short.MaxValue);
    }

    public void Dispose()
    {
        _running = false;
        if (_thread.IsAlive)
        {
            _thread.Join(500);
        }

        DisconnectVirtualGamepad();
    }

    private void SafeDisposeController()
    {
        try
        {
            // IXbox360Controller 未公开 Dispose；断开连接即可。
        }
        catch
        {
            // ignore
        }
        finally
        {
            _controller = null;
        }
    }

    private void SafeDisposeClient()
    {
        try
        {
            _client?.Dispose();
        }
        catch
        {
            // ignore
        }
        finally
        {
            _client = null;
        }
    }

    private static void WaitForNextTick(Stopwatch loopTimer, ref double nextLoopAtMs, double intervalMs)
    {
        if (nextLoopAtMs <= 0.0)
        {
            nextLoopAtMs = loopTimer.Elapsed.TotalMilliseconds;
        }

        nextLoopAtMs += intervalMs;
        while (true)
        {
            var remainingMs = nextLoopAtMs - loopTimer.Elapsed.TotalMilliseconds;
            if (remainingMs <= 0.0)
            {
                break;
            }

            if (remainingMs >= 1.5)
            {
                Thread.Sleep(1);
                continue;
            }

            Thread.SpinWait(64);
        }
    }
}

