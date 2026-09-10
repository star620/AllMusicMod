// ---------------------------------------------------------------------
// Ported from HEROsMod (https://github.com/JavidPack/HEROsMod).
// Copyright (C) HEROsMod contributors (JavidPack and others).
// Redistributed and modified by AllMusicMod under the GNU General Public License v3.0.
// Modified: 2026-09-04 (namespace changed to AllMusicMod.UIKit, bugfixes, trimmed dependencies).
// ---------------------------------------------------------------------
#nullable disable

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using System;

#nullable disable

namespace AllMusicMod.UIKit
{
	internal class UIButton : UIView
	{
		public static Asset<Texture2D> buttonBackground;
		private static Texture2D buttonFill;

		public static Texture2D ButtonFill
		{
			get {
				if (buttonFill == null)
				{
					Color[] edgeColors = new Color[buttonBackground.Value.Width * buttonBackground.Value.Height];
					buttonBackground.Value.GetData(edgeColors);
					Color[] fillColors = new Color[buttonBackground.Value.Height];
					for (int y = 0; y < fillColors.Length; y++)
					{
						fillColors[y] = edgeColors[buttonBackground.Value.Width - 1 + y * buttonBackground.Value.Width];
					}
					buttonFill = new Texture2D(UIView.graphics, 1, fillColors.Length);
					buttonFill.SetData(fillColors);
				}
				return buttonFill;
			}
		}

		private Color hoverColor = new Color(38, 42, 120);
		private Color drawColor;

		private UILabel label = new UILabel("");

		public string Text
		{
			get { return label.Text; }
			set {
				label.Text = value;
				label.Anchor = AnchorPosition.Center;
				ScaleText();
				CenterLabel();
			}
		}

		/// <summary>替换按钮文字字体并重新测量/居中（支持中文需传入 MouseText 等 CJK 字体）。</summary>
		public void SetFont(ReLogic.Graphics.DynamicSpriteFont font)
		{
			label.font = font;
			ScaleText();
			CenterLabel();
		}

		/// <summary>固定宽度下文本的最大高度占比（相对按钮高，默认 0.62，居中更协调）。</summary>
		public float TextHeightRatio { get; set; } = 0.62f;

		/// <summary>文本向下微调像素（MouseText 字形度量偏上时加大以视觉居中；负数向上）。</summary>
		public float TextVerticalOffset { get; set; } = 2f;

		private void CenterLabel()
		{
			label.CenterToParent();
			label.Position = new Vector2(label.Position.X, label.Position.Y + TextVerticalOffset);
		}

		public bool AutoSize { get; set; }

		public UIButton(string text)
		{
			AutoSize = true;
			this.AddChild(label);
			this.Text = text;
			this.BackgroundColor = new Color(28, 32, 119);
			drawColor = BackgroundColor;
			this.onMouseEnter += new EventHandler(UIButton_onMouseEnter);
			this.onMouseLeave += new EventHandler(UIButton_onMouseLeave);
		}

		public UIButton(string text, Color backgroundColor, Color hoverColor)
		{
			AutoSize = true;
			this.AddChild(label);
			this.Text = text;
			this.BackgroundColor = backgroundColor;
			drawColor = BackgroundColor;
			this.hoverColor = hoverColor;
			this.onMouseEnter += new EventHandler(UIButton_onMouseEnter);
			this.onMouseLeave += new EventHandler(UIButton_onMouseLeave);
		}
		public void SetBackgroundColor(Color color)
		{
			BackgroundColor = color;
			drawColor = BackgroundColor;
		}

		public void SetTextColor(Color color)
		{
			label.ForegroundColor = color;
		}

		private void UIButton_onMouseLeave(object sender, EventArgs e)
		{
			drawColor = BackgroundColor;
		}

		private void UIButton_onMouseEnter(object sender, EventArgs e)
		{
			drawColor = hoverColor;
		}

		private float width = 0;

		protected override float GetWidth()
		{
			if (AutoSize)
			{
				return label.Width + buttonBackground.Value.Width * 2 + 30;
			}
			else
			{
				return width;
			}
		}

		protected override void SetWidth(float width)
		{
			this.width = width;

			ScaleText();
			CenterLabel();
		}

		protected override float GetHeight()
		{
			return buttonBackground.Value.Height;
		}

		private void ScaleText()
		{
			Vector2 size = label.font.MeasureString(label.Text);
			if (!AutoSize)
			{
				float maxTextW = width - (buttonBackground.Value.Width * 2 + 10);
				// 先按宽度收缩；若宽度足够，则按高度限幅（占按钮高 TextHeightRatio，
				// 避免 MouseText 被放大到与 34px 按钮等高导致字形顶部被视觉推高/不居中）。
				float s = 1f;
				if (size.X > maxTextW && size.X > 0f)
					s = maxTextW / size.X;
				float byH = TextHeightRatio * this.Height;
				if (size.Y > 0f && size.Y * s > byH)
					s = byH / size.Y;
				label.Scale = s;
			}
			else
			{
				// AutoSize：按高度限幅放大，宽度保持文本自然宽
				float byH = TextHeightRatio * this.Height;
				if (size.Y > 0f)
					label.Scale = byH / size.Y;
			}
		}

		public override void Draw(SpriteBatch spriteBatch)
		{
			spriteBatch.Draw(buttonBackground.Value, DrawPosition, null, drawColor * Opacity, 0f, Origin, 1f, SpriteEffects.None, 0f);
			int fillWidth = (int)Width - 2 * buttonBackground.Value.Width;
			Vector2 pos = DrawPosition;
			pos.X += buttonBackground.Value.Width;
			spriteBatch.Draw(ButtonFill, pos - Origin, null, drawColor * Opacity, 0f, Vector2.Zero, new Vector2(fillWidth, 1f), SpriteEffects.None, 0f);
			pos.X += fillWidth;
			spriteBatch.Draw(buttonBackground.Value, pos, null, drawColor * Opacity, 0f, Origin, 1f, SpriteEffects.FlipHorizontally, 0f);
			base.Draw(spriteBatch);
		}
	}
}


