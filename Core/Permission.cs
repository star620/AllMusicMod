using Terraria;
using Terraria.ID;

namespace AllMusicMod.Core;

/// <summary>命令/UI 操作所需的权限等级。</summary>
public enum AllMusicPerm
{
    /// <summary>全员可做（点歌/排队）。</summary>
    Any,
    /// <summary>需要 OP（提升）权限（全局控制：停止/暂停/恢复/切歌/改动队列）。</summary>
    Elevated,
}

/// <summary>权限判定。只在权威进程（服务器/单机）上调用。</summary>
public static class Permission
{
    /// <summary>
    /// 判定 whoAmI 是否有权执行 perm。
    ///  - 单机：一律放行。
    ///  - 控制台（whoAmI &lt; 0，专服服主）：一律放行。
    ///  - Any：全员。
    ///  - Elevated：需具备服务器操作权限。
    ///    注意：tML 原版没有 TShock 的 OpTeam 字段，也无法直接判定“房主玩家”，
    ///    故 tML 端暂对 Elevated 也放行（避免卡住单机/服务端流程）；
    ///    M4 将改用 tML 命令系统（FabledItem 权限）做精确的服务器操作员判定后再收紧。
    /// </summary>
    public static bool Can(int whoAmI, AllMusicPerm perm)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            return true;
        if (perm == AllMusicPerm.Any)
            return true;
        if (whoAmI < 0)
            return true; // 专服控制台 = 服主，放行
        return true; // tML：无 OpTeam，暂放行（见上方注释，M4 收紧）
    }
}