using System;
using System.IO;
using Terraria;

namespace AllMusicMod.Netease;

/// <summary>
/// 网易云登录会话（二维码登录成功后的 Cookie 态保存/加载，以及手动粘贴 Cookie 的兜底入口）。
/// 安全说明：网易云的 Cookie（尤其 MUSIC_U）本身就是登录态，等同于账号。
/// 这里只把它永久保存在“本地 mod 存档目录”下的明文文件里，绝不上报/写入日志；请勿外传该文件。
/// 文件路径：&lt;Terraria存档&gt;/NeteaseCache/netease_login.txt
///  - 扫描登录成功后自动覆盖写入；
///  - 二维码不好使时，也可手动把浏览器里登录后的 Cookie 字符串（含 MUSIC_U=...）整个粘贴写入该文件，
///    第一行生效，格式不合法时视为未登录，播放会回退匿名免费口。
/// </summary>
public static class NeteaseSession
{
	private const string FileName = "netease_login.txt";

	/// <summary>登录态 Cookie 字符串（如 "MUSIC_U=xxxx; __csrf=yyy"），null/空=未登录。</summary>
	public static string? LoginCookie { get; private set; }

	/// <summary>当前账号昵称（仅展示用；进入世界后异步经 /login/status 刷新）。</summary>
	public static string? Nickname { get; private set; }

	public static bool HasAuth => !string.IsNullOrWhiteSpace(LoginCookie);

	private static string FilePath()
	{
		var dir = Path.Combine(Main.SavePath, NeteaseMp3Player.CacheFolderName);
		Directory.CreateDirectory(dir);
		return Path.Combine(dir, FileName);
	}

	/// <summary>登录态文件路径（供 UI 提示“扫码不好使时手动粘贴 Cookie 到该文件”）。</summary>
	public static string CookieFilePath => FilePath();

	/// <summary>确保登录态文件存在（不存在则创建空文件），供“用记事本打开”一键粘贴 Cookie。</summary>
	public static void EnsureCookieFile()
	{
		try
		{
			if (!File.Exists(FilePath()))
				File.WriteAllText(FilePath(), "");
		}
		catch { /* 忽略：随后打开文件失败时 UI 会提示 */ }
	}

	/// <summary>进入世界时调用一次：加载已保存的登录态与上次昵称（扫描失败回退时也走这里读手动粘贴值）。</summary>
	public static void Load()
	{
		try
		{
			if (File.Exists(FilePath()))
			{
				string[] lines = File.ReadAllText(FilePath())
					.Replace("\r\n", "\n").Split('\n');
				string first = (lines.Length > 0 ? lines[0] : "").Trim();
				LoginCookie = string.IsNullOrWhiteSpace(first) ? null : first;
				Nickname = lines.Length > 1 ? lines[1].Trim() : null;
				if (string.IsNullOrWhiteSpace(Nickname))
					Nickname = null;
			}
			else
			{
				LoginCookie = null;
				Nickname = null;
			}
		}
		catch
		{
			LoginCookie = null;
			Nickname = null;
		}
	}

	/// <summary>二维码校验成功后保存登录态（供取 VIP 流）。返回是否保存成功。</summary>
	public static bool SaveCookie(string cookie)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(cookie))
				return false;
			LoginCookie = cookie.Trim();
			File.WriteAllText(FilePath(), LoginCookie + "\n" + (Nickname ?? ""));
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>更新昵称并持久化为文件第二行（cookie 不受影响）。</summary>
	public static void SaveNickname(string nick)
	{
		Nickname = string.IsNullOrWhiteSpace(nick) ? null : nick.Trim();
		try
		{
			string cookie = LoginCookie ?? "";
			File.WriteAllText(FilePath(), cookie + "\n" + (Nickname ?? ""));
		}
		catch { /* 忽略：昵称非关键 */ }
	}

	/// <summary>清除登录态（账号失效/重登时回退匿名免费流）。</summary>
	public static void Clear()
	{
		LoginCookie = null;
		Nickname = null;
		try { if (File.Exists(FilePath())) File.Delete(FilePath()); } catch { /* 忽略 */ }
	}
}