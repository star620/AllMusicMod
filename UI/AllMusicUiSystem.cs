using System;
using System.Collections.Generic;
using AllMusicMod.Core;
using AllMusicMod.UIKit;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;

namespace AllMusicMod.UI;

/// <summary>
/// UI 生命周期（UIKit 驱动版）：
///  - 客户端进入世界后懒创建在线音乐面板，并挂到 MasterView.gameScreen。
///  - UpdateUI 每帧调用 MasterView.UpdateMaster()（同步鼠标状态、滚动增量并更新 UIKit 树）。
///  - ModifyInterfaceLayers 在 Mouse Text 之下插入 UIKit 绘制层。
///  - 专服（dedServ）不创建任何 UI。
/// </summary>
public sealed class AllMusicUiSystem : ModSystem
{
    internal static AllMusicUiSystem Instance { get; private set; } = null!;

    private bool _worldUiReady;

    /// <summary>点歌面板 / 歌词浮窗当前是否打开（AllMusicSystem.PreUpdatePlayers 据此锁定玩家操作）。</summary>
    internal static bool PanelOpen =>
        UIKitNeteasePanel.Instance is { } net && (net.IsOpen || net.LyricVisible);

    private static KeyboardState _prevKb;

    public override void Load()
    {
        Instance = this;
        // 借用聊天框输中文：拦截在 vanilla「聊天发送」这一帧（DoUpdate_HandleInput，位于 Main.Update 之前）。
        // 箱体聊天文本逐帧在 Main.Update(之后)累积，发送在 DoUpdate_HandleInput 读取 Main.chatText。
        // 策略：
        //  1) 聊天开着时每帧把 Main.chatText 完整快照镜像进点歌面板（不清空 → 退格/方向键编辑仍有效）。
        //  2) 仅在检测到「回车」边沿时清空 chatText，使 vanilla 发送时收到空串（不会把这段字当聊天广播），
        //     并标记「提交搜索」；Esc 关闭则视为取消。
        _prevKb = Keyboard.GetState();
        Terraria.On_Main.DoUpdate_HandleInput += OnChatFlush;
    }

    private static void OnChatFlush(On_Main.orig_DoUpdate_HandleInput orig, Main self)
    {
        var panel = UIKitNeteasePanel.Instance;
        if (panel is { AwaitingChat: true })
        {
            KeyboardState kb = Keyboard.GetState();
            bool enterEdge = kb.IsKeyDown(Keys.Enter) && !_prevKb.IsKeyDown(Keys.Enter);
            _prevKb = kb;

            if (Main.drawingPlayerChat)
            {
                panel.MirrorChat(Main.chatText); // 完整快照，退格编辑仍可用
                if (enterEdge && Main.chatText is { Length: > 0 })
                {
                    Main.chatText = "";          // 回车发送前置空 → vanilla 发空串不广播
                    panel.MarkCommitRequested();
                }
            }

            orig(self);

            // 输入已由 vanilla 处理完，此刻 PlayerInput.ScrollWheelDelta 仍是本帧值（tML 在更早时清零），
            // 在这里转存给 UIScrollView，供其 Update 时消费驱动列表滚动（比读 XNA 原始累计值更可靠）。
            UIScrollView.PendingWheelDelta = PlayerInput.ScrollWheelDelta;

            // 聊天框已关（回车或 Esc）→ 收尾：回车置过提交标记则搜索，Esc 则取消
            if (!Main.drawingPlayerChat)
                panel.FinalizeBorrowChat();
        }
        else
        {
            // 未借用也持续跟踪按键，避免键位边沿漂移
            _prevKb = Keyboard.GetState();
            orig(self);
            UIScrollView.PendingWheelDelta = PlayerInput.ScrollWheelDelta;
        }
    }

    public override void Unload()
    {
        Terraria.On_Main.DoUpdate_HandleInput -= OnChatFlush;
        UIKitNeteasePanel.DetachAll();
        Instance = null!;
    }

    /// <summary>单机保存退出 / 世界卸载的可靠清场。</summary>
    public override void PreSaveAndQuit() => ResetForNewWorld();

    public override void OnWorldUnload() => ResetForNewWorld();

    public override void UpdateUI(GameTime gameTime)
    {
        if (Main.dedServ)
            return;

        // 进入世界后（含单人/多人）才构建 UI——控件内需要字体与皮肤资源
        if (!Main.gameMenu)
        {
            if (!_worldUiReady)
            {
                _worldUiReady = true;
                UIKitNeteasePanel.EnsureInstance();
                UIKitNeteasePanel.Instance!.Attach();
            }
        }
        else
        {
            // 离开世界（回主菜单）必须清场：关窗口、停残留音频、清空上一局遗留状态
            if (_worldUiReady)
            {
                _worldUiReady = false;
                ResetForNewWorld();
            }
        }

        if (_worldUiReady)
        {
            // 游戏内置覆盖层(背包/设置/暂停/地图等)打开时先收掉本 mod 窗口，
            // 否则我们的 UIKit 窗口会叠在菜单上面且吃掉点击，导致“菜单点不动”。
            bool overlayOpen = VanillaOverlayOpen();
            if (overlayOpen)
                UIKitNeteasePanel.Instance?.CloseFloatingWindows();

            UIKitNeteasePanel.Instance?.Tick();
            MasterView.UpdateMaster();

            // 鼠标悬停/操作本 mod UI 时占用玩家输入：UIKit 只处理控件命中，不吞全局鼠标状态。
            // 滚轮：快捷栏切换已由 PreUpdatePlayers 里 LockVanillaMouseScroll 锁；
            // 这里消费列表用剩的 ForUI 增量并吞掉左右键，避免点歌时顺带与世界交互。
            if (!overlayOpen && CursorOverUi())
            {
                Main.player[Main.myPlayer].mouseInterface = true;
                Main.LocalPlayer.cursorItemIconEnabled = false;
                PlayerInput.ScrollWheelDeltaForUI = 0;
                PlayerInput.ScrollWheelDelta = 0;
                Main.mouseLeft = false;
                Main.mouseLeftRelease = false;
                Main.mouseRight = false;
                Main.mouseRightRelease = false;
            }
        }
    }

    /// <summary>是否有游戏内置 UI 覆盖层需要优先交互（此时收起我们的窗口、禁止吞噬输入）。</summary>
    private static bool VanillaOverlayOpen()
    {
        if (Main.gameMenu || Main.mapFullscreen)
            return true;
        if (Main.InGameUI != null && Main.InGameUI.CurrentState != null)
            return true;
        if (Main.playerInventory)
            return true;
        var p = Main.LocalPlayer;
        if (p != null && (p.talkNPC != -1 || p.sign != -1 || p.chest != -1))
            return true;
        return false;
    }

    private static bool CursorOverUi() =>
        UIKitNeteasePanel.Instance is { } net && net.CursorOverUi();

    /// <summary>离开世界清场：关窗口 + 停本机残留音频 + 清空权威态（队列/当前曲/展示）。</summary>
    private static void ResetForNewWorld()
    {
        if (UIKitNeteasePanel.Instance is { } net)
        {
            net.SetOpen(false);
            net.ResetContent();
        }
        AllMusicCoordinator.ResetSession();
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        if (Main.dedServ)
            return;

        // 主菜单兜底清场
        if (Main.gameMenu)
        {
            if (_worldUiReady)
            {
                _worldUiReady = false;
                ResetForNewWorld();
            }
            return;
        }

        int mouseTextIndex = layers.FindIndex(l => l.Name.Equals("Vanilla: Mouse Text", StringComparison.Ordinal));
        if (mouseTextIndex == -1)
            return;

        layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
            "AllMusicMod: UIKit", DrawAllMusicUi, InterfaceScaleType.UI));
    }

    private bool DrawAllMusicUi()
    {
        if (!_worldUiReady || Main.gameMenu)
            return true;

        MasterView.DrawMaster(Main.spriteBatch);

        if (!string.IsNullOrEmpty(UIView.HoverText))
            Main.instance.MouseText(UIView.HoverText);
        return true;
    }
}