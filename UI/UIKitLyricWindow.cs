// ============================================================================
// 独立歌词窗口：跟随当前播放曲目自动取词，播放器式滚动 + 当前行高亮。
//  - 歌词源：网易云官方 /api/song/lyric（NeteaseLyric 负责取词、LRC 解析与内存缓存）
//  - 高亮位置：本机播放位置（NeteaseMp3Player.GetPositionMs）二分定位当前行，天然与音频对齐
//  - 超长行：按窗口宽度自动换行（字号不变，行高自适应）
//  - 滚轮：可手动翻看；停手 3 秒后平滑回到当前行
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AllMusicMod.Netease;
using AllMusicMod.Net;
using AllMusicMod.UIKit;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameInput;

namespace AllMusicMod.UI
{
	/// <summary>歌词浮窗（独立于点歌面板，可各自开合）。入口：点歌面板的「歌词」按钮。</summary>
	internal sealed class UIKitLyricWindow
	{
		public const float WinW = 560f;
		public const float WinH = 560f;
		private const float SidePad = 10f;
		private const float FootPad = 10f;
		private const float HeadTop = 4f;   // 顶部控件起点
		private const float HeadGap = 8f;   // 顶部控件与歌词视图的间距

		/// <summary>本次会话是否显示翻译（跨窗口开合保留）。</summary>
		private static bool _showTranslation = true;

		private UIWindow _win = null!;
		private UILyricView _view = null!;
		private UILabel _title = null!;
		private UIButton _btnClose = null!;
		private UIButton _btnTrans = null!;

		private bool _built;
		private long _wantedId;              // 当前展示/加载中的歌曲 id（0=无曲）
		private LyricDoc? _doc;
		private string _stateText = NoSongText;
		private string _titleShown = "";

		private readonly object _gate = new();
		private LyricDoc? _pending;
		private long _pendingId;

		private const string NoSongText = "暂无歌曲播放 · 点歌后自动显示歌词";

		/// <summary>窗口当前是否可见（AllMusicUiSystem 据此做输入吞噬与滚轮锁）。</summary>
		public bool Visible => _built && _win.Visible;

		// ==================================================================== 生命周期

		public void Attach()
		{
			if (!_built)
			{
				Build();
				_built = true;
			}
			MasterView.gameScreen.AddChild(_win);
			_win.Visible = false;
		}

		public void Detach()
		{
			if (!_built)
				return;
			MasterView.gameScreen.RemoveChild(_win);
			_win.Visible = false;
		}

		public void Toggle() => SetOpen(!Visible);

		public void SetOpen(bool open)
		{
			if (!_built)
				return;
			_win.Visible = open;
			if (open)
			{
				_win.MoveToFront();
				Refresh(true); // 打开即按当前曲目刷新一次（不必等下一帧）
			}
		}

		/// <summary>光标是否落在歌词窗口内（宿主据此占用输入，避免点歌词时与世界交互）。</summary>
		public bool CursorOverUi() => Visible && _win.MouseInside;

		private void Build()
		{
			_win = new UIWindow
			{
				CanMove = true,
				ClickAndDrag = true,
				Width = WinW,
				Height = WinH,
			};
			_win.Position = new Vector2((Main.screenWidth - WinW) / 2f + 40f, (Main.screenHeight - WinH) / 2f);
			_win.BackgroundColor = new Color(20, 14, 56, 255) * 0.88f;
			_win.Visible = false;

			_title = NewLabel("歌词", 0.95f, Color.White);
			_title.Position = new Vector2(SidePad + 2f, HeadTop + 6f);
			_win.AddChild(_title);

			// 关闭按钮在右上；其真实高度（按钮纹理较高）决定标题区高度，避免压住按钮底部
			const float btnW = 74f, btnGap = 8f;
			_btnClose = new UIButton("关闭") { AutoSize = false, Width = btnW, TextVerticalOffset = 2f };
			_btnClose.SetFont(FontAssets.MouseText.Value);
			_btnClose.SetTextColor(Color.White);
			_btnClose.Position = new Vector2(WinW - btnW - SidePad, HeadTop);
			_btnClose.onLeftClick += (_, _) => SetOpen(false);
			_btnClose.Tooltip = "关闭歌词窗口（点歌面板的「歌词」按钮可再次打开）";
			_win.AddChild(_btnClose);

			_btnTrans = new UIButton("翻译") { AutoSize = false, Width = btnW, TextVerticalOffset = 2f };
			_btnTrans.SetFont(FontAssets.MouseText.Value);
			_btnTrans.Position = new Vector2(WinW - btnW * 2 - btnGap - SidePad, HeadTop);
			_btnTrans.onLeftClick += (_, _) => ToggleTranslation();
			_win.AddChild(_btnTrans);

			// 标题随按钮垂直居中
			_title.Position = new Vector2(SidePad + 2f, HeadTop + (_btnClose.Height - _title.Height) / 2f);

			float viewTop = HeadTop + _btnClose.Height + HeadGap;
			_view = new UILyricView
			{
				Width = WinW - SidePad * 2f,
				Height = WinH - viewTop - FootPad,
			};
			_view.Position = new Vector2(SidePad, viewTop);
			_view.ShowTranslation = _showTranslation;
			_win.AddChild(_view);
			UpdateTransButton();
		}

		/// <summary>切换「翻译」显示（重建歌词排版：带翻译的行占两行）。</summary>
		private void ToggleTranslation()
		{
			_showTranslation = !_showTranslation;
			_view.ShowTranslation = _showTranslation;
			UpdateTransButton();
		}

		private void UpdateTransButton()
		{
			if (_btnTrans == null)
				return;
			_btnTrans.SetTextColor(_showTranslation ? new Color(150, 245, 200) : new Color(190, 185, 220));
			_btnTrans.Tooltip = _showTranslation
				? "当前：显示翻译（原文下方小字），点击隐藏"
				: "当前：不显示翻译，点击显示（需该曲有翻译歌词）";
		}

		private static UILabel NewLabel(string text, float scale, Color color)
		{
			var l = new UILabel(text) { Scale = scale };
			l.font = FontAssets.MouseText.Value;
			l.ForegroundColor = color;
			return l;
		}

		// ==================================================================== 每帧

		/// <summary>由点歌面板每帧调用（窗口隐藏时空转）。</summary>
		public void Tick()
		{
			if (!_built || !_win.Visible)
				return;
			Refresh(false);
		}

		private void Refresh(bool opening)
		{
			// 1) 切歌 → 重新取词
			long id = AllMusicNetService.CurrentNetId;
			if (opening || id != _wantedId)
			{
				_wantedId = id;
				_doc = null;
				_stateText = id > 0 ? "正在获取歌词…" : NoSongText;
				_view.SetContent(null, _stateText);
				if (id > 0)
				{
					long want = id;
					Task.Run(async () =>
					{
						LyricDoc doc = await NeteaseLyric.FetchAsync(want).ConfigureAwait(false);
						lock (_gate)
						{
							_pending = doc;
							_pendingId = want;
						}
					});
				}
			}

			// 2) 落定后台取词结果
			LyricDoc? done;
			long doneId;
			lock (_gate)
			{
				done = _pending;
				doneId = _pendingId;
				_pending = null;
			}
			if (done != null && doneId == _wantedId)
			{
				_doc = done;
				_stateText = done.IsEmpty ? "这首暂无歌词" : "";
				_view.SetContent(done, _stateText);
			}

			// 3) 播放位置 → 当前行（本机播放器位置，和音频天然对齐）
			int line = -1;
			if (_doc is { IsEmpty: false })
				line = _doc.IndexAt(NeteaseMp3Player.GetPositionMs());
			_view.SetCurrentLine(line);

			// 4) 标题：歌词 · 当前曲
			string title = BuildTitle();
			if (title != _titleShown)
			{
				_titleShown = title;
				_title.Text = title;
			}
		}

		private static string BuildTitle()
		{
			string song = AllMusicNetService.CurrentName ?? "";
			string t = song.Length == 0 ? "歌词" : "歌词 · " + song;
			return t.Length > 26 ? t[..26] + "…" : t;
		}
	}

	/// <summary>
	/// 歌词绘制视图：只画可视区行，当前行垂直居中并高亮，上下行按距离递减亮度；
	/// 超长行按宽度自动换行；滚轮手动翻看，停手后平滑回中。
	/// </summary>
	internal sealed class UILyricView : UIView
	{
		private const float BaseScale = 0.82f;    // 非当前行字号
		private const float CurBoost = 1.10f;     // 当前行放大系数
		private const float RowH = 30f;           // 原文行占高
		private const float TransRowH = 26f;      // 翻译行占高
		private const float TransScale = 0.7f;    // 翻译行字号（相对非当前行）
		private const float SidePad = 14f;        // 视图内左右留白
		private const int FollowPauseMs = 3000;   // 手动翻看后回中延迟

		private static readonly Color CurrentColor = new(255, 240, 210);
		private static readonly Color NormalColor = new(198, 205, 235);
		private static readonly Color TransColor = new(148, 168, 215);
		private static readonly Color TransCurrentColor = new(226, 224, 200);

		private sealed record Row(int Line, string Text, bool IsTrans, float Height);

		private readonly RasterizerState _raster = new() { ScissorTestEnable = true };
		private readonly List<Row> _rows = new();
		private readonly List<float> _rowTop = new();
		private float _contentH;
		private float _builtForW = -1f;
		private LyricDoc? _doc;
		private string _emptyText = "";
		private int _curLine = -1;
		private float _scroll;
		private int _resumeFollowTick;
		private int _lastWheel;
		private bool _showTranslation;

		/// <summary>是否在原文下方显示翻译（切换会重排歌词行高）。</summary>
		public bool ShowTranslation
		{
			get => _showTranslation;
			set
			{
				if (_showTranslation == value)
					return;
				_showTranslation = value;
				Rebuild();
			}
		}

		/// <summary>设置歌词文档（null = 还没有内容，显示 emptyText）。</summary>
		public void SetContent(LyricDoc? doc, string emptyText)
		{
			_doc = doc;
			_emptyText = emptyText ?? "";
			_curLine = -1;
			_scroll = 0f;
			_resumeFollowTick = 0;
			Rebuild();
		}

		/// <summary>设置当前高亮行（-1 = 还没唱到第一句）。</summary>
		public void SetCurrentLine(int line) => _curLine = line;

		public override void Update()
		{
			base.Update();
			if (!Visible)
				return;

			if (Math.Abs(_builtForW - Width) > 0.5f)
				Rebuild(); // 视图宽度变化（一般不会）时按新宽重排

			int nowWheel = Mouse.GetState().ScrollWheelValue;
			int rawDelta = nowWheel - _lastWheel;
			_lastWheel = nowWheel;

			if (IsMouseInside())
			{
				PlayerInput.LockVanillaMouseScroll("AllMusicMod");
				int delta = UIScrollView.PendingWheelDelta; // 宿主在输入阶段捕获的增量
				UIScrollView.PendingWheelDelta = 0;
				if (delta == 0)
					delta = rawDelta;
				PlayerInput.ScrollWheelDelta = 0;
				PlayerInput.ScrollWheelDeltaForUI = 0;

				if (delta != 0)
				{
					_resumeFollowTick = Environment.TickCount + FollowPauseMs;
					_scroll = ClampScroll(_scroll - delta);
				}
			}

			// 未在手动翻看 → 平滑跟随当前行（播放器式滑动）
			if (Environment.TickCount >= _resumeFollowTick)
			{
				float target = FollowTarget();
				float diff = target - _scroll;
				_scroll += Math.Abs(diff) < 0.5f ? diff : diff * 0.16f;
			}
			_scroll = ClampScroll(_scroll);
		}

		// ------------------------------------------------------------------ 布局

		/// <summary>按当前文档与宽度重排：逐行换行成显示行，并累计每行顶部 Y。</summary>
		private void Rebuild()
		{
			_rows.Clear();
			_rowTop.Clear();
			_builtForW = Width;

			if (_doc is { IsEmpty: false })
			{
				var font = FontAssets.MouseText.Value;
				// 当前行会放大 CurBoost 倍，按此收窄换行宽度，保证高亮行也不越界
				float maxW = Math.Max(40f, (Width - SidePad * 2f) / CurBoost);
				for (int i = 0; i < _doc.Lines.Count; i++)
				{
					LyricLine line = _doc.Lines[i];
					foreach (string seg in Wrap(line.Text, font, maxW))
						_rows.Add(new Row(i, seg, false, RowH));

					// 翻译：接在原文行下面（小字号、暗色），超长同样自动换行
					if (_showTranslation && line.Translation.Length > 0)
					{
						foreach (string seg in Wrap(line.Translation, font, maxW))
							_rows.Add(new Row(i, seg, true, TransRowH));
					}
				}
			}

			float y = 0f;
			for (int i = 0; i < _rows.Count; i++)
			{
				_rowTop.Add(y);
				y += _rows[i].Height;
			}
			_contentH = y;
		}

		/// <summary>贪心换行：逐字累加，超出可用宽度即断；优先在空格/常见标点处断开。</summary>
		private static List<string> Wrap(string text, DynamicSpriteFont font, float maxW)
		{
			var result = new List<string>();
			if (string.IsNullOrEmpty(text))
			{
				result.Add("");
				return result;
			}

			var cur = new StringBuilder();
			float w = 0f;
			int breakAt = -1; // 可断点（切到该下标为止，不含）
			foreach (char c in text)
			{
				float cw = font.MeasureString(c.ToString()).X;
				if (cur.Length > 0 && w + cw > maxW)
				{
					int cut = breakAt > 0 && breakAt < cur.Length ? breakAt : cur.Length;
					result.Add(cur.ToString(0, cut).Trim());
					cur.Remove(0, cut);
					w = font.MeasureString(cur.ToString()).X;
					breakAt = -1;
				}
				cur.Append(c);
				w += cw;
				if (IsBreakAfter(c))
					breakAt = cur.Length;
			}
			if (cur.Length > 0)
				result.Add(cur.ToString().Trim());
			return result;
		}

		private static bool IsBreakAfter(char c) =>
			c is ' ' or '\t' or '\u3000' or '，' or '。' or '、' or '；' or '：' or '！' or '？'
			  or ',' or '.' or ';' or ':' or '!' or '?';

		private float ClampScroll(float v)
		{
			float max = Math.Max(0f, _contentH - Height);
			return v < 0f ? 0f : (v > max ? max : v);
		}

		/// <summary>当前行所占的显示行区间（首行下标 + 行数）。</summary>
		private (int Start, int Count) CurrentBlock()
		{
			if (_curLine < 0)
				return (-1, 0);
			int start = -1, count = 0;
			for (int i = 0; i < _rows.Count; i++)
			{
				if (_rows[i].Line == _curLine)
				{
					if (start < 0)
						start = i;
					count++;
				}
				else if (start >= 0)
				{
					break;
				}
			}
			return (start, count);
		}

		/// <summary>让当前行块（含其翻译行）整体居中的滚动目标。</summary>
		private float FollowTarget()
		{
			var (start, count) = CurrentBlock();
			if (start < 0)
				return 0f;
			int last = start + count - 1;
			float blockCenter = (_rowTop[start] + _rowTop[last] + _rows[last].Height) / 2f;
			return ClampScroll(blockCenter - Height / 2f);
		}

		// ------------------------------------------------------------------ 绘制

		public override void Draw(SpriteBatch spriteBatch)
		{
			if (!Visible)
				return;

			Vector2 pos = DrawPosition - Origin;
			Utils.DrawInvBG(spriteBatch, pos.X, pos.Y, Width, Height, new Color(14, 10, 40, 255) * (0.92f * Opacity));

			// 裁剪绘制：超出视图的歌词绝不画到窗口外
			spriteBatch.End();
			spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.AlphaBlend, null, null, _raster, null, Main.UIScaleMatrix);
			Rectangle cut = ModUtils.GetClippingRectangle(spriteBatch,
				new Rectangle((int)pos.X, (int)pos.Y, (int)Width, (int)Height));
			Rectangle prev = spriteBatch.GraphicsDevice.ScissorRectangle;
			spriteBatch.GraphicsDevice.ScissorRectangle = cut;

			DrawContent(spriteBatch, pos);

			spriteBatch.GraphicsDevice.ScissorRectangle = prev;
			spriteBatch.End();
			spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, null, null, null, null, Main.UIScaleMatrix);

			base.Draw(spriteBatch);
		}

		private void DrawContent(SpriteBatch spriteBatch, Vector2 pos)
		{
			var font = FontAssets.MouseText.Value;

			if (_rows.Count == 0)
			{
				if (_emptyText.Length > 0)
				{
					const float s = 0.8f;
					Vector2 size = font.MeasureString(_emptyText) * s;
					spriteBatch.DrawString(font, _emptyText,
						new Vector2(pos.X + (Width - size.X) / 2f, pos.Y + (Height - size.Y) / 2f),
						new Color(190, 195, 225) * Opacity, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
				}
				return;
			}

			for (int i = 0; i < _rows.Count; i++)
			{
				Row r = _rows[i];
				float y = pos.Y + _rowTop[i] - _scroll;
				if (y + r.Height < pos.Y || y > pos.Y + Height)
					continue; // 不可见行不画

				bool isCur = _curLine >= 0 && r.Line == _curLine;
				int dist = _curLine < 0 ? 0 : Math.Abs(r.Line - _curLine);
				float alpha = MathHelper.Clamp(1f - dist * 0.18f, 0.25f, 1f);

				float scale;
				Color color;
				if (r.IsTrans)
				{
					scale = BaseScale * TransScale;
					color = isCur ? TransCurrentColor : TransColor * (alpha * 0.95f);
				}
				else
				{
					scale = BaseScale * (isCur ? CurBoost : 1f);
					color = isCur ? CurrentColor : NormalColor * (alpha * 0.92f);
				}

				Vector2 size = font.MeasureString(r.Text) * scale;
				float x = pos.X + (Width - size.X) / 2f;
				float ty = y + (r.Height - size.Y) / 2f;
				Utils.DrawBorderStringFourWay(spriteBatch, font, r.Text, x, ty, color * Opacity,
					Color.Black * (0.55f * alpha * Opacity), Vector2.Zero, scale);
			}
		}
	}
}
