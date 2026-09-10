// ============================================================================
// 在线音乐点播窗口（网易云）。
// 数据链路：MusicSources.SearchAsync（三源并行搜索）→ 按源取流/DownloadAsync
//         → 下载缓存 → Microsoft.Xna.Framework.Media.MediaPlayer 本地解码播放。
// 输入：搜索框聚焦时用 TextInputEXT.StartTextInput() 显式呼出系统 IME（中文输入法），
//      TextInput/TextEditing 事件负责把已确认字符/拼音组成串喂进 _query/_composition。
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using AllMusicMod.Core;
using AllMusicMod.Net;
using AllMusicMod.Netease;
using AllMusicMod.UIKit;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ReLogic.Content;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;

namespace AllMusicMod.UI
{
	/// <summary>
	/// 在线音乐点播台（MP3/在线点歌）。搜索框聚焦即呼出系统中文输入法。
	/// 搜索结果来自网易云单源，逐行带来源标签；无版权/VIP 类曲目无法取流时由播放器提示。
	/// </summary>
	internal sealed class UIKitNeteasePanel
	{
		private const float WinW = 560f;
		private const float WinH = 560f;
		private const float Pad = 12f;
		private const float RowH = 34f;
		private const float RowGap = 3f;

		// 可切换的音源（现仅网易云，保留切源按钮结构便于日后扩展）
		private static readonly MusicSource[] SourceOptions =
			{ MusicSource.Netease };

		private static string SourceLabel(MusicSource s) => s switch
		{
			MusicSource.Netease => "网易云",
			_ => "?",
		};

		// ---- 根 ----
		private UIWindow _win = null!;
		/// <summary>右下角常驻“点歌”入口（对应原 UIKitPointSongPanel._btnToggle，UI 保持不变）。</summary>
		private UIButton _btnToggle = null!;
		private bool _attached;
		private bool _open;

		// ---- 窗口内容 ----
		private UILabel _title = null!;

		private UIKitSearchBox _searchBox = null!;
		private UIButton _btnSearch = null!;
		private UIButton _btnClear = null!;
		private readonly List<UIButton> _srcBtns = new();

		private UIScrollView _list = null!;
		private readonly List<UIButton> _rowBtns = new();

		// ---- 展示模式：搜索结果 / 点歌队列 ----
		private UIButton _btnTabSearch = null!;
		private UIButton _btnTabQueue = null!;
		private bool _showQueue;
		/// <summary>当前队列/播放内容的签名（队列模式下内容变化才重建行）。</summary>
		private string _queueSig = "";

		// ---- 歌词浮窗（独立窗口，由「歌词」按钮开合） ----
		private readonly UIKitLyricWindow _lyricWin = new();
		private UIButton _btnLyric = null!;

		// ---- 当前播放信息 + 进度条（列表下方） ----
		private UILabel _nowLabel = null!;
		private UISimpleProgress _nowBar = null!;

		private UILabel _volLabel = null!;
		private UISlider _volSlider = null!;
		private UIButton _btnStopMp3 = null!;

		private UILabel _status = null!;

		// ---- 网易云二维码登录 ----
		/// <summary>承载扫码登录入口的“网易云”源按钮（搜索框下方，右键弹出登录）。</summary>
		private UIButton _neteaseLoginHost = null!;
		private UIWindow _loginWin = null!;
		private QrImageView _qrImg = null!;
		private UILabel _qrStatus = null!;
		private UIButton _btnLoginRefresh = null!;
	private UIButton _btnLogout = null!;      // 退出当前登录
private UIButton _btnCookieOpen = null!;   // 用记事本打开 Cookie 文件
private UIButton _btnCookieImport = null!; // 从 Cookie 文件导入并校验登录态
	private readonly object _qrLock = new();
		private CancellationTokenSource? _qrCts;
		private int _loginRev;   // 二维码 PNG 版本，帧循环据此换纹理（主线程加载）
		private byte[]? _qrPng;
		private int _shownQrRev;
		private string _qrText = "";
		private bool _qrSuccess;
		private bool _qrExpired;

		// ---- 搜索状态 ----
		private MusicSource _src = MusicSource.Netease;
		private string _query = "";
		private bool _focused;
		private readonly HashSet<Keys> _prevKeys = new();
		private List<SongHit> _results = new();
		private bool _searchBusy;
		private int _searchSeq;
		private int _selIdx = -1;
		private bool _needRebuild = true;
		/// <summary>瞬态提示（搜索中/失败/无结果），RefreshStatus 用它避免被默认文案盖掉。</summary>
		private string? _notice;

		private sealed record SearchPending(int Seq, bool Ok, List<SongHit> Songs, string Err);
		private readonly object _searchGate = new();
		private SearchPending? _pending;

		// ---- 中文输入：显式呼出系统 IME（TextInputEXT 事件驱动，不依赖聊天接管） ----
		/// <summary>置给 Main.CurrentInputTextTakerOverride 的接管令牌，用于保持文本输入态不被打断。</summary>
		private readonly object _chatToken = new();
		/// <summary>IME 当前是否已激活（StartTextInput 处于开启状态）。</summary>
		private bool _imeActive;
		/// <summary>输入法未确认的拼音/候选组成串（预览用）。</summary>
		private string _composition = "";

		// ---- 借用游戏原生聊天框实现中文输入：FNA 全屏下自绘 IME 候选不弹，改用 vanilla 聊天输入框承载输入 ----
		/// <summary>是否正处于“借用聊天框输入”状态（vanilla 聊天框已打开）。</summary>
		internal bool AwaitingChat;
		/// <summary>借用聊天框期间累积的输入内容（每帧从 Main.chatText 完整快照镜像）。</summary>
		private string _chatBuf = "";
		/// <summary>回车时是否已要求提交搜索（区分“回车=搜索”与“Esc=取消”收尾）。</summary>
		private bool _commitRequested;

		// 光标闪烁时钟
		private float _caretTime;

		// 队列移出手势：同一行连续右键两次确认移出（双击）。行号 1-base，tick 用 Environment.TickCount。
		private int _rmRcRow = -1;
		private int _rmRcTick = 0;

		public static UIKitNeteasePanel? Instance { get; private set; }

		static UIKitNeteasePanel()
		{
			// FNA 的 OS 文本输入事件：TextEditing = 输入法未确认的组成串；TextInput = 已确认的字符。
			// 事件是进程级静态的，这里转发给“当前实例”处理；收到后自行按 _focused/_imeActive 过滤。
			TextInputEXT.TextInput += c => Instance?.OnOsCommitted(c);
			TextInputEXT.TextEditing += (s, _, _) => Instance?.OnOsComposition(s);
		}

		public static void EnsureInstance()
		{
			if (Instance == null)
			{
				// 进入世界时加载已保存的网易云登录态（含扫码失败后手动粘贴的 Cookie）
				NeteaseSession.Load();
				Instance = new UIKitNeteasePanel();
				RefreshAccountNicknameAsync(); // 有登录态则后台校验并刷新“当前账号”昵称
			}
		}

		private UIKitNeteasePanel()
		{
			BuildRoot();
		}

		public void Attach()
	{
		if (_attached)
			return;
		MasterView.gameScreen.AddChild(_btnToggle);
		_btnToggle.Visible = true;
		MasterView.gameScreen.AddChild(_win);
		MasterView.gameScreen.AddChild(_loginWin);
		_lyricWin.Attach();
		_attached = true;
		SetOpen(false);
	}

		/// <summary>游戏内置覆盖层（背包/设置/暂停等）打开时收起本 mod 全部浮窗，避免叠在菜单上吃点击。</summary>
		public void CloseFloatingWindows()
		{
			SetOpen(false);
			_lyricWin.SetOpen(false);
		}

		public static void DetachAll()
		{
			if (Instance != null && Instance._attached)
		{
			Instance.StopQr();
			Instance._lyricWin.Detach();
			MasterView.gameScreen.RemoveChild(Instance._win);
			MasterView.gameScreen.RemoveChild(Instance._loginWin);
			MasterView.gameScreen.RemoveChild(Instance._btnToggle);
			Instance._attached = false;
			Instance.StopIme();
		}
			Instance = null;
		}

		/// <summary>点歌面板是否打开（AllMusicSystem.PreUpdatePlayers 据此锁定玩家操作）。</summary>
		public bool IsOpen => _open;

		/// <summary>歌词浮窗是否可见（宿主据此锁滚轮 / 占用输入）。</summary>
		public bool LyricVisible => _lyricWin.Visible;

		// ==================================================================== 构建

		private void BuildRoot()
		{
			if (UILabel.DefaultFont == null)
				UILabel.DefaultFont = FontAssets.MouseText.Value;

			_win = new UIWindow
			{
				CanMove = true,
				ClickAndDrag = true,
				Width = WinW,
				Height = WinH,
			};
			_win.Position = new Vector2((Main.screenWidth - WinW) / 2f - 60f + 40f, (Main.screenHeight - WinH) / 2f);
			_win.BackgroundColor = new Color(53, 35, 111, 255) * 0.74f;
			_win.Visible = false; // 构建期即隐藏，等用户主动点开

			// 标题
			_title = NewLabel("♪ 在线音乐 点播", 1.05f, Color.White);
			_title.Position = new Vector2(Pad + 2f, 8f);
			_win.AddChild(_title);

			// 网易云登录：入口挂在下方“网易云”源按钮上（右键弹出/收起扫码登录，登录后可播 VIP/会员曲）
			// 原先右上角的“登录”按钮未正确挂到窗口导致不显示，已移除，改为源按钮右键触发。

			// 搜索行：输入框 + 搜索 + 清空
			_searchBox = new UIKitSearchBox
			{
				Width = 386f,
				Height = 32f,
				Hint = "点击后可直接输入（支持中文输入法）",
			};
			_searchBox.Position = new Vector2(Pad, 42f);
			_searchBox.onLeftClick += (_, _) => StartBorrowChat();
			_searchBox.Tooltip = "点击后直接打字：英文/中文均可（系统输入法），回车=搜索，Esc=取消焦点";
			_win.AddChild(_searchBox);

			_btnSearch = MakeButton("搜索", 88f, Color.White);
			_btnSearch.Position = new Vector2(Pad + 386f + 8f, 41f);
			_btnSearch.onLeftClick += (_, _) => CommitBorrowIfActive();
			_btnSearch.Tooltip = "在当前音源下搜索（借用聊天框输入时，点击此按钮同样会提交输入并搜索）";
			_win.AddChild(_btnSearch);

			_btnClear = MakeButton("✕", 34f, new Color(190, 185, 220));
			_btnClear.Position = new Vector2(WinW - Pad - 38f, 41f);
			_btnClear.onLeftClick += (_, _) => { SetQueryExternal(""); StartBorrowChat(); };
			_btnClear.Tooltip = "清空输入";
			_win.AddChild(_btnClear);

			// 音源切换：一次只查一个源（不三源并发）。网易云源按钮右键弹出扫码登录。
			float srcX = Pad + 2f;
			float srcW = 118f;
			for (int i = 0; i < SourceOptions.Length; i++)
			{
				MusicSource opt = SourceOptions[i];
				// 网易云承载登录入口，按钮加宽以容纳“当前账户昵称”显示
				float w = opt == MusicSource.Netease ? 210f : srcW;
				var sb = MakeButton(SourceLabel(opt), w, new Color(190, 185, 220));
				sb.Position = new Vector2(srcX, 80f);
				sb.SetBackgroundColor(new Color(38, 42, 120));
				sb.onLeftClick += (_, _) => SwitchSource(opt);
				if (opt == MusicSource.Netease)
				{
					_neteaseLoginHost = sb;
					sb.onRightClick += (_, _) => ToggleLoginWindow();
				}
				_win.AddChild(sb);
				_srcBtns.Add(sb);
				srcX += srcW + 6f;
			}
			UpdateLoginUi();

			// —— 展示模式切换：搜歌结果 / 点歌队列 ——
			_btnTabSearch = MakeButton("搜 歌", 92f, Color.White);
			_btnTabSearch.Position = new Vector2(Pad, 120f);
			_btnTabSearch.SetBackgroundColor(new Color(38, 42, 120));
			_btnTabSearch.onLeftClick += (_, _) => { if (_showQueue) { _showQueue = false; _needRebuild = true; } };
			_btnTabSearch.Tooltip = "展示搜索结果";
			_win.AddChild(_btnTabSearch);

			_btnTabQueue = MakeButton("播放队列", 118f, Color.White);
			_btnTabQueue.Position = new Vector2(Pad + 92f + 6f, 120f);
			_btnTabQueue.SetBackgroundColor(new Color(38, 42, 120));
			_btnTabQueue.onLeftClick += (_, _) => { if (!_showQueue) { _showQueue = true; _needRebuild = true; } };
			_btnTabQueue.Tooltip = "展示当前点歌列表（含点歌人）：在某一行连续右键两次可把该首移出队列";
			_win.AddChild(_btnTabQueue);

			// —— 歌词浮窗开关（独立窗口，与上面两个页签无关） ——
			_btnLyric = MakeButton("歌 词", 92f, new Color(190, 185, 220));
			_btnLyric.Position = new Vector2(Pad + 92f + 6f + 118f + 6f, 120f);
			_btnLyric.SetBackgroundColor(new Color(38, 42, 120));
			_btnLyric.onLeftClick += (_, _) => _lyricWin.Toggle();
			_btnLyric.Tooltip = "打开/关闭歌词窗口（跟随当前播放曲目，滚动高亮；超长行自动换行）";
			_win.AddChild(_btnLyric);

			// 结果 / 队列 共用的滚动列表
			_list = new UIScrollView
			{
				Width = WinW - Pad * 2,
				Height = 224f,
			};
			_list.Position = new Vector2(Pad, 158f);
			_win.AddChild(_list);

			// 当前播放信息 + 播放进度条（列表下方）
			_nowLabel = NewLabel("当前无播放", 0.72f, new Color(210, 215, 245));
			_nowLabel.Position = new Vector2(Pad + 2f, 390f);
			_win.AddChild(_nowLabel);

			_nowBar = new UISimpleProgress
			{
				// 与窗口同宽（左右各留 2px 视觉边），完全覆盖整行；高度跟随轨道纹理
				Width = WinW - 4f,
				Visible = false,
			};
			_nowBar.Position = new Vector2(2f, 426f);
			_win.AddChild(_nowBar);

			// 音量 + 停止
			_volLabel = NewLabel("MP3音量 100%", 0.8f, new Color(190, 185, 220));
			_volLabel.Position = new Vector2(Pad + 2f, 472f);
			_win.AddChild(_volLabel);

			_volSlider = new UISlider { Width = 230f, MinValue = 0f, MaxValue = 1f, Value = NeteaseMp3Player.Volume };
			_volSlider.Position = new Vector2(Pad + 132f, 466f);
			_volSlider.BackgroundColor = Color.White;
			_volSlider.valueChanged += (_, v) => NeteaseMp3Player.SetVolume(v);
			_win.AddChild(_volSlider);

			_btnStopMp3 = MakeButton("停止", 92f, Color.White);
			_btnStopMp3.Position = new Vector2(WinW - Pad - 96f, 464f);
			_btnStopMp3.onLeftClick += (_, _) => AllMusicNetService.Request(AllMusicMessageType.RequestStop);
			_btnStopMp3.Tooltip = "停止当前播放并顺延下一首";
			_win.AddChild(_btnStopMp3);

			// 状态
			_status = NewLabel("", 0.72f, new Color(190, 185, 220));
			_status.Position = new Vector2(Pad + 2f, 516f);
			_win.AddChild(_status);

			// 右下角常驻“点歌”入口：点击打开/关闭在线音乐面板（对应原 UIKitPointSongPanel._btnToggle）。
			_btnToggle = new UIButton("点歌")
			{
				AutoSize = false,
				Width = 74f,
			};
			_btnToggle.SetFont(FontAssets.MouseText.Value);
			_btnToggle.SetTextColor(Color.White);
			_btnToggle.SetBackgroundColor(new Color(28, 32, 119));
			_btnToggle.onLeftClick += (_, _) => Toggle();
			_btnToggle.Tooltip = "打开 / 关闭在线音乐点唱台";
			PositionToggleButton();

			BuildLoginWindow();
		}

		/// <summary>构建“网易云登录”弹窗（默认隐藏，右键“网易云”源按钮弹出）。扫码为主，附浏览器 Cookie 兜底导入 + 退出登录。</summary>
		private void BuildLoginWindow()
		{
			const float w = 400f;
			const float qrSize = 200f; // 与二维码接口请求的 200x200 保持一致
			const float padX = 16f;

			_loginWin = new UIWindow
			{
				CanMove = true,
				ClickAndDrag = true,
				Width = w,
				Height = 470f, // 占位：构建完按内容真实高度回填
			};
			_loginWin.BackgroundColor = new Color(30, 22, 76, 255) * 0.9f;
			_loginWin.Visible = false;

			// 自上而下流式布局：每块控件按上一块的真实高度推进，避免文字互相覆盖
			float y = 12f;

			var lt = NewLabel("网易云登录", 1.0f, Color.White);
			lt.Position = new Vector2(padX, y);
			_loginWin.AddChild(lt);
			y += lt.Height + 10f;

			var tip = NewLabel("扫码登录：请用「网易云音乐 App」扫码", 0.66f, new Color(255, 214, 120));
			tip.Position = new Vector2(padX, y);
			_loginWin.AddChild(tip);
			y += tip.Height + 12f;

			_qrImg = new QrImageView { Visible = false };
			_qrImg.Position = new Vector2((w - qrSize) / 2f, y);
			_loginWin.AddChild(_qrImg);
			y += qrSize + 12f;

			_qrStatus = NewLabel("准备中…", 0.78f, new Color(200, 200, 230));
			_qrStatus.Position = new Vector2(padX, y);
			_loginWin.AddChild(_qrStatus);
			y += _qrStatus.Height + 12f;

			// 扫码 与 Cookie 兜底的分隔说明（多行文本，直白指导；颜色提亮增强对比度）
			var help = NewLabel(
				"—— 若扫码卡在“行为验证码”，改用下方 Cookie 导入 ——\n"
				+ "① 电脑浏览器打开并登录 music.163.com\n"
				+ "② 按 F12 → 网络 → 刷新页面 → 点任意请求\n"
				+ "③ 复制「请求头 → Cookie」整段（含 MUSIC_U=）\n"
				+ "④ 点[打开Cookie文件]粘贴保存，再点[导入校验]",
				0.54f, new Color(226, 226, 246));
			help.Position = new Vector2(padX, y);
			_loginWin.AddChild(help);
			y += help.Height + 10f;

			// 登录态文件路径单独一行：过长时保留末尾（含文件名），完整路径放悬停提示
			string fullPath = NeteaseSession.CookieFilePath;
			var pathLabel = NewLabel("登录态文件：" + ShortenPath(fullPath, 30), 0.54f, new Color(150, 210, 245));
			pathLabel.Position = new Vector2(padX, y);
			pathLabel.Tooltip = fullPath;
			_loginWin.AddChild(pathLabel);
			y += pathLabel.Height + 18f;

			// —— 操作按钮行 1：Cookie 兜底 ——
			_btnCookieOpen = MakeButton("打开Cookie文件", 172f, new Color(220, 220, 255));
			_btnCookieOpen.Tooltip = "用系统默认编辑器打开登录态文件，粘贴浏览器 Cookie 后保存";
			_btnCookieOpen.onLeftClick += (_, _) => OpenCookieFile();
			_btnCookieOpen.Position = new Vector2(padX, y);
			_loginWin.AddChild(_btnCookieOpen);

			_btnCookieImport = MakeButton("导入校验", 172f, new Color(160, 240, 180));
			_btnCookieImport.Tooltip = "读取 Cookie 文件并联网校验；有效则立即登录成功，无需重进世界";
			_btnCookieImport.onLeftClick += (_, _) => ImportCookieFile();
			_btnCookieImport.Position = new Vector2(padX + 188f, y);
			_loginWin.AddChild(_btnCookieImport);
			y += _btnCookieOpen.Height + 10f;

			// —— 操作按钮行 2：扫码刷新 / 退出登录 / 关闭 ——
			_btnLoginRefresh = MakeButton("扫码刷新", 112f, Color.White);
			_btnLoginRefresh.Tooltip = "重新获取二维码（二维码过期 / 想换号时）";
			_btnLoginRefresh.onLeftClick += (_, _) => StartQrLogin();
			_btnLoginRefresh.Position = new Vector2(padX, y);
			_loginWin.AddChild(_btnLoginRefresh);

			_btnLogout = MakeButton("退出登录", 112f, new Color(255, 170, 170));
			_btnLogout.Tooltip = "清除本机已保存的网易云登录态，返回未登录（之后可重新扫码或导入 Cookie）";
			_btnLogout.onLeftClick += (_, _) => LogoutNetease();
			_btnLogout.Position = new Vector2(padX + 122f, y);
			_loginWin.AddChild(_btnLogout);

			var btnClose = MakeButton("关闭", 112f, Color.White);
			btnClose.onLeftClick += (_, _) => CloseLoginWindow();
			btnClose.Position = new Vector2(padX + 244f, y);
			_loginWin.AddChild(btnClose);
			y += _btnLoginRefresh.Height + 14f;

			// 按内容真实高度回填窗口尺寸并居中（避免底部留白或溢出）
			_loginWin.Height = y;
			_loginWin.Position = new Vector2((Main.screenWidth - w) / 2f, (Main.screenHeight - y) / 2f);
		}

	// ==================================================================== 浏览器 Cookie 兜底导入

	/// <summary>用系统默认编辑器打开 Cookie 文件（不存在则先创建），供粘贴浏览器 Cookie。</summary>
	private void OpenCookieFile()
	{
		try
		{
			StopQr();
			NeteaseSession.EnsureCookieFile();
			Process.Start(new ProcessStartInfo(NeteaseSession.CookieFilePath) { UseShellExecute = true });
			SetQrText("已用记事本打开文件：粘贴 Cookie 整段并保存，然后点「导入校验」");
		}
		catch (Exception ex)
		{
			SetQrText("打开文件失败：" + ex.Message);
		}
	}

	/// <summary>读取 Cookie 文件、联网校验并落地登录态；成功后无需重进世界即可播放 VIP 曲。</summary>
	private void ImportCookieFile()
	{
		Task.Run(async () =>
		{
			try
			{
				StopQr();
				NeteaseSession.Load();
				if (!NeteaseSession.HasAuth)
				{
					SetQrText("文件中没有 Cookie：请先粘贴含 MUSIC_U= 的完整 Cookie");
					return;
				}
				SetQrText("正在联网校验 Cookie…");
				var (ok, nick) = await NeteaseApi.LoginStatusAsync().ConfigureAwait(false);
				if (ok)
				{
					if (!string.IsNullOrEmpty(nick))
						NeteaseSession.SaveNickname(nick);
					// 置成功态：状态栏与“网易云”按钮外观由主线程 TickQrLogin 统一落定
					lock (_qrLock)
					{
						_qrSuccess = true;
						_qrText = "登录成功：" + (string.IsNullOrEmpty(nick) ? "账号已生效" : nick);
					}
				}
				else
				{
					lock (_qrLock)
						_qrText = "Cookie 无效或已过期：请重新复制浏览器 Cookie";
				}
			}
			catch (Exception ex)
			{
				SetQrText("导入异常：" + ex.Message);
			}
		});
	}

	/// <summary>后台线程安全地更新登录弹窗状态文字。</summary>
	private void SetQrText(string text)
	{
		lock (_qrLock) _qrText = text;
	}

	// 网易云登录（扫码 / Cookie 兜底）

	private static string LoginCookiePathHint()
		=> "\n登录态文件：" + NeteaseSession.CookieFilePath;

		private void ToggleLoginWindow()
		{
			if (_loginWin != null && _loginWin.Visible)
				CloseLoginWindow();
			else
				OpenLoginWindow();
		}

		private void OpenLoginWindow()
		{
			if (_loginWin == null)
				return;
			_loginWin.Visible = true;
			_loginWin.MoveToFront();
			StartQrLogin();
		}

		private void CloseLoginWindow()
		{
			if (_loginWin == null)
				return;
			_loginWin.Visible = false;
			StopQr();
			UpdateLoginUi();
		}

		/// <summary>退出当前登录：清除本地 Cookie/昵称、重置扫码会话，并立即刷新登录态 UI。</summary>
		private void LogoutNetease()
		{
			StopQr();
			NeteaseSession.Clear();
			NeteaseApi.ResetQrLogin();
			_qrImg.Visible = false;
			lock (_qrLock)
			{
				_qrSuccess = false;
				_qrExpired = false;
				_qrText = "已退出登录";
			}
			_qrStatus.Text = "已退出登录（可重新扫码或导入 Cookie）";
			_btnLoginRefresh.SetTextColor(Color.White);
			UpdateLoginUi();
		}

		/// <summary>路径过长时保留末尾（含文件名与最近目录），前缀以省略号替代，避免撑破弹窗。</summary>
		private static string ShortenPath(string path, int maxTail)
		{
			path = (path ?? "").Trim();
			return path.Length > maxTail ? "…" + path[^maxTail..] : path;
		}

		/// <summary>取消/中断扫码轮询后台任务。</summary>
		private void StopQr()
		{
			try { _qrCts?.Cancel(); } catch { /* 忽略 */ }
			_qrCts?.Dispose();
			_qrCts = null;
			lock (_qrLock)
			{
				_qrSuccess = false;
				_qrExpired = false;
			}
		}

		/// <summary>启动/刷新扫码登录：后台申请二维码 → 轮询校验 → 成功后保存 Cookie。</summary>
		private void StartQrLogin()
		{
			StopQr();
			_qrImg.Visible = false;
			_qrStatus.Text = "正在获取二维码…";
			_qrCts = new CancellationTokenSource();
			var ct = _qrCts.Token;

			Task.Run(async () =>
			{
				try
				{
					// 最多轮 5 个二维码。单个 key 遇连续瞬时异常(如 HTTP404)或过期即换新 key，
					// 避免卡死在一个已被镜像/网易云废弃的 key 上一直显示错误。
					for (int attempt = 1; attempt <= 5 && !ct.IsCancellationRequested; attempt++)
					{
						NeteaseApi.ResetQrLogin(); // 每个新 key 重新建立会话，避免跨 key 串 Cookie
						var key = await NeteaseApi.LoginQrKeyAsync().ConfigureAwait(false);
						if (ct.IsCancellationRequested)
							return;
						if (string.IsNullOrEmpty(key.Unikey))
						{
							lock (_qrLock)
								_qrText = (key.Err ?? "获取二维码失败") + $"（第 {attempt}/5 次）";
							try { await Task.Delay(1500, ct).ConfigureAwait(false); }
							catch (TaskCanceledException) { return; }
							continue;
						}
						lock (_qrLock)
						{
							_qrText = "等待扫码…(请用网易云 App 扫码)";
							if (key.Png != null)
							{
								_qrPng = key.Png;
								_loginRev++;
							}
						}

						bool success = false, giveUp = false;
						int transient = 0, captcha = 0;
						for (int i = 0; i < 120 && !ct.IsCancellationRequested; i++)
						{
							var chk = await NeteaseApi.LoginQrCheckAsync(key.Unikey).ConfigureAwait(false);
							if (ct.IsCancellationRequested)
								return;

							lock (_qrLock)
							{
								_qrText = chk.Message;
								if (chk.Code == 803)
								{
									bool ok = NeteaseSession.SaveCookie(chk.Cookie ?? "");
									if (ok && !string.IsNullOrEmpty(chk.Nickname))
										NeteaseSession.SaveNickname(chk.Nickname); // 记录当前账户昵称供按钮展示
									if (ok)
										RefreshAccountNicknameAsync(); // 用已存 Cookie 再权威校验一次昵称
									_qrSuccess = success = true;
									_qrText = ok
										? "登录成功" + (string.IsNullOrEmpty(chk.Nickname) ? "" : "：" + chk.Nickname)
										: "已授权，但本地保存登录态失败";
								}
								else if (chk.Code == 800)
								{
									_qrText = $"二维码已过期，正在自动刷新（{attempt}/5）…";
									giveUp = true;
								}
								else if (chk.Code < 0) // 瞬时异常（含 HTTP404），连续 3 次判定该 key 失效，换新 key
								{
									transient++;
									captcha = 0;
									if (transient >= 3)
									{
										_qrText = $"镜像接口瞬时异常，正在自动更换二维码（{attempt}/5）…";
										giveUp = true;
									}
								}
								else if (chk.Code == 8821) // 风控：行为验证码无法在游戏内完成，引导 Cookie 导入
								{
									captcha++;
									transient = 0;
									if (captcha >= 2)
									{
										_qrText = "扫码被风控拦截（需行为验证码），请点下方[打开Cookie文件]改用浏览器 Cookie";
										giveUp = true;
									}
								}
								else
								{
									transient = 0; // 正常状态（801 等待 / 802 已扫码待确认）重置瞬时计数
									captcha = 0;
								}
							}
							if (success || giveUp)
								break;
							try { await Task.Delay(2000, ct).ConfigureAwait(false); }
							catch (TaskCanceledException) { return; }
						}

						if (success)
							return;
						if (ct.IsCancellationRequested)
							return;
						// 本轮未成功且是最后一次 → 进入稳定的“点刷新”终态；否则外用循环换下一个 key。
						if (attempt >= 5)
						{
							lock (_qrLock)
							{
								_qrExpired = true;
								_qrText = "多次尝试仍未登录成功，点“刷新”重新开始";
							}
						}
					}
				}
				catch (Exception ex)
				{
					lock (_qrLock) _qrText = "登录异常：" + ex.Message;
				}
			});
		}

		/// <summary>每帧在主线程落定登录弹窗：加载二维码纹理、刷新状态文字、登录成功/过期提示。</summary>
		private void TickQrLogin()
		{
			if (_loginWin == null || !_loginWin.Visible)
				return;

			bool success, expired;
			string text;
			byte[]? png;
			int rev;
			lock (_qrLock)
			{
				text = _qrText;
				success = _qrSuccess;
				expired = _qrExpired;
				png = _qrPng;
				rev = _loginRev;
			}

			// 二维码纹理（主线程加载）
			if (png != null && _shownQrRev != rev)
			{
				_shownQrRev = rev;
				try
				{
					using var ms = new MemoryStream(png);
					var tex = Texture2D.FromStream(Main.instance.GraphicsDevice, ms);
					_qrImg.Texture = tex;
					_qrImg.Visible = true;
				}
				catch { _qrImg.Visible = false; }
			}

			if (expired)
			{
				_qrStatus.Text = "二维码已过期，点“刷新”重新获取";
				_btnLoginRefresh.SetTextColor(Color.White);
			}
			else if (success)
			{
				_qrStatus.Text = text;
				_btnLoginRefresh.SetTextColor(new Color(160, 205, 160));
				UpdateLoginUi(); // 已登录：源按钮补上“✓”并刷新右键登录提示
			}
			else
			{
				_qrStatus.Text = text;
				_btnLoginRefresh.SetTextColor(Color.White);
			}
		}

		/// <summary>后台校验登录态并刷新昵称（有登录态才会联网；结果经 SaveNickname 落盘，UI 每帧按需刷新）。</summary>
		private static async void RefreshAccountNicknameAsync()
		{
			try
			{
				var (ok, nick) = await NeteaseApi.LoginStatusAsync().ConfigureAwait(false);
				if (ok && !string.IsNullOrEmpty(nick))
					NeteaseSession.SaveNickname(nick);
			}
			catch { /* 忽略：昵称展示非关键 */ }
		}

		/// <summary>「网易云」源按钮的登录态外观：已登录显示“当前账户昵称”，悬停提示分区右键入口。</summary>
		private void UpdateLoginUi()
		{
			var host = _neteaseLoginHost;
			if (host == null)
				return;
			bool authed = NeteaseSession.HasAuth;
			string nick = authed ? (NeteaseSession.Nickname ?? "") : "";
			string text = authed
				? (nick.Length > 0 ? "网易云·" + ShortAccountName(nick) : "网易云 ✓")
				: "网易云";
			if (host.Text != text)
				host.Text = text;
			host.Tooltip = authed
				? $"已登录：{(nick.Length > 0 ? nick : "账号")}（可播放 VIP/会员曲）。右键查看/刷新登录态，左键切换音源。"
				: ("左键切换音源；右键：网易云扫码登录（登录后可播放 VIP/会员曲）。"
				  + "二维码不好使时，把浏览器 Cookie（含 MUSIC_U=）粘贴到" + LoginCookiePathHint());
		}

		/// <summary>账号昵称太长时截断到按钮内可显示（约 9 个汉字）。</summary>
		private static string ShortAccountName(string name)
		{
			name = (name ?? "").Trim();
			return name.Length > 9 ? name[..9] + "…" : name;
		}

		private static UILabel NewLabel(string text, float scale, Color color)
		{
			var l = new UILabel(text) { Scale = scale };
			l.font = FontAssets.MouseText.Value;
			l.ForegroundColor = color;
			return l;
		}

		private static UIButton MakeButton(string text, float width, Color textColor)
		{
			var b = new UIButton(text) { AutoSize = false };
			b.TextVerticalOffset = 2f;
			b.Width = width;
			b.SetFont(FontAssets.MouseText.Value);
			b.SetTextColor(textColor);
			return b;
		}

		// 显隐

		public void SetOpen(bool open)
		{
			if (_open == open)
				return;
			if (open)
			{
				_open = true;
				_win.Visible = true;
				_win.MoveToFront();
				_needRebuild = true;
				ApplySrcColors();
				NeteaseMp3Player.Poll(); // 先落定一次播放状态，让切过来即可见
			}
			else
			{
				_open = false;
				_win.Visible = false;
				_focused = false;
				_composition = "";
				StopIme();
				CloseLoginWindow(); // 关闭面板时一并收起扫码登录弹窗
			}
		}

		/// <summary>打开/关闭在线音乐点唱台（右下角“点歌”入口）。</summary>
		private void Toggle() => SetOpen(!_open);

		/// <summary>右下角常驻“点歌”入口按钮位置（随屏幕右下角，每帧刷新）。</summary>
		private void PositionToggleButton()
		{
			if (_btnToggle == null)
				return;
			_btnToggle.Position = new Vector2(Main.screenWidth - 92f, Main.screenHeight - 56f);
		}

		/// <summary>切换当前音源：清空旧源结果，下次搜索只查该源。</summary>
		private void SwitchSource(MusicSource s)
		{
			if (_src == s)
				return;
			_src = s;
			_results.Clear();
			_selIdx = -1;
			_needRebuild = true;
			_searchSeq++; // 作废进行中的旧源搜索
			_searchBusy = false;
			_notice = "已切换到 " + SourceLabel(s);
			ApplySrcColors();
		}

		private void ApplySrcColors()
		{
			for (int i = 0; i < _srcBtns.Count && i < SourceOptions.Length; i++)
				_srcBtns[i].SetTextColor(_src == SourceOptions[i] ? new Color(255, 215, 0) : new Color(190, 185, 220));
		}

		/// <summary>离开世界清场：清空搜索词/结果/聚焦/输入接管状态。</summary>
		public void ResetContent()
		{
			_query = "";
			_focused = false;
			_composition = "";
			_imeActive = false;
			_searchBox.Focused = false;
			_searchBox.Text = "";
			_results = new List<SongHit>();
			_searchBusy = false;
			_searchSeq++;
			_notice = null;
			_selIdx = -1;
			_needRebuild = true;
			_showQueue = false;
			_queueSig = "";
			_prevKeys.Clear();
			StopIme();
			CloseLoginWindow(); // 离开世界清场：停掉扫码轮询并收起登录弹窗
			_lyricWin.SetOpen(false); // 歌词浮窗一并收起（下次进世界重新打开）
		}

		/// <summary>光标是否落在本窗口上（输入吞噬判断用）。</summary>
		public bool CursorOverUi()
		{
			if (_attached && _open && _win.Visible && _win.MouseInside)
				return true;
			// 歌词浮窗独立存在，光标在它上面时同样要占用输入
			if (_lyricWin.CursorOverUi())
				return true;
			// 右下角“点歌”入口按钮也占用输入，避免点到它时顺带与世界交互
			if (_attached && _btnToggle != null && _btnToggle.Visible && _btnToggle.MouseInside)
				return true;
			return false;
		}

		// 搜索

		private void RunSearch(string keyword)
		{
			keyword = (keyword ?? "").Trim();
			if (keyword.Length == 0)
			{
				_notice = "先输入关键词再搜索";
				return;
			}

			_searchBusy = true;
			int mySeq = ++_searchSeq;
			_notice = "正在" + SourceLabel(_src) + "搜索 “" + FitToWidth(keyword, 100f) + "”…";

			Task.Run(() =>
			{
				try
				{
					var songs = MusicSources.SearchAsync(_src, keyword, 30).GetAwaiter().GetResult();
					lock (_searchGate)
						_pending = new SearchPending(mySeq, true, songs, "");
				}
				catch (Exception ex)
				{
					lock (_searchGate)
						_pending = new SearchPending(mySeq, false, new List<SongHit>(), ex.Message);
				}
			});
		}

		private void ApplyPendingSearch()
		{
			SearchPending? p;
			lock (_searchGate)
			{
				if (_pending == null || _pending.Seq != _searchSeq)
					return;
				p = _pending;
				_pending = null;
			}

			_searchBusy = false;
			_results = p.Songs;
			_selIdx = -1;
			_needRebuild = true;
			if (!p.Ok)
				_notice = "搜索失败：" + FitToWidth(p.Err, 150f);
			else if (p.Songs.Count == 0)
				_notice = "没有搜到相关歌曲，换个关键词试试";
			else
				_notice = null;
		}

		// 每帧

		public void Tick()
		{
			if (!_attached)
				return;

			// 常驻入口按钮位置随屏幕刷新（右下角“点歌”入口）
			PositionToggleButton();

			// 登录态/昵称随每帧刷新（后台校验昵称落地后这里立即反映到“网易云”源按钮）
			UpdateLoginUi();

			// 播放器下载落定/自然播完检测：只要进了世界就推进（窗口隐藏时后台播放也正常）
			NeteaseMp3Player.Poll();

			// 歌词浮窗独立于点歌面板：面板关着也照常滚动高亮
			_lyricWin.Tick();

			// 输入法激活跟随焦点/窗口/聊天框；窗口关闭时在此解除
			UpdateImeState();

			// Esc 键不受 AllMusicSystem 锁定（用户要求）：先关扫码登录弹窗，再取消搜索框焦点，最后才关闭点唱台。
			if (_open && _win.Visible)
			{
				bool escDown = Keyboard.GetState().IsKeyDown(Keys.Escape);
				bool escPrev = _prevKeys.Contains(Keys.Escape);
				if (escDown && !escPrev)
				{
					if (_loginWin.Visible)
						CloseLoginWindow();
					else if (_focused)
						_focused = false;
					else
						SetOpen(false);
				}
			}

			if (!_open)
				return;

			// 扫码登录弹窗状态推进（后台轮询结果落定主线程）
			TickQrLogin();

			// 借用聊天的收尾由 AllMusicUiSystem.OnMainUpdate 钩子驱动（回车/Esc 关闭聊天框后触发），
			// 此处不再重复处理。

			LayoutWindow();
			_caretTime += 0.016f;
			_searchBox.CaretOn = _focused && (int)(_caretTime / 0.5) % 2 == 0;
			_searchBox.Text = DisplayText;
			_searchBox.Focused = _focused;

			HandleTyping();

			ApplyPendingSearch();

			// 队列模式：当前播放 / 点歌人 / 队列内容变化时才重建行（避免每帧重建）
			if (_showQueue)
			{
				string sig = BuildQueueSig();
				if (sig != _queueSig)
				{
					_queueSig = sig;
					_needRebuild = true;
				}
			}
			else
			{
				_queueSig = "";
			}

			if (_needRebuild)
			{
				_needRebuild = false;
				Rebuild();
			}

			ApplyTabColors();
			UpdateNowRow();

			RefreshStatus();

			// 帧末更新按键历史，供下一帧做“刚按下”边缘检测
			_prevKeys.Clear();
			foreach (var k in Keyboard.GetState().GetPressedKeys())
				_prevKeys.Add(k);
		}

		private void HandleTyping()
		{
			if (!_focused || !_win.Visible)
				return;
			if (AwaitingChat)
				return; // 借用聊天框期间由 vanilla 聊天承载输入，不在此处理字符键
			if (WritingTextActive())
				return; // 真实聊天/重命名等输入状态中不抢键（IME 事件已单独负责文本）

			// 控制键边缘检测（IME 激活期间也生效：退格删字符、回车搜索、Esc 取消焦点）
			var state = Keyboard.GetState();
			foreach (var key in state.GetPressedKeys())
			{
				if (_prevKeys.Contains(key))
					continue;
				if (key == Keys.Escape)
				{
					_focused = false;
					continue;
				}
				if (key == Keys.Enter)
				{
					RunSearch(_query);
					continue;
				}
				if (key == Keys.Back)
				{
					Backspace();
					continue;
				}
			}
		}

		/// <summary>退格：优先清掉未确认的组成串，否则删掉已确认文本的最后一个字符。</summary>
		private void Backspace()
		{
			if (_composition.Length > 0)
			{
				_composition = "";
				return;
			}
			if (_query.Length > 0)
				_query = _query[..^1];
		}

		private static bool WritingTextActive()
		{
			try { return Terraria.GameInput.PlayerInput.WritingText; }
			catch { return false; }
		}

		/// <summary>进入“借用聊天框输入”状态：打开 vanilla 聊天框承载中文输入法，停用自绘 IME。</summary>
		private void StartBorrowChat()
		{
			if (!_attached || !_open)
				return;
			AwaitingChat = true;
			_focused = true;
			_chatBuf = "";
			_commitRequested = false;
			_composition = "";
			StopIme();
			try { Main.OpenPlayerChat(); } catch { AwaitingChat = false; _focused = false; }
		}

		/// <summary>
		/// 每帧把当前聊天框文本完整快照镜像进缓冲区（不清理 chatText，保留 vanilla 退格/方向键编辑）。
		/// 完整性由 AllMusicUiSystem.OnMainUpdate 每帧调用保证；回车时由该钩子先清空 chatText 防广播，
		/// 因此这段文字不会作为聊天消息发送到服务器。
		/// </summary>
		internal void MirrorChat(string? text)
		{
			if (!AwaitingChat)
				return;
			_chatBuf = string.IsNullOrEmpty(text) ? "" : (text.Length > 60 ? text[..60] : text);
		}

		/// <summary>用户在聊天框里按了回车：记录“提交搜索”意图（由钩子在回车边沿调用）。</summary>
		internal void MarkCommitRequested() => _commitRequested = true;

		/// <summary>结束借用：聊天框已关闭（含水回车/Esc）。回车→把镜像文本变成搜索词并搜索；Esc→取消。</summary>
		internal void FinalizeBorrowChat()
		{
			if (!AwaitingChat)
				return;
			AwaitingChat = false;
			string q = _commitRequested ? _chatBuf.Trim() : "";
			_commitRequested = false;
			_chatBuf = "";
			_focused = false;
			if (q.Length > 0)
			{
				SetQueryExternal(q);
				RunSearch(q);
			}
		}

		/// <summary>点“搜索”按钮时：若正借用聊天框，则兜底镜像当前文本并立即提交（关掉聊天、触发搜索）。</summary>
		private void CommitBorrowIfActive()
		{
			if (!AwaitingChat)
			{
				RunSearch(_query);
				return;
			}
			MirrorChat(Main.chatText);           // 兜底：把回车前可能遗漏的最新字符也镜像进来
			if (Main.chatText != null)
				Main.chatText = "";              // 关聊天，避免再次发送/广播
			Main.drawingPlayerChat = false;
			_commitRequested = true;
			FinalizeBorrowChat();
		}

		/// <summary>用于显示在搜索框里的当前文本（借用聊天时显示已捕获内容）。</summary>
		private string DisplayText => AwaitingChat ? _chatBuf + "…" : _query + _composition;

		/// <summary>
		/// IME 生命周期：搜索框获得焦点且条件满足时显式 StartTextInput 呼出系统输入法，
		/// 并置 Main.CurrentInputTextTakerOverride 抢占“文本接管者”+ WritingText=true 保持态、
		/// 同时屏蔽快捷栏/热键误操作；失焦/关窗时对称 StopTextInput。
		/// </summary>
		private void UpdateImeState()
		{
			bool shouldTake = _open && _focused && _win.Visible && !Main.drawingPlayerChat && !AwaitingChat;
			if (shouldTake != _imeActive)
			{
				if (shouldTake)
					StartIme();
				else
					StopIme();
			}
		}

		private void StartIme()
		{
			if (_imeActive)
				return;
			_imeActive = true;
			_composition = "";
			try
			{
				Main.CurrentInputTextTakerOverride = _chatToken;
				Terraria.GameInput.PlayerInput.WritingText = true;
				TextInputEXT.StartTextInput();
			}
			catch { /* 无窗口等环境忽略 */ }
		}

		private void StopIme()
		{
			if (!_imeActive)
				return;
			_imeActive = false;
			_composition = "";
			try
			{
				if (ReferenceEquals(Main.CurrentInputTextTakerOverride, _chatToken))
					Main.CurrentInputTextTakerOverride = null;
				Terraria.GameInput.PlayerInput.WritingText = false;
				TextInputEXT.StopTextInput();
			}
			catch { /* 忽略 */ }
		}

		/// <summary>鼠标类操作（清空/外部改动）改动关键词时同步状态。</summary>
		private void SetQueryExternal(string s)
		{
			_query = s.Length > 60 ? s[..60] : s;
			_composition = "";
		}

		/// <summary>OS 确认字符：仅在本搜索框聚焦且 IME 激活时追加为已确认文本。</summary>
		private void OnOsCommitted(char c)
		{
			if (!_attached || !_open || !_focused || !_win.Visible || !_imeActive)
				return;
			if (c == '\b' || c == '\r' || c == '\n' || c < ' ')
				return;
			if (_query.Length < 60)
				_query += c;
		}

		/// <summary>输入法未确认的组成串（拼音/候选），空串表示无进行中组成。仅用于预览。</summary>
		private void OnOsComposition(string s)
		{
			if (!_attached || !_open || !_focused || !_win.Visible)
				return;
			_composition = s ?? "";
		}

		private void LayoutWindow()
		{
			if (_win.Parent == null)
				return;
			float maxX = MasterView.gameScreen.Width - WinW - 8f;
			float maxY = MasterView.gameScreen.Height - WinH - 8f;
			_win.Position = new Vector2(MathHelper.Clamp(_win.Position.X, 0f, Math.Max(0f, maxX)),
										MathHelper.Clamp(_win.Position.Y, 0f, Math.Max(0f, maxY)));
		}

		private void Rebuild()
		{
			_list.ClearContent();
			_rowBtns.Clear();
			_list.ContentHeight = 0;

			if (_showQueue)
			{
				BuildQueueRows();
				return;
			}

			if (_results.Count == 0)
				return;

			_list.ContentHeight = _results.Count * (RowH + RowGap);
			float rowW = WinW - Pad * 2 - 24f;
			for (int i = 0; i < _results.Count; i++)
			{
				var hit = _results[i];
				var tr = hit.Track;
				// Note（VIP/付费/专辑等）只是搜索元数据的提示，未必代表真实可播性——
				// 网易云不少带 VIP 标记的歌匿名仍能取到流，不能据此整行禁点。一律可点，
				// 真正取不到流时由播放器在下方/聊天给出明确失败提示。
				string text = $"[{i + 1}] {tr.Name} {tr.SourceTag}{hit.Note}  {hit.Artists}  {Fmt(tr.DurationMs)}";
				var btn = MakeButton(FitToWidth(text, rowW - 10f), rowW, Color.White);
				btn.Position = new Vector2(2f, i * (RowH + RowGap));
				int idx = i;
				// 左键 = 加入播放列表（空闲时也会自动起播）；右键 = 直接播放（打断当前，立即该曲）
				btn.onLeftClick += (_, _) =>
				{
					_selIdx = idx;
					AllMusicNetService.Request(AllMusicMessageType.RequestQueueAdd, tr.Key);
					_needRebuild = true; // 下一帧重绘高亮，避免在事件分发中改控件树
				};
				btn.onRightClick += (_, _) => AllMusicNetService.Request(AllMusicMessageType.RequestForcePlay, tr.Key);
				btn.Tooltip = hit.Note.Length > 0
					? $"左键加入列表 / 右键直接播：{tr.Name} {tr.SourceTag}（标记{hit.Note}，若无法取流会提示）"
					: $"左键加入列表 / 右键直接播：{tr.Name} {tr.SourceTag}（{hit.Artists}）";
				_list.AddChild(btn);
				_rowBtns.Add(btn);
			}

			for (int i = 0; i < _rowBtns.Count; i++)
				_rowBtns[i].SetTextColor(i == _selIdx ? new Color(255, 215, 0) : Color.White);
		}

		// 点歌队列视图

		/// <summary>队列模式内容签名：当前播放/点歌人 + 每首队列歌/点歌人（变化才重建）。</summary>
		private string BuildQueueSig()
		{
			var parts = new List<string>(AllMusicNetService.QueueItems.Count + 4)
			{
				AllMusicNetService.IsPlaying ? "1" : "0",
				AllMusicNetService.CurrentName ?? "",
				AllMusicNetService.CurrentAdder,
			};
			foreach (var it in AllMusicNetService.QueueItems)
				parts.Add(it.Name + "\u0001" + it.Adder);
			return string.Join("\u0002", parts);
		}

		private void BuildQueueRows()
		{
			float rowW = WinW - Pad * 2 - 24f;
			int row = 0;

			// 当前播放行（信息展示，不可点击）
			bool hasNow = AllMusicNetService.IsPlaying
				&& !string.IsNullOrWhiteSpace(AllMusicNetService.CurrentName);
			if (hasNow)
			{
				string adder = AllMusicNetService.CurrentAdder ?? "";
				string text = "▶ " + AllMusicNetService.CurrentName!
					+ (adder.Length > 0 ? $"（由 {adder} 点）" : "");
				var btn = MakeButton(FitToWidth(text, rowW - 10f), rowW, new Color(255, 215, 0));
				btn.Position = new Vector2(2f, row * (RowH + RowGap));
				btn.Tooltip = "当前正在播放（不可移出）";
				_list.AddChild(btn);
				_rowBtns.Add(btn);
				row++;
			}

			var items = AllMusicNetService.QueueItems;
			for (int i = 0; i < items.Count; i++)
			{
				var it = items[i];
				int shown = i + 1; // 1-base
				string text = $"{shown}. {it.Name}"
					+ (it.Adder.Length > 0 ? $"　由 {it.Adder}" : "");
				var btn = MakeButton(FitToWidth(text, rowW - 10f), rowW, Color.White);
				btn.Position = new Vector2(2f, row * (RowH + RowGap));
				int idx = shown;
				btn.onRightClick += (_, _) => HandleQueueRowRemove(idx, it.Name);
				btn.Tooltip = it.Adder.Length > 0
					? $"#{shown}（{it.Name} 由 {it.Adder} 点）· 连续右键两次移出队列"
					: $"#{shown} {it.Name} · 连续右键两次移出队列";
				_list.AddChild(btn);
				_rowBtns.Add(btn);
				row++;
			}

			if (row == 0)
			{
				var hint = MakeButton("歌单为空：到「搜 歌」里左键入队 / 右键立即播放吧", rowW, new Color(150, 155, 190));
				hint.Position = new Vector2(2f, 0f);
				hint.Tooltip = "点歌后这里会列出等待播放的歌曲和点歌人";
				_list.AddChild(hint);
				_rowBtns.Add(hint);
			}

			_list.ContentHeight = row * (RowH + RowGap);
		}

		/// <summary>队列行双击右键确认移出：第一次右键仅提示，约 0.9 秒内第二次右键同一行才真正移出。</summary>
		private void HandleQueueRowRemove(int idx, string name)
		{
			int now = Environment.TickCount;
			if (_rmRcRow == idx && now - _rmRcTick >= 0 && now - _rmRcTick < 900)
			{
				_rmRcRow = -1;
				_rmRcTick = 0;
				AllMusicNetService.Request(AllMusicMessageType.RequestQueueRemove, idx.ToString());
				_needRebuild = true;
			}
			else
			{
				_rmRcRow = idx;
				_rmRcTick = now;
				AllMusicNetService.Messsage($"再次右键「{name}」确认移出队列", Color.Orange);
			}
		}

		/// <summary>两个展示模式按钮的高亮色（歌词按钮按其浮窗开合态上色）。</summary>
		private void ApplyTabColors()
		{
			if (_btnTabSearch == null || _btnTabQueue == null)
				return;
			_btnTabSearch.SetTextColor(_showQueue ? new Color(190, 185, 220) : new Color(255, 215, 0));
			_btnTabQueue.SetTextColor(_showQueue ? new Color(255, 215, 0) : new Color(190, 185, 220));
			_btnLyric?.SetTextColor(_lyricWin.Visible ? new Color(150, 245, 200) : new Color(190, 185, 220));
		}

		/// <summary>每帧刷新“当前播放”文本与进度条（本地播放位置）。</summary>
		private void UpdateNowRow()
		{
			if (_nowLabel == null || _nowBar == null)
				return;
			NeteaseMp3Player.GetStatus(out var phase, out var song, out string msg, out bool paused);
			string text;
			float val = 0f;

			if (phase == Mp3Phase.Playing && song != null)
			{
				long dur = song.Value.NetDurationMs;
				long pos = dur > 0 ? Math.Min(NeteaseMp3Player.GetPositionMs(), dur) : 0;
				text = (paused ? "已暂停：" : "♪ 播放中：") + song.Value.DisplayName
					+ (dur > 0 ? $"   {Fmt((int)pos)} / {Fmt((int)dur)}" : "");
				if (dur > 0)
					val = Math.Clamp(pos / (float)dur, 0f, 1f);
			}
			else if (phase == Mp3Phase.Prepared && song != null)
			{
				text = "♪ 已就绪，等待同步起播：" + song.Value.DisplayName;
			}
			else if (phase == Mp3Phase.Downloading && song != null)
			{
				text = "♪ 缓冲中：" + song.Value.DisplayName + " …";
			}
			else if (phase == Mp3Phase.Failed)
			{
				text = "无法播放：" + FitToWidth(msg, 320f);
			}
			else if (!string.IsNullOrEmpty(AllMusicNetService.CurrentName))
			{
				string adder = AllMusicNetService.CurrentAdder ?? "";
				text = "♪ 待播放：" + AllMusicNetService.CurrentName
					+ (adder.Length > 0 ? $"（由 {adder} 点）" : "");
			}
			else
			{
				text = "当前无播放 · 点歌后自动开播";
			}

			_nowLabel.Text = FitToWidth(text, WinW - Pad * 2 - 4f, 0.72f);
			_nowBar.Value01 = val;
			// 只要本机在播放/就绪/缓冲（或权威侧有当前曲）就常显，比例为 0 也画空轨道：
			// 避免“刚开始播/切歌瞬间”进度条闪没。
			bool localActive = phase is Mp3Phase.Playing or Mp3Phase.Prepared or Mp3Phase.Downloading;
			_nowBar.Visible = (localActive && song != null) || AllMusicNetService.IsPlaying;
		}

		private void RefreshStatus()
		{
			// 本机在线播放器状态优先（下载中/准备/失败/播放中的信息需要一直可见）
			NeteaseMp3Player.GetStatus(out var phase, out var song, out string msg, out bool paused);
			if (phase == Mp3Phase.Playing && song != null)
			{
				SetStatusText((paused ? "已暂停：" : "♪ 播放中：") + FitToWidth(song.Value.DisplayName, 150f));
			}
			else if (phase == Mp3Phase.Downloading)
			{
				SetStatusText("下载中：" + FitToWidth(song?.DisplayName ?? "", 120f) + " …");
			}
			else if (phase == Mp3Phase.Prepared)
			{
				SetStatusText("已就绪，等待同步起播…");
			}
			else if (phase == Mp3Phase.Failed)
			{
				SetStatusText("无法播放：" + FitToWidth(msg, 160f));
			}
			else if (_searchBusy)
			{
				SetStatusText(_notice ?? "正在搜索…");
			}
			else if (_results.Count > 0)
			{
				SetStatusText($"共 {_results.Count} 条结果（{SourceLabel(_src)}） · 空闲即播、播放中入队列 · 无法取流的歌会提示");
			}
			else if (_notice != null)
			{
				SetStatusText(_notice); // 搜索失败 / 无结果 / 空关键词提示
			}
			else
			{
				int q = AllMusicNetService.QueueItems.Count;
				SetStatusText($"输入关键词搜索 · 队列 {q} 首 · 「播放队列」页可看列表/点歌人");
			}

			int vol = (int)(NeteaseMp3Player.Volume * 100f);
			_volLabel.Text = $"MP3音量 {vol}%";
			_volSlider.Value = NeteaseMp3Player.Volume;
		}

		private void SetStatusText(string text)
		{
			if (_status.Text != text)
				_status.Text = text;
		}

		// 文本工具

		private static string FitToWidth(string s, float maxPx, float scale = 0.72f)
		{
			if (string.IsNullOrEmpty(s) || maxPx <= 0f)
				return s ?? "";
			var font = FontAssets.MouseText.Value;
			if (font.MeasureString(s).X * scale <= maxPx)
				return s;
			string r = s;
			while (r.Length > 0 && font.MeasureString(r + "…").X * scale > maxPx)
				r = r[..^1];
			return r + "…";
		}

		private static string Fmt(int ms)
		{
			if (ms <= 0) return "";
			long t = ms / 1000;
			return $"{t / 60}:{t % 60:D2}";
		}
	}

	/// <summary>简易搜索输入框：左侧对齐文本 + 闪烁光标，可点击获得焦点（由宿主每帧喂 Text/Focused/CaretOn）。</summary>
	internal sealed class UIKitSearchBox : UIView
	{
		public string Text { get; set; } = "";
		public bool Focused { get; set; }
		public bool CaretOn { get; set; }
		public string Hint { get; set; } = "";

		public UIKitSearchBox()
		{
			BackgroundColor = new Color(20, 26, 64, 235);
		}

		public override void Draw(SpriteBatch spriteBatch)
		{
			if (!Visible)
				return;

			var pos = DrawPosition;
			Utils.DrawInvBG(spriteBatch, pos.X, pos.Y, Width, Height,
				Focused ? new Color(16, 22, 58, 245) : new Color(22, 26, 58, 230));

			var font = FontAssets.MouseText.Value;
			bool empty = Text.Length == 0;
			string shown = empty ? Hint : Text;
			float scale = 0.78f;
			var color = empty ? new Color(140, 145, 175) : Color.White;

			// 截断到可用宽（右侧留给光标）
			float maxW = Width - 14f - (CaretOn && Focused ? font.MeasureString("|").X * scale : 0f);
			string draw = shown;
			while (font.MeasureString(draw).X * scale > maxW && draw.Length > 0)
				draw = draw[..^1];
			if (draw != shown && !empty)
				draw += "…";

			Vector2 textPos = pos + new Vector2(7f, (Height - font.MeasureString("H").Y * scale) / 2f - 1f);
			spriteBatch.DrawString(font, draw, textPos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);

			if (Focused && CaretOn && !empty)
			{
				float cx = textPos.X + font.MeasureString(draw).X * scale + 1f;
				spriteBatch.DrawString(font, "|", new Vector2(cx, textPos.Y), Color.White, 0f, Vector2.Zero, scale,
					SpriteEffects.None, 0f);
			}
			else if (Focused && CaretOn && empty)
			{
				spriteBatch.DrawString(font, "|", new Vector2(textPos.X + font.MeasureString(Hint).X * scale + 1f, textPos.Y),
					new Color(140, 145, 175), 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
			}

			base.Draw(spriteBatch);
		}
	}

	/// <summary>简单自绘图片视图：直接持有运行时生成的 Texture2D（二维码），不依赖 ReLogic Asset。</summary>
	internal sealed class QrImageView : UIView
	{
		public Texture2D? Texture;

		protected override float GetWidth() => Texture?.Width ?? 0f;

		protected override float GetHeight() => Texture?.Height ?? 0f;

		public override void Draw(SpriteBatch spriteBatch)
		{
			if (Visible && Texture != null)
				spriteBatch.Draw(Texture, DrawPosition, null, Color.White * Opacity, 0f, Origin, Scale, SpriteEffects.None, 0f);
			base.Draw(spriteBatch);
		}
	}

	/// <summary>播放进度条：与音量滑条同款边框轨道（barEdge 三片式）+ 内圈已播放填充，做出“嵌在框内”的观感。</summary>
	internal sealed class UISimpleProgress : UIView
	{
		/// <summary>内圈填充相对轨道的内缩（像素）：左右避开端盖、上下留边以露出边框。</summary>
		private const int InsetX = 2, InsetY = 3;

		public float Value01 { get; set; }

		public Color FillColor { get; set; } = new Color(96, 200, 255);

		/// <summary>高度跟随轨道纹理，保证与底部音量滑条同一视觉厚度。</summary>
		protected override float GetHeight() => UISlider.barTexture?.Value.Height ?? 24f;

		public override void Draw(SpriteBatch spriteBatch)
		{
			if (!Visible)
				return;

			var asset = UISlider.barTexture;
			if (asset == null)
			{
				FallbackDraw(spriteBatch);
				return;
			}

			Texture2D track = asset.Value;
			Vector2 pos = DrawPosition;
			int capW = track.Width;               // 左右端盖宽
			int midW = (int)Width - capW * 2;

			// 轨道：与 UISlider.DrawBackground 一致的三片式画法（左端盖 + 中段 + 右端盖翻转）
			spriteBatch.Draw(track, pos, null, Color.White * Opacity, 0f, Origin, 1f, SpriteEffects.None, 0f);
			if (midW > 0)
			{
				pos.X += capW;
				spriteBatch.Draw(UISlider.BarFill, pos - Origin, null, Color.White * Opacity, 0f, Vector2.Zero,
					new Vector2(midW, 1f), SpriteEffects.None, 0f);
				pos.X += midW;
				spriteBatch.Draw(track, pos, null, Color.White * Opacity, 0f, Origin, 1f, SpriteEffects.FlipHorizontally, 0f);
			}

			// 已播放填充：贴在轨道内圈，宽度随进度铺满框内
			float v = MathHelper.Clamp(Value01, 0f, 1f);
			if (v > 0f)
			{
				Vector2 topLeft = DrawPosition - Origin;
				int innerX = (int)topLeft.X + capW + InsetX;
				int innerW = Math.Max(1, (int)Width - capW * 2 - InsetX * 2);
				int innerY = (int)topLeft.Y + InsetY;
				int innerH = Math.Max(1, (int)track.Height - InsetY * 2);
				spriteBatch.Draw(ModUtils.DummyTexture,
					new Rectangle(innerX, innerY, Math.Max(2, (int)(innerW * v)), innerH), FillColor);
			}
			base.Draw(spriteBatch);
		}

		/// <summary>轨道纹理不可用时退化为纯色底 + 填充（保底可见）。</summary>
		private void FallbackDraw(SpriteBatch spriteBatch)
		{
			Vector2 topLeft = DrawPosition - Origin;
			spriteBatch.Draw(ModUtils.DummyTexture,
				new Rectangle((int)topLeft.X, (int)topLeft.Y, (int)Width, (int)Height), new Color(10, 14, 40, 255));
			float v = MathHelper.Clamp(Value01, 0f, 1f);
			if (v > 0f)
				spriteBatch.Draw(ModUtils.DummyTexture,
					new Rectangle((int)topLeft.X + InsetX, (int)topLeft.Y + InsetY,
						Math.Max(2, (int)((Width - InsetX * 2) * v)), Math.Max(1, (int)Height - InsetY * 2)), FillColor);
			base.Draw(spriteBatch);
		}
	}
}