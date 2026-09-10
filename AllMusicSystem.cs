using AllMusicMod.Core;
using AllMusicMod.UI;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace AllMusicMod;

/// <summary>
/// 生命周期系统：权威进程每帧推进共享队列；打开点歌 UI 时锁定玩家操作。
/// 锁定依据（tML 1.4.4 时序）：
///  - PreUpdatePlayers 早于 Player.Update 消费 control* 字段，此处置 false 当帧即生效。
///  - 滚轮切快捷栏读原始 ScrollWheelDelta（早于 UpdateUI），故用 tML 官方
///    PlayerInput.LockVanillaMouseScroll 锁；面板列表在 UpdateUI 消费 ScrollWheelDeltaForUI 正常滚动。
/// </summary>
public sealed class AllMusicSystem : ModSystem
{
    public override void PreUpdatePlayers()
    {
        if (Main.dedServ || Main.gameMenu)
            return;

        // 打开点歌 UI 时锁滚轮（阻止切换快捷栏）。锁玩家移动见 AllMusicPlayer.PreUpdateMovement：
        // 因为 tML 1.4.4 客户端会在 Player.Update 内重新填充 control*，这里清 control* 会被覆盖。
        if (!AllMusicUiSystem.PanelOpen)
            return;
        PlayerInput.LockVanillaMouseScroll("AllMusicMod");
    }

    public override void PostUpdatePlayers()
    {
        AllMusicCoordinator.Tick();
    }
}