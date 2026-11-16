using System.Runtime.InteropServices;
using System.Windows;
using WpfClipboard = System.Windows.Clipboard;

namespace DesktopBox.Services;

/// <summary>监听系统剪切板变化（AddClipboardFormatListener）。</summary>
internal sealed class ClipboardWatcher : IDisposable
{
    public const int WmClipboardUpdate = 0x031D;

    private IntPtr _hwnd;
    private bool _listening;
    private bool _disposed;

    public event EventHandler<ClipboardNotice>? Changed;

    public void Attach(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero)
            return;
        if (_listening && _hwnd == hwnd)
            return;

        Detach();
        _hwnd = hwnd;
        if (AddClipboardFormatListener(hwnd))
            _listening = true;
    }

    public void Detach()
    {
        if (!_listening || _hwnd == IntPtr.Zero)
            return;
        RemoveClipboardFormatListener(_hwnd);
        _listening = false;
        _hwnd = IntPtr.Zero;
    }

    /// <summary>在宿主 WndProc 收到 WM_CLIPBOARDUPDATE 时调用。</summary>
    public void HandleUpdate()
    {
        if (_disposed)
            return;
        var notice = TryReadNotice();
        if (notice is null)
            return;
        Changed?.Invoke(this, notice);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Detach();
        Changed = null;
    }

    private static ClipboardNotice? TryReadNotice()
    {
        // 剪切板偶发被占用，短重试
        for (var i = 0; i < 4; i++)
        {
            try
            {
                if (WpfClipboard.ContainsText())
                {
                    var text = WpfClipboard.GetText() ?? "";
                    if (string.IsNullOrWhiteSpace(text))
                        return new ClipboardNotice("文本", "（空）");
                    var oneLine = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    if (oneLine.Length > 160)
                        oneLine = oneLine[..160] + "…";
                    return new ClipboardNotice("文本", oneLine);
                }

                if (WpfClipboard.ContainsFileDropList())
                {
                    var files = WpfClipboard.GetFileDropList();
                    var n = files?.Count ?? 0;
                    if (n <= 0)
                        return new ClipboardNotice("文件", "文件列表");
                    var first = files![0] ?? "";
                    var name = System.IO.Path.GetFileName(first);
                    if (string.IsNullOrEmpty(name))
                        name = first;
                    return n == 1
                        ? new ClipboardNotice("文件", name)
                        : new ClipboardNotice("文件", $"{n} 项 · {name}");
                }

                if (WpfClipboard.ContainsImage())
                    return new ClipboardNotice("图片", "已复制图片");

                return new ClipboardNotice("剪切板", "有新内容");
            }
            catch (COMException)
            {
                System.Threading.Thread.Sleep(40);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}

internal sealed record ClipboardNotice(string Kind, string Preview);
