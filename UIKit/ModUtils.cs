// ---------------------------------------------------------------------
// Ported from HEROsMod (https://github.com/JavidPack/HEROsMod).
// Copyright (C) HEROsMod contributors (JavidPack and others).
// Redistributed and modified by AllMusicMod under the GNU General Public License v3.0.
// Modified: 2026-09-04 (namespace changed to AllMusicMod.UIKit, bugfixes, trimmed dependencies).
// ---------------------------------------------------------------------
#nullable disable

using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Terraria;

namespace AllMusicMod.UIKit
{
	/// <summary>
	/// HEROsMod ModUtils 的移植子集：仅提供被 UIKit 基础控件使用的静态成员。
	/// 宿主（ModSystem）每帧应调用 <see cref="CaptureInputState"/> 以同步鼠标状态。
	/// </summary>
	internal static class ModUtils
	{
		private static Texture2D _dummyTexture;

		public static Texture2D DummyTexture
		{
			get {
				if (_dummyTexture == null || _dummyTexture.IsDisposed)
				{
					_dummyTexture = new Texture2D(Main.instance.GraphicsDevice, 1, 1);
					_dummyTexture.SetData(new Color[] { Color.White });
				}
				return _dummyTexture;
			}
		}

		public static MouseState MouseState { get; private set; }

		/// <summary>每帧由宿主调用一次，同步 XNA 鼠标状态（UIKit 的 UISlider 等依赖它判断释放）。</summary>
		public static void CaptureInputState()
		{
			MouseState = Microsoft.Xna.Framework.Input.Mouse.GetState();
		}

		/// <summary>
		/// 把以 UI 坐标给出的裁剪矩形转换到实际屏幕坐标并夹紧到视口内，
		/// 用于 SpriteBatch ScissorRectangle 的绘制裁剪（滚动区域遮罩）。
		/// </summary>
		public static Rectangle GetClippingRectangle(SpriteBatch spriteBatch, Rectangle r)
		{
			Vector2 vector = new Vector2(r.X, r.Y);
			Vector2 position = new Vector2(r.Width, r.Height) + vector;
			vector = Vector2.Transform(vector, Main.UIScaleMatrix);
			position = Vector2.Transform(position, Main.UIScaleMatrix);
			Rectangle result = new Rectangle((int)vector.X, (int)vector.Y, (int)(position.X - vector.X), (int)(position.Y - vector.Y));
			int width = spriteBatch.GraphicsDevice.Viewport.Width;
			int height = spriteBatch.GraphicsDevice.Viewport.Height;
			result.X = Utils.Clamp<int>(result.X, 0, width);
			result.Y = Utils.Clamp<int>(result.Y, 0, height);
			result.Width = Utils.Clamp<int>(result.Width, 0, width - result.X);
			result.Height = Utils.Clamp<int>(result.Height, 0, height - result.Y);
			return result;
		}
	}
}

