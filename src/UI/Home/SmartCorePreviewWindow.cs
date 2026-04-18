using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

public sealed partial class MainWindow
{
    private readonly object _smartCorePreviewWindowLock = new();
    private System.Windows.Forms.Form? _smartCorePreviewWindow;
    private bool _smartCorePreviewShuttingDown;

    private void OpenSmartCorePreviewWindow()
    {
        lock (_smartCorePreviewWindowLock)
        {
            if (_smartCorePreviewWindow is not null && !_smartCorePreviewWindow.IsDisposed)
            {
                _smartCorePreviewWindow.BeginInvoke(new Action(() =>
                {
                    _smartCorePreviewWindow.Show();
                    _smartCorePreviewWindow.Activate();
                    _smartCorePreviewWindow.BringToFront();
                }));
                return;
            }

            var previewWindowThread = new Thread(() =>
            {
                var initialSize = Math.Max(1, _homeViewState.SnapOuterRange);
                using var form = new System.Windows.Forms.Form
                {
                    Text = "智慧核心预览",
                    StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
                    ClientSize = new System.Drawing.Size(initialSize, initialSize),
                    TopMost = true,
                    FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle,
                    MaximizeBox = false,
                    MinimizeBox = false
                };
                form.SizeGripStyle = System.Windows.Forms.SizeGripStyle.Hide;
                form.BackColor = System.Drawing.Color.FromArgb(18, 20, 24);
                form.ShowInTaskbar = false;
                form.Shown += (_, _) =>
                {
                    form.MinimumSize = form.Size;
                    form.MaximumSize = form.Size;
                    form.TopMost = true;
                };

                var frameBuffer = Array.Empty<byte>();
                var lastFrameId = 0;
                var frameWidth = 0;
                var frameHeight = 0;
                string? frameError = null;

                var refreshTimer = new System.Windows.Forms.Timer { Interval = 50 };
                refreshTimer.Tick += (_, _) =>
                {
                    var targetSize = Math.Max(1, _homeViewState.SnapOuterRange);
                    var expectedClientSize = new System.Drawing.Size(targetSize, targetSize);
                    if (form.ClientSize != expectedClientSize)
                    {
                        form.ClientSize = expectedClientSize;
                        form.MinimumSize = form.Size;
                        form.MaximumSize = form.Size;
                    }

                    var worker = _dxgiWorker;
                    if (worker is not null)
                    {
                        worker.TryCopyLatestFrame(ref frameBuffer, ref lastFrameId, out frameWidth, out frameHeight, out frameError);
                    }
                    else
                    {
                        frameWidth = 0;
                        frameHeight = 0;
                    }

                    form.Invalidate();
                };

                form.Paint += (_, e) =>
                {
                    e.Graphics.Clear(System.Drawing.Color.FromArgb(18, 20, 24));
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

                    if (frameWidth <= 0 || frameHeight <= 0 || frameBuffer.Length != frameWidth * frameHeight * 4)
                    {
                        var statusText = string.IsNullOrWhiteSpace(frameError) ? "等待捕获画面..." : $"捕获错误: {frameError}";
                        using var statusBrush = new System.Drawing.SolidBrush(System.Drawing.Color.Gainsboro);
                        e.Graphics.DrawString(statusText, form.Font, statusBrush, new System.Drawing.PointF(12f, 12f));
                        return;
                    }

                    var clientRect = new System.Drawing.Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height);
                    var scale = Math.Min(clientRect.Width / (float)frameWidth, clientRect.Height / (float)frameHeight);
                    scale = Math.Max(scale, 1f);
                    var drawWidth = Math.Max(1, (int)MathF.Round(frameWidth * scale));
                    var drawHeight = Math.Max(1, (int)MathF.Round(frameHeight * scale));
                    var drawRect = new System.Drawing.Rectangle(
                        clientRect.X + (clientRect.Width - drawWidth) / 2,
                        clientRect.Y + (clientRect.Height - drawHeight) / 2,
                        drawWidth,
                        drawHeight);

                    using var bitmap = new System.Drawing.Bitmap(frameWidth, frameHeight, PixelFormat.Format32bppArgb);
                    var bitmapData = bitmap.LockBits(
                        new System.Drawing.Rectangle(0, 0, frameWidth, frameHeight),
                        ImageLockMode.WriteOnly,
                        PixelFormat.Format32bppArgb);
                    try
                    {
                        Marshal.Copy(frameBuffer, 0, bitmapData.Scan0, frameBuffer.Length);
                    }
                    finally
                    {
                        bitmap.UnlockBits(bitmapData);
                    }

                    e.Graphics.DrawImage(bitmap, drawRect);

                    var probe = _onnxWorker?.GetDebugProbe() ?? default;
                    if (probe.HasValue && probe.InputWidth > 0 && probe.InputHeight > 0)
                    {
                        var x1 = probe.Raw0 - probe.Raw2 * 0.5f;
                        var y1 = probe.Raw1 - probe.Raw3 * 0.5f;
                        var x2 = probe.Raw0 + probe.Raw2 * 0.5f;
                        var y2 = probe.Raw1 + probe.Raw3 * 0.5f;

                        var minX = Math.Clamp(MathF.Min(x1, x2), 0f, probe.InputWidth);
                        var minY = Math.Clamp(MathF.Min(y1, y2), 0f, probe.InputHeight);
                        var maxX = Math.Clamp(MathF.Max(x1, x2), 0f, probe.InputWidth);
                        var maxY = Math.Clamp(MathF.Max(y1, y2), 0f, probe.InputHeight);

                        var overlayRect = new System.Drawing.RectangleF(
                            drawRect.Left + minX / probe.InputWidth * drawRect.Width,
                            drawRect.Top + minY / probe.InputHeight * drawRect.Height,
                            (maxX - minX) / probe.InputWidth * drawRect.Width,
                            (maxY - minY) / probe.InputHeight * drawRect.Height);

                        if (overlayRect.Width > 1f && overlayRect.Height > 1f)
                        {
                            using var boxPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0, 216, 255), 2f);
                            using var labelBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0, 216, 255));
                            using var textBrush = new System.Drawing.SolidBrush(System.Drawing.Color.Black);
                            e.Graphics.DrawRectangle(boxPen, overlayRect.X, overlayRect.Y, overlayRect.Width, overlayRect.Height);

                            const string label = "target";
                            var textSize = e.Graphics.MeasureString(label, form.Font);
                            var labelRect = new System.Drawing.RectangleF(
                                overlayRect.X,
                                Math.Max(drawRect.Top, overlayRect.Y - textSize.Height - 4f),
                                textSize.Width + 8f,
                                textSize.Height + 2f);
                            e.Graphics.FillRectangle(labelBrush, labelRect);
                            e.Graphics.DrawString(label, form.Font, textBrush, labelRect.X + 4f, labelRect.Y + 1f);
                        }
                    }
                };

                form.FormClosing += (_, e) =>
                {
                    if (!_smartCorePreviewShuttingDown && e.CloseReason == System.Windows.Forms.CloseReason.UserClosing)
                    {
                        e.Cancel = true;
                        form.Hide();
                    }
                };

                form.FormClosed += (_, _) =>
                {
                    refreshTimer.Stop();
                    refreshTimer.Dispose();
                    lock (_smartCorePreviewWindowLock)
                    {
                        _smartCorePreviewWindow = null;
                    }
                };

                lock (_smartCorePreviewWindowLock)
                {
                    _smartCorePreviewWindow = form;
                }

                refreshTimer.Start();
                form.Show();
                System.Windows.Forms.Application.Run(form);
            })
            {
                IsBackground = true,
                Name = "SmartCorePreviewWindowThread"
            };
            previewWindowThread.SetApartmentState(ApartmentState.STA);
            previewWindowThread.Start();
        }
    }

    private void CloseSmartCorePreviewWindow()
    {
        lock (_smartCorePreviewWindowLock)
        {
            if (_smartCorePreviewWindow is not null && !_smartCorePreviewWindow.IsDisposed)
            {
                _smartCorePreviewShuttingDown = true;
                _smartCorePreviewWindow.BeginInvoke(new Action(() => _smartCorePreviewWindow.Close()));
            }
        }
    }
}
