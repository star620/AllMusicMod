// UIKit 皮肤加载器（AllMusicMod 自有代码，仅负责把打包进本 mod 的
// HEROsMod 皮肤位图挂到移植控件的静态字段上，行为参照 HEROsMod.Mod.Load()）。
using AllMusicMod.UIKit;
using Microsoft.Xna.Framework.Graphics;
using ReLogic.Content;
using Terraria;
using Terraria.ModLoader;

namespace AllMusicMod.UI
{
	/// <summary>
	/// 加载 HEROsMod UIKit 皮肤位图（buttonEdge/barEdge/scrollbgEdge/scrollbarEdge）。
	/// 这些 png 由 HEROsMod 项目原样携带，随 AllMusicMod 打包分发（GPLv3，见 UIKit 文件头）。
	/// </summary>
	internal static class UIKitSkin
	{
		public static void Load(Mod mod)
		{
			if (Main.dedServ)
				return;

			// 与 HEROsMod.Load 一致：必须在 !Main.dedServ 下立即加载
			UIButton.buttonBackground = Request(mod, "Images/UIKit/buttonEdge");
			UISlider.barTexture = Request(mod, "Images/UIKit/barEdge");
			UIScrollView.ScrollbgTexture = Request(mod, "Images/UIKit/scrollbgEdge");
			UIScrollBar.ScrollbarTexture = Request(mod, "Images/UIKit/scrollbarEdge");
		}

		public static void Unload()
		{
			UIButton.buttonBackground = null;
			UISlider.barTexture = null;
			UIScrollView.ScrollbgTexture = null;
			UIScrollBar.ScrollbarTexture = null;
		}

		private static Asset<Texture2D> Request(Mod mod, string path)
			=> mod.Assets.Request<Texture2D>(path, AssetRequestMode.ImmediateLoad);
	}
}
