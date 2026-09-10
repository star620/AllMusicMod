using AllMusicMod.UI;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace AllMusicMod;

/// <summary>
/// 打开点歌 UI 时锁定玩家操作（WASD/空格/使用物品等；Esc 除外）。
/// 正确的 hook 是 ModPlayer.PreUpdateMovement：它在每个玩家的 Player.Update 内部、
/// vanilla 给 control* 重新赋值之后、移动/物品消耗之外被调用，清空字段当帧即生效。
/// （单纯用 ModSystem.PreUpdatePlayers 清 control* 无效——tML 1.4.4 客户端会在玩家
/// 更新时重新填充这些字段，导致清空被覆盖。）
/// </summary>
public sealed class AllMusicPlayer : ModPlayer
{
	public override void PreUpdateMovement()
	{
		if (Main.dedServ || Main.gameMenu)
			return;
		if (!AllMusicUiSystem.PanelOpen)
			return;

		// 锁移动/跳跃/抓钩/坐骑/使用物品/智能交互（Esc 不在这些字段中，可正常关闭窗口）
		Player.controlUp = false;
		Player.controlDown = false;
		Player.controlLeft = false;
		Player.controlRight = false;
		Player.controlJump = false;
		Player.controlHook = false;
		Player.controlMount = false;
		Player.controlUseItem = false;
		Player.controlUseTile = false;
		Player.controlSmart = false;
		Player.releaseUseItem = false;

		// 该 hook 在“position 依 velocity 更新之前”触发，但 control* 已在此前被应用到
		// velocity（水平/垂直速度早已算好）；仅清 control* 是清的，会漏出当帧速度造成的位移。
		// 因此在此强制把 velocity 清零，position 更新用 0 速度 → 角色完全不动。
		Player.velocity = Vector2.Zero;

		// 鼠标：指向世界/快捷栏的交互拦截
		Player.mouseInterface = true;
		Player.cursorItemIconEnabled = false;
	}
}