using System.ComponentModel;

namespace MacroPilot.Models;

/// <summary>运行日志一行。Level 决定正文颜色，StatusKind 决定状态文字颜色。</summary>
public sealed class LogEntry : INotifyPropertyChanged
{
    public string Time { get; init; } = "";
    public string Body { get; init; } = "";
    public bool IsAction { get; init; }

    // Info / Success / Warning / Error
    public string Level { get; init; } = "Info";

    private string _status = "";
    private string _statusKind = "None";
    // None / Running / Success / Fail / Stopped
    public string StatusKind
    {
        get => _statusKind;
        set { if (_statusKind != value) { _statusKind = value; Raise(nameof(StatusKind)); } }
    }

    public string Status
    {
        get => _status;
        set { if (_status != value) { _status = value; Raise(nameof(Status)); Raise(nameof(HasStatus)); } }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_status);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
