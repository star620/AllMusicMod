namespace AllMusicMod.Net;

/// <summary>
/// AllMusicMod 自定义网络消息类型。
/// 请求(Client→Server)：多人时由客户端发出，服务器裁决后广播对应 Control。
/// 控制(Server→Clients)：服务器权威广播，各客户端收到后本地解析/排程/发声（路线A，零逐音符包）。
/// 数据源：各端用本地 MidiSongs 目录里同一份 .mid 解析，网络只传「歌名 + 起播提前量」。
/// </summary>
public enum AllMusicMessageType : byte
{
    // ---- Client → Server（请求）----
    RequestPlay = 1,        // + string songName
    RequestStop = 2,
    RequestPause = 3,
    RequestResume = 4,
    RequestSkip = 5,        // 切歌（跳过当前，播放队列下一首）
    RequestQueueAdd = 6,    // + string songName（点歌进队）
    RequestQueueMove = 7,   // + byte from(1-base) + byte to
    RequestQueueRemove = 8, // + byte idx(1-base)
    RequestHello = 9,       // 客户端进入世界时发送，请求服务器下发当前播放+队列快照（加入对齐）
    RequestLoop = 10,       // + string "1"/"0"（单曲循环开/关，服务器权威）
    RequestForcePlay = 11,  // + string songName（右键强播：打断当前，立即播放该曲）

    // ---- Server → Clients（控制/同步）----
    ControlPlay = 51,       // + string songName + int leadMs（各端延迟 leadMs 起播）
    ControlPlayUrl = 59,    // + string key + string url + int leadMs（服务器已带登录态取到CDN直链，各端直接下载该URL播放）
    ControlStop = 52,
    ControlPause = 53,
    ControlResume = 54,
    QueueSnapshot = 55,     // + int count + count×(string name)  队列全量快照(1-base)
    CurrentPlaying = 56,    // + bool idle + string songName + bool paused（面板展示）
    Reject = 57,            // + string reason（权限/校验失败反馈）
    ControlLoop = 58,       // + string "1"/"0"（同步单曲循环开关到各端）
}