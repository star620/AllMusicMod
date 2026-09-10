using System.Security.Cryptography;
using System.Text;

namespace AllMusicMod.Netease;

/// <summary>
/// 网易云 eapi 请求参数加密（AES-128-ECB + MD5 digest）。
/// 明文 /api 端点做扫码登录最多只走到中间态 8821，无法换取真实登录 Cookie；
/// 官方客户端（PC/移动）都走 /eapi 加密端点，一次到位返回 803 + Set-Cookie。
/// 算法取自 yoyostyle / NeteaseCloudMusicApi 的 eapi 实现。
/// </summary>
internal static class NeteaseEapi
{
	private const string AesKey = "e82ckenh8dichen8"; // 16 字节 eapi 专属密钥

	/// <summary>把待加密对象包成 eapi 表单参数形式：{"params": hex_lower}。</summary>
	public static Dictionary<string, string> Encrypt(string url, object data)
	{
		string json = System.Text.Json.JsonSerializer.Serialize(data);
		string apiPath = new Uri(url).AbsolutePath.Replace("/eapi/", "/api/", StringComparison.Ordinal);
		string digest = Md5Hex($"nobody{apiPath}use{json}md5forencrypt");
		string raw = $"{apiPath}-36cd479b6b5-{json}-36cd479b6b5-{digest}";

		using var aes = Aes.Create();
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.PKCS7;
		aes.Key = Encoding.UTF8.GetBytes(AesKey);
		using var enc = aes.CreateEncryptor();
		byte[] ct = enc.TransformFinalBlock(Encoding.UTF8.GetBytes(raw), 0, raw.Length);
		return new Dictionary<string, string> { ["params"] = Convert.ToHexString(ct).ToLowerInvariant() };
	}

	private static string Md5Hex(string s)
	{
		byte[] h = MD5.HashData(Encoding.UTF8.GetBytes(s));
		return Convert.ToHexString(h).ToLowerInvariant();
	}
}