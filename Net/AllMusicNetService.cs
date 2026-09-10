using AllMusicMod.Core;
using AllMusicMod.Netease;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace AllMusicMod.Net;

/// <summary>
/// 网络收发与展示状态。
///  - 收包入口 Mod.HandlePacket → NetService.Handle。
///  - 申请入口 Request：UI 与命令统一走这里，权威进程内部直接裁决，纯客户端发包给服务器。
///  - 客户端展示状态（当前曲 / 暂停 / 队列）由服务器广播驱动，供 plist / 点歌面板使用。
/// </summary>
public static class AllMusicNetService
{
    // ---- 客户端展示状态（由服务器广播维护；权威进程写入同一套供 UI 读取） ----
    /// <summary>正在播放的曲目展示名（null = 空闲）；网易云曲目带 [网易云] 标签。</summary>
    public static string? CurrentName { get; set; }
    /// <summary>当前曲是否来自网易云（面板/播放动作据此分支）。</summary>
    public static bool CurrentIsNet { get; set; }
    public static bool IsPlaying { get; set; }
    public static bool IsPaused { get; set; }
    /// <summary>当前曲目由谁点播（权威进程记录；客户端随 CurrentPlaying 快照收到）。</summary>
    public static string CurrentAdder { get; set; } = "";

    /// <summary>当前曲目的网易云 id（0 = 无曲/非在线曲）。歌词窗口据此取词。</summary>
    public static long CurrentNetId { get; set; }

    /// <summary>队列中一首歌的展示信息（名称 + 点歌人）。</summary>
    public sealed record QueueItem(string Name, string Adder);

    /// <summary>队列展示列表（1-base 顺序，不含当前播放）。客户端只读展示，权威写入在服务器。</summary>
    public static List<QueueItem> QueueItems { get; } = new();

    private static AllMusicMod Mod => ModContent.GetInstance<AllMusicMod>();

    // ---- 统一申请入口 ----

    /// <summary>进入世界时调用：单机忽略；纯客户端请求服务器下发当前播放+队列快照做加入对齐。</summary>
    public static void RequestHello()
    {
        if (Main.netMode == NetmodeID.SinglePlayer || Main.netMode == NetmodeID.Server)
            return; // 单机/服务器无外部对齐需求
        try
        {
            var p = Mod.GetPacket();
            p.Write((byte)AllMusicMessageType.RequestHello);
            p.Send();
        }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 命令/UI 触发一次操作。权威进程（单机/服务器，含房主）内部裁决；纯客户端发包给服务器。
    /// arg：RequestPlay / RequestQueueAdd 传歌名，RequestQueueRemove 传索引字符串，其余可为 null。
    /// </summary>
    public static void Request(AllMusicMessageType op, string? arg = null)
    {
        if (Main.netMode == NetmodeID.SinglePlayer || Main.netMode == NetmodeID.Server)
        {
            AllMusicCoordinator.HandleRequest(Main.myPlayer, op, arg, out _);
            return;
        }

        try
        {
            var p = Mod.GetPacket();
            p.Write((byte)op);
            // 序列化约定须与 Handle 的读取顺序一致
            switch (op)
            {
                case AllMusicMessageType.RequestPlay:
                case AllMusicMessageType.RequestForcePlay:
                case AllMusicMessageType.RequestQueueAdd:
                case AllMusicMessageType.RequestQueueRemove:
                case AllMusicMessageType.RequestLoop:
                    p.Write(arg ?? "");
                    break;
            }
            p.Send();
        }
        catch (Exception ex)
        {
            Messsage(ex.Message, Color.Red);
        }
    }

    // ---- 收包调度 ----

    public static void Handle(AllMusicMod mod, BinaryReader r, int whoAmI)
    {
        if (r.BaseStream.Position >= r.BaseStream.Length)
            return;

        var t = (AllMusicMessageType)r.ReadByte();
        bool isServer = Main.netMode == NetmodeID.Server;

        switch (t)
        {
            // Client→Server 请求：仅服务器处理
            case AllMusicMessageType.RequestPlay:
                if (isServer) HandleRequestFromClient(whoAmI, t, SafeReadString(r)); break;
            case AllMusicMessageType.RequestForcePlay:
                if (isServer) HandleRequestFromClient(whoAmI, t, SafeReadString(r)); break;
            case AllMusicMessageType.RequestQueueAdd:
                if (isServer) HandleRequestFromClient(whoAmI, t, SafeReadString(r)); break;
            case AllMusicMessageType.RequestQueueRemove:
                if (isServer) HandleRequestFromClient(whoAmI, t, SafeReadString(r)); break;
            case AllMusicMessageType.RequestStop:
            case AllMusicMessageType.RequestPause:
            case AllMusicMessageType.RequestResume:
            case AllMusicMessageType.RequestSkip:
                if (isServer) HandleRequestFromClient(whoAmI, t, null); break;
            case AllMusicMessageType.RequestLoop:
                if (isServer) HandleRequestFromClient(whoAmI, t, SafeReadString(r)); break;
            case AllMusicMessageType.RequestHello:
                if (isServer) AllMusicCoordinator.SendInitialToClient(whoAmI); break;

            // Server→Client 控制：仅客户端处理（各自本地发声）
            case AllMusicMessageType.ControlPlay:
                if (!isServer) AllMusicCoordinator.ApplyControl(t, SafeReadString(r), SafeReadInt(r)); break;
            case AllMusicMessageType.ControlPlayUrl:
                // + string key + string url + int leadMs
                if (!isServer) AllMusicCoordinator.ApplyControlUrl(SafeReadString(r), SafeReadString(r), SafeReadInt(r)); break;
            case AllMusicMessageType.ControlStop:
            case AllMusicMessageType.ControlPause:
            case AllMusicMessageType.ControlResume:
                if (!isServer) AllMusicCoordinator.ApplyControl(t, null, 0); break;
            case AllMusicMessageType.ControlLoop:
                if (!isServer) AllMusicCoordinator.ApplyControl(t, SafeReadString(r), 0); break;

            case AllMusicMessageType.QueueSnapshot:
                if (!isServer) ReadQueueSnapshot(r); break;
            case AllMusicMessageType.CurrentPlaying:
                if (!isServer) ReadCurrentPlaying(r); break;
            case AllMusicMessageType.Reject:
                if (!isServer) Messsage(SafeReadString(r), Color.Red); break;
        }
    }

    private static void HandleRequestFromClient(int whoAmI, AllMusicMessageType op, string? arg)
    {
        if (!AllMusicCoordinator.HandleRequest(whoAmI, op, arg, out string? reason) && reason != null)
            SendReject(whoAmI, reason);
    }

    // ---- 队列 / 当前播放展示 ----

    private static void ReadQueueSnapshot(BinaryReader r)
    {
        int count = SafeReadInt(r);
        QueueItems.Clear();
        for (int i = 0; i < count && i < 256; i++)
        {
            if (r.BaseStream.Position >= r.BaseStream.Length) break;
            string key = SafeReadString(r);
            string adder = SafeReadString(r);
            var tr = TrackRef.Parse(key);
            QueueItems.Add(new QueueItem(tr.DisplayName, adder));
        }
    }

    private static void ReadCurrentPlaying(BinaryReader r)
    {
        bool idle = SafeReadBoolean(r);
        string key = SafeReadString(r);
        bool paused = SafeReadBoolean(r);
        string adder = SafeReadString(r);

        if (idle || string.IsNullOrEmpty(key))
        {
            CurrentName = null;
            CurrentIsNet = false;
            IsPlaying = false;
            IsPaused = false;
            CurrentAdder = "";
            CurrentNetId = 0;
            return;
        }

        var tr = TrackRef.Parse(key);
        CurrentName = tr.DisplayName;
        CurrentIsNet = tr.IsNet;
        IsPlaying = true;
        IsPaused = paused;
        CurrentAdder = adder;
        CurrentNetId = tr.IsNet ? tr.NetId : 0;

        // 新加入者若正赶上在线曲目：本地也拉流追赶（下到即可从头播，能听到后续部分）。
        // 已在本机准备/播放同一首则不重复请求。
        bool validNet = tr.IsNet && tr.Source switch
        {
            MusicSource.Netease => tr.NetId > 0,
            _ => false,
        };
        if (validNet && !Main.dedServ)
        {
            NeteaseMp3Player.GetStatus(out var phase, out _, out _, out _);
            if (phase is Mp3Phase.Idle or Mp3Phase.Failed)
            {
                NeteaseMp3Player.RequestPlay(tr, 0);
                if (paused)
                    NeteaseMp3Player.Pause();
            }
        }
    }

    // ---- 反馈 ----

    private static void SendReject(int whoAmI, string reason)
    {
        try
        {
            var p = Mod.GetPacket();
            p.Write((byte)AllMusicMessageType.Reject);
            p.Write(reason);
            if (whoAmI >= 0)
                p.Send(toClient: whoAmI);
            else
                p.Send();
        }
        catch { /* 忽略：反馈非关键路径 */ }
    }

    /// <summary>在客户端以聊天文本提示；服务器侧仅打日志。</summary>
    internal static void Messsage(string msg, Color color)
    {
        if (Main.netMode != NetmodeID.Server)
            Main.NewText(msg, color);
        else if (!string.IsNullOrEmpty(msg))
            MainModLog(msg);
    }

    internal static void MainModLog(string msg) =>
        ModContent.GetInstance<AllMusicMod>().Logger.Info("[MidiGM] " + msg);

    // ---- 安全读取（防越界/异常包） ----

    private static string SafeReadString(BinaryReader r)
    {
        try
        {
            if (r.BaseStream.Position >= r.BaseStream.Length) return "";
            var s = r.ReadString();
            return s ?? "";
        }
        catch { return ""; }
    }

    private static int SafeReadInt(BinaryReader r)
    {
        try { return r.ReadInt32(); }
        catch { return 0; }
    }

    private static bool SafeReadBoolean(BinaryReader r)
    {
        try { return r.ReadBoolean(); }
        catch { return false; }
    }
}