// ---------------------------------------------------------------------
// Ported from HEROsMod (https://github.com/JavidPack/HEROsMod) - UIKit core.
// Copyright (C) HEROsMod contributors (JavidPack and others).
// Redistributed and modified by AllMusicMod under the GNU General Public License v3.0.
// Modified: 2026-09-04 (namespace AllMusicMod.UIKit).
// ---------------------------------------------------------------------
#nullable disable

using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AllMusicMod.UIKit
{
	internal class MasterView : UIView
	{
		private static MouseState mouseState = Mouse.GetState();
		private static MouseState previousMouseState = Mouse.GetState();
		private static GameScreen _gameScreen = null;

		public static GameScreen gameScreen
		{
			get {
				if (_gameScreen == null)
				{
					_gameScreen = new GameScreen();
					AddChildToMaster(_gameScreen);
				}
				return _gameScreen;
			}
		}

		protected override float GetWidth()
		{
			return Main.screenWidth;
		}

		protected override float GetHeight()
		{
			return Main.screenHeight;
		}

		private static MasterView masterView = new MasterView();

		public static void UpdateMaster()
		{
			ModUtils.CaptureInputState(); // 同步 XNA 鼠标状态（UISlider 释放判定依赖）
			mouseState = Mouse.GetState();
			UIView.MouseLeftButton = mouseState.LeftButton == ButtonState.Pressed;
			UIView.MousePrevLeftButton = previousMouseState.LeftButton == ButtonState.Pressed;
			UIView.MouseRightButton = mouseState.RightButton == ButtonState.Pressed;
			UIView.MousePrevRightButton = previousMouseState.RightButton == ButtonState.Pressed;
			previousMouseState = mouseState;
			HoverText = "";
			masterView.Update();
		}

		public static void DrawMaster(SpriteBatch spriteBatch)
		{
			spriteBatch.End();
			spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, SamplerState.AnisotropicClamp, DepthStencilState.None, null, null, Main.UIScaleMatrix);
			masterView.Draw(spriteBatch);
			spriteBatch.End();
			spriteBatch.Begin(SpriteSortMode.Immediate, BlendState.NonPremultiplied, SamplerState.AnisotropicClamp, DepthStencilState.None, null, null, Main.UIScaleMatrix);
		}

		public static void AddChildToMaster(UIView view)
		{
			masterView.AddChild(view);
		}

		public class GameScreen : UIView
		{
			public GameScreen()
			{
				this.OverridesMouse = false;
			}

			public override void Update()
			{
				if (!Main.gameMenu && !Main.mapFullscreen)
					this.Visible = true;
				else this.Visible = false;
				base.Update();
			}

			protected override float GetWidth()
			{
				return this.Parent.Width;
			}

			protected override float GetHeight()
			{
				return this.Parent.Height;
			}
		}
	}
}
