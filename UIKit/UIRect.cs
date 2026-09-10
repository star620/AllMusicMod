// ---------------------------------------------------------------------
// Ported from HEROsMod (https://github.com/JavidPack/HEROsMod).
// Copyright (C) HEROsMod contributors (JavidPack and others).
// Redistributed and modified by AllMusicMod under the GNU General Public License v3.0.
// Modified: 2026-09-04 (namespace changed to AllMusicMod.UIKit, bugfixes, trimmed dependencies).
// ---------------------------------------------------------------------
#nullable disable

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

#nullable disable

namespace AllMusicMod.UIKit
{
	internal class UIRect : UIView
	{
		public UIRect()
		{
			this.Width = 10;
			this.Height = 10;
		}

		public UIRect(Vector2 position, float width, float height)
		{
			this.Position = position;
			this.Width = width;
			this.Height = height;
		}

		public override void Draw(SpriteBatch spriteBatch)
		{
			Texture2D texture = ModUtils.DummyTexture;
			spriteBatch.Draw(texture, new Rectangle((int)(DrawPosition.X - Origin.X), (int)(DrawPosition.Y - Origin.Y), (int)Width, (int)Height), ForegroundColor);
			base.Draw(spriteBatch);
		}
	}
}


