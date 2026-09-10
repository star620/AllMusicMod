using System.IO;
using Terraria;
using Terraria.ModLoader;
using AllMusicMod.Net;
using AllMusicMod.UI;
using AllMusicMod.UIKit;

namespace AllMusicMod;

/// <summary>
/// AllMusic：MP3/在线点歌独立 mod（MIDI 已拆分为原 MidiGmMod）。
/// 服务端权威共享队列，网络只传控制/快照，各端本地下载播放。
/// </summary>
public sealed class AllMusicMod : Mod
{
    public override void Load()
    {
        if (!Main.dedServ)
            UIKitSkin.Load(this);
    }

    public override void Unload()
    {
        UIKitSkin.Unload();
        base.Unload();
    }

    /// <summary>收到自定义网络消息（客户端收服务器的 Control*，服务器收客户的 Request*）。</summary>
    public override void HandlePacket(BinaryReader reader, int whoAmI)
    {
        AllMusicNetService.Handle(this, reader, whoAmI);
    }
}