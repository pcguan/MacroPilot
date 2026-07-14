using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;
using Microsoft.Win32;

namespace MacroPilot.Input;

/// <summary>一个已握手确认的 CH9329 串口及其 USB 桥片信息。</summary>
public sealed record Ch9329PortInfo(string Port, int Baud, string VidPid, string BridgeName, bool BridgeKnown);

/// <summary>
/// 扫描串口并探测 CH9329（发 GET_INFO 0x01，应答含 0x81）；
/// 对确认的口再经注册表查 USB VID/PID，解析桥片芯片名（内置表 + 用户学习表）。
/// </summary>
public static class Ch9329Scanner
{
    public static readonly int[] CandidateBauds = { 9600, 115200 };

    // 内置桥片表：USB "VID:PID"（大写）→ 芯片名。
    private static readonly IReadOnlyDictionary<string, string> BuiltInBridges =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["1A86:7523"] = "CH340",
        ["1A86:5523"] = "CH341",
        ["1A86:55D3"] = "CH343",
        ["1A86:55D4"] = "CH9102",
        ["10C4:EA60"] = "CP210x",
        ["0403:6001"] = "FT232",
        ["067B:2303"] = "PL2303",
    };

    public static List<string> ListPorts()
    {
        try { var p = new List<string>(SerialPort.GetPortNames()); p.Sort(); return p; }
        catch { return new List<string>(); }
    }

    /// <summary>扫描所有串口，返回握手确认的 CH9329 口（含桥片解析）。learnedBridges 为用户已命名的桥片表。</summary>
    public static List<Ch9329PortInfo> Scan(IEnumerable<int> baudRates, IReadOnlyDictionary<string, string>? learnedBridges)
    {
        var bauds = DistinctBauds(baudRates);
        var vidPidMap = GetPortVidPidMap();
        var result = new List<Ch9329PortInfo>();
        foreach (var port in ListPorts())
        {
            // 先按 USB 桥过滤：注册表拿得到 USB 口清单时，只探测"是 USB 串口桥"的口
            // （CH9329 必是 USB 桥、必在表内），跳过板载 COM / 蓝牙 SPP 等，别去打扰它们。拿不到表则退回全扫。
            if (vidPidMap.Count > 0 && !vidPidMap.ContainsKey(port)) continue;
            foreach (var baud in bauds)
            {
                if (Probe(port, baud))
                {
                    vidPidMap.TryGetValue(port, out var vidPid);
                    var (name, known) = ResolveBridge(vidPid, learnedBridges);
                    result.Add(new Ch9329PortInfo(port, baud, vidPid ?? "", name, known));
                    break;
                }
            }
        }
        result.Sort((a, b) => string.Compare(a.Port, b.Port, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static List<int> DistinctBauds(IEnumerable<int>? baudRates)
    {
        var list = new List<int>();
        if (baudRates != null)
            foreach (var b in baudRates) if (b > 0 && !list.Contains(b)) list.Add(b);
        if (list.Count == 0) list.Add(9600);
        return list;
    }

    private static (string Name, bool Known) ResolveBridge(string? vidPid, IReadOnlyDictionary<string, string>? learned)
    {
        if (string.IsNullOrEmpty(vidPid)) return ("未知桥片", false);
        if (BuiltInBridges.TryGetValue(vidPid, out var v)) return (v, true);
        if (learned != null && learned.TryGetValue(vidPid, out var v2)) return (v2, true);
        return ($"未知桥片（{vidPid}）", false);
    }

    /// <summary>从注册表 HKLM\SYSTEM\CurrentControlSet\Enum\USB 建立 COMx → "VID:PID" 映射。</summary>
    public static Dictionary<string, string> GetPortVidPidMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return map;
            foreach (var devName in usb.GetSubKeyNames())
            {
                var vidPid = ParseVidPid(devName);
                if (vidPid == null) continue;
                using var dev = usb.OpenSubKey(devName);
                if (dev == null) continue;
                foreach (var inst in dev.GetSubKeyNames())
                {
                    using var dp = dev.OpenSubKey(inst + @"\Device Parameters");
                    if (dp?.GetValue("PortName") is string com && !map.ContainsKey(com))
                        map[com] = vidPid;
                }
            }
        }
        catch { }
        return map;
    }

    /// <summary>从硬件 ID（如 VID_1A86&PID_7523）解析出 "VID:PID"（大写）。</summary>
    private static string? ParseVidPid(string hardwareId)
    {
        int vi = hardwareId.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
        int pi = hardwareId.IndexOf("PID_", StringComparison.OrdinalIgnoreCase);
        if (vi < 0 || pi < 0 || vi + 8 > hardwareId.Length || pi + 8 > hardwareId.Length) return null;
        string vid = hardwareId.Substring(vi + 4, 4).ToUpperInvariant();
        string pid = hardwareId.Substring(pi + 4, 4).ToUpperInvariant();
        return $"{vid}:{pid}";
    }

    public static bool Probe(string port, int baud)
    {
        SerialPort? sp = null;
        try
        {
            sp = new SerialPort(port, baud) { ReadTimeout = 200, WriteTimeout = 200 };
            sp.Open();
            sp.DtrEnable = true; sp.RtsEnable = true;
            sp.DiscardInBuffer();
            byte[] getInfo = { 0x57, 0xAB, 0x00, 0x01, 0x00, 0x03 };
            sp.Write(getInfo, 0, getInfo.Length);
            Thread.Sleep(60);
            int n = sp.BytesToRead;
            if (n <= 0) return false;
            var buf = new byte[n];
            sp.Read(buf, 0, n);
            for (int i = 0; i + 3 < buf.Length; i++)
                if (buf[i] == 0x57 && buf[i + 1] == 0xAB && buf[i + 3] == 0x81) return true;
            return false;
        }
        catch { return false; }
        finally { try { sp?.Close(); sp?.Dispose(); } catch { } }
    }
}
