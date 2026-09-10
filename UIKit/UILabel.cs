// ---------------------------------------------------------------------
// Ported from HEROsMod (https://github.com/JavidPack/HEROsMod).
// Copyright (C) HEROsMod contributors (JavidPack and others).
// Redistributed and modified by AllMusicMod under the GNU General Public License v3.0.
// Modified: 2026-09-04 (namespace changed to AllMusicMod.UIKit, bugfixes, trimmed dependencies).
// ---------------------------------------------------------------------
#nullable disable

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Graphics;
using Terraria;
using Terraria.GameContent;

#nullable disable

namespace AllMusicMod.UIKit
{
	internal class UILabel : UIView
	{
		/// <summary>可切换的默认字体。HEROsMod 原版为 DeathText（无 CJK），
		/// AllMusicMod 在面板初始化时切到 MouseText 以正确渲染中文歌名。</summary>
		public static DynamicSpriteFont DefaultFont { get; set; } = null;

		public static DynamicSpriteFont defaultFont
		{
			get { return DefaultFont ?? FontAssets.DeathText.Value; }
		}
		public DynamicSpriteFont font;
		private string text = "";

		public string Text
		{
			get { return text; }
			set {
				text = value;
				SetWidthHeight();
			}
		}

		private float width = 0;
		private float height = 0;

		public UILabel(string text)
		{
			font = defaultFont;
			this.Text = text;
		}

		public UILabel()
		{
			font = defaultFont;
			this.Text = "";
		}

		private void SetWidthHeight()
		{
			if (Text != null)
			{
				Vector2 size = font.MeasureString(Text);
				width = size.X;
				height = size.Y;
			}
			else
			{
				width = 0;
				height = 0;
			}
		}

		protected override float GetWidth()
		{
			return width * Scale;
		}

		protected override float GetHeight()
		{
			if (height == 0)
			{
				return font.MeasureString("H").Y * Scale;
			}
			else return height * Scale;
		}

		public override void Draw(SpriteBatch spriteBatch)
		{
			if (Text != null)
			{
				Utils.DrawBorderStringFourWay(spriteBatch, font, Text, DrawPosition.X, DrawPosition.Y, ForegroundColor, Color.Black * Opacity, Origin / Scale, Scale);
			}
			base.Draw(spriteBatch);
		}
	}
}


