using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace MacroPilot.Services;

internal interface IClipboardAccess
{
    uint Sequence { get; }
    object? Capture();
    void SetText(string text);
    void Restore(object? snapshot);
}

/// <summary>临时借用剪贴板。仅在内容仍属于本次粘贴时还原，保护用户期间新复制的内容。</summary>
internal sealed class ClipboardLease
{
    private readonly IClipboardAccess _access;
    private readonly object? _snapshot;
    private readonly uint _ownedSequence;
    private bool _restored;

    internal ClipboardLease(IClipboardAccess access, string text)
    {
        _access = access;
        uint before = access.Sequence;
        _snapshot = access.Capture(); // 备份失败必须终止，不能先覆盖再发现无法还原。
        if (access.Sequence != before) throw new InvalidOperationException("剪贴板已被其它程序修改，请重试文本输入。");
        access.SetText(text);
        _ownedSequence = access.Sequence;
    }

    internal void Restore()
    {
        if (_restored) return;
        _restored = true;
        if (_access.Sequence == _ownedSequence) _access.Restore(_snapshot);
    }
}

internal sealed class WpfClipboardAccess : IClipboardAccess
{
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    public uint Sequence => GetClipboardSequenceNumber();

    private static T OnUi<T>(Func<T> action)
    {
        var app = Application.Current ?? throw new InvalidOperationException("无法访问剪贴板：应用程序未初始化。");
        return app.Dispatcher.Invoke(action);
    }

    public object? Capture() => OnUi<object?>(() =>
    {
        var source = Clipboard.GetDataObject();
        if (source == null) return null;
        var copy = new DataObject();
        foreach (string format in source.GetFormats(autoConvert: false))
        {
            var value = source.GetData(format, autoConvert: false);
            if (value == null) throw new InvalidOperationException($"无法备份剪贴板格式：{format}");
            // 立即读取延迟格式；复制可变缓冲，避免原剪贴板所有者退出后备份失效。
            if (value is Stream stream)
            {
                long position = stream.CanSeek ? stream.Position : 0;
                try
                {
                    if (stream.CanSeek) stream.Position = 0;
                    var saved = new MemoryStream();
                    stream.CopyTo(saved); saved.Position = 0; value = saved;
                }
                finally { if (stream.CanSeek) stream.Position = position; }
            }
            else if (value is BitmapSource bitmap)
            {
                var saved = bitmap.CloneCurrentValue(); saved.Freeze(); value = saved;
            }
            else if (value is Array array) value = array.Clone();
            copy.SetData(format, value, autoConvert: false);
        }
        return copy;
    });

    public void SetText(string text) => OnUi(() => { Clipboard.SetText(text); return true; });
    public void Restore(object? snapshot) => OnUi(() =>
    {
        if (snapshot == null) Clipboard.Clear();
        else Clipboard.SetDataObject(snapshot, copy: true);
        return true;
    });
}
