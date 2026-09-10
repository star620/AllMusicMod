using System.Net;
using System.Text.Json.Nodes;
using Terraria.ModLoader;

namespace AllMusicMod.Netease;

/// <summary>
/// 网易云单机原型的数据链路（所有调用都在后台线程进行，播放管线留在主线程）：
///  1) 搜索走公共 binaryify 镜像 apis.netstart.cn/music/search —— 免登录返回 id/歌名/歌手/时长。
///  2) 播放直链优先走官方 /api/song/enhance/player/url（登录态时带 Cookie 头取 VIP/高音质 mp3 直链），
///     未登录时退回官方外链口 music.163.com/song/media/outer/url?id={id}.mp3。
///     注意：镜像 song/url 系接口不接收第三方 Cookie，登录态取流必须直连官方。
/// </summary>
public static class NeteaseApi
{
	public const string SearchBase = "https://apis.netstart.cn/music";
	public const string PlayUrlTemplate = "https://music.163.com/song/media/outer/url?id={0}.mp3";
	/// <summary>取流等级。standard 对普通账号和多数 VIP 都可播；等级越高越容易失败。</summary>
	private const string Level = "standard";

	private const string BrowserUA =
		"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";

	private static readonly HttpClient Http = BuildHttp();

	// ---- 扫码登录（官方 eapi 加密通道）----
	//     明文 /api 端点做 client/login 最多只到中间态 8821，换取不到真实登录 Cookie；
	//     官方客户端走 /eapi 加密端点，AES-ECB 加密 params 一次到位返回 803 + Set-Cookie。
	//     unikey 申请与扫码兑换都走 /eapi，账号与 unikey 的加密参数键需共享同一加密上下文。
	private const string OfficialUnikeyUrl = "https://music.163.com/eapi/login/qrcode/unikey";
	private const string OfficialCheckUrl = "https://music.163.com/eapi/login/qrcode/client/login";
	private const string QrImgApi = "https://api.qrserver.com/v1/create-qr-code/?size=200x200&data=";

	/// <summary>扫码登录专用会话：unikey 与 client/login 兑换必须共享 CookieContainer，扫码结果才能关联到本机。</summary>
	private static HttpClient _loginHttp = null!;
	private static CookieContainer _loginJar = new();

	static NeteaseApi()
	{
		ResetQrLogin();
	}

	private static void Log(string m)
	{
		try { ModContent.GetInstance<AllMusicMod>().Logger.Info("[Netease] " + m); } catch { }
	}

	/// <summary>开始一次新的扫码登录：重建登录会话（清空 CookieContainer）。</summary>
	public static void ResetQrLogin()
	{
		_loginJar = new CookieContainer();
		var handler = new SocketsHttpHandler
		{
			CookieContainer = _loginJar,
			AllowAutoRedirect = false,
			ConnectTimeout = TimeSpan.FromSeconds(10),
		};
		_loginHttp = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
		_loginHttp.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUA);
		_loginHttp.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/login");
	}

	private static HttpClient BuildHttp()
	{
		// 播放地址的重定向手动处理（https→http 更可控），不做自动跟随。
		var handler = new SocketsHttpHandler
		{
			AllowAutoRedirect = false,
			ConnectTimeout = TimeSpan.FromSeconds(10),
		};
		var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUA);
		client.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
		return client;
	}

	/// <summary>提取扫码成功后下发的登录 Cookie（Set-Cookie 头），取不到时回退到会话 CookieContainer 里的已登录 Cookie。</summary>
	private static string ExtractLoginCookie(HttpResponseMessage resp)
	{
		var keep = new List<string>();
		if (resp.Headers.TryGetValues("Set-Cookie", out var vals))
		{
			foreach (var raw in vals)
			{
				foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
				{
					string t = part.Trim();
					int eq = t.IndexOf('=');
					if (eq <= 0 || eq == t.Length - 1)
						continue;
					string name = t[..eq].Trim();
					string val = t[(eq + 1)..].Trim();
					if (name.Length > 0 && name.All(ch => char.IsLetterOrDigit(ch) || ch == '_')
						&& !val.Contains(',') && !val.Contains(' ') && !val.Contains(';'))
						keep.Add(t);
				}
			}
		}
		if (keep.Count > 0)
			return string.Join("; ", keep);

		try
		{
			var all = _loginJar.GetCookies(new Uri("https://music.163.com"));
			var names = new List<string>();
			foreach (Cookie c in all)
			{
				if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Value))
					names.Add($"{c.Name}={c.Value}");
			}
			return string.Join("; ", names);
		}
		catch { return ""; }
	}

	// ---------------------------------------------------------------- 搜索

	/// <summary>按关键词搜索单曲（type=1）。失败抛异常，由调用方转成界面提示。</summary>
	public static async Task<List<NeteaseSong>> SearchAsync(string keyword, int limit = 20)
	{
		if (string.IsNullOrWhiteSpace(keyword))
			return new List<NeteaseSong>();

		string url = $"{SearchBase}/search?keywords={Uri.EscapeDataString(keyword.Trim())}&type=1&limit={limit}";
		using var resp = await Http.GetAsync(url).ConfigureAwait(false);
		if (!resp.IsSuccessStatusCode)
			throw new HttpRequestException($"搜索服务返回 {(int)resp.StatusCode}");

		string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(json);
		}
		catch
		{
			throw new InvalidDataException("搜索返回内容不是合法 JSON");
		}

		int code = node?["code"]?.GetValue<int>() ?? 200;
		if (code != 200)
			throw new InvalidDataException($"搜索服务 code={code}");

		var songs = new List<NeteaseSong>();
		if (node?["result"] is JsonObject res && res["songs"]?.AsArray() is { } arr)
		{
			foreach (var item in arr)
			{
				var song = NeteaseSong.FromJson(item);
				if (song != null)
					songs.Add(song);
			}
		}

		return songs;
	}

	// ------------------------------------------------------------ 登录态取流（VIP）

	/// <summary>官方账号信息接口（带 Cookie 校验登录态并取昵称）。</summary>
	private const string OfficialAccountUrl = "https://music.163.com/api/nuser/account/get";

	/// <summary>官方播放地址接口（带 Cookie 取 VIP/高音质直链，type=mp3）。</summary>
	private const string OfficialPlayerUrl = "https://music.163.com/api/song/enhance/player/url";

	/// <summary>取流等级 → 官方 br 参数（type=mp3）。standard=128000 保底成功率最高。</summary>
	private static int LevelToBr(string level) => level switch
	{
		"higher" => 192000,
		"exhigh" => 320000,
		"lossless" => 999000,
		_ => 128000, // standard
	};

	/// <summary>用已保存登录态向官方账号接口校验并取昵称（用于界面显示“当前账户”）。未登录/失败返回 (false,"")。</summary>
	public static async Task<(bool Ok, string Nick)> LoginStatusAsync()
	{
		if (!NeteaseSession.HasAuth)
			return (false, "");
		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, OfficialAccountUrl);
			req.Headers.TryAddWithoutValidation("Cookie", NeteaseSession.LoginCookie!);
			using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
				.ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
				return (false, "");
			var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
			int code = (int)(j?["code"]?.GetValue<long>() ?? -1);
			var acct = j?["account"] as JsonObject;
			bool anon = acct?["anonimousUser"]?.GetValue<bool>() ?? true;
			string? nick = j?["profile"]?["nickname"]?.GetValue<string>();
			return code == 200 && acct != null && !anon && !string.IsNullOrWhiteSpace(nick)
				? (true, nick!)
				: (false, nick ?? "");
		}
		catch
		{
			return (false, "");
		}
	}

	/// <summary>官方歌词接口（匿名可取；带登录 Cookie 覆盖更全，如解锁后的曲目）。</summary>
	private const string OfficialLyricUrl = "https://music.163.com/api/song/lyric";

	/// <summary>取歌词 JSON（含 lrc/tlyric）；失败返回 null，由调用方按“无歌词”处理。</summary>
	public static async Task<string?> GetLyricJsonAsync(long id)
	{
		try
		{
			using var req = new HttpRequestMessage(HttpMethod.Get, $"{OfficialLyricUrl}?id={id}&lv=1&kv=1&tv=-1");
			if (NeteaseSession.HasAuth)
				req.Headers.TryAddWithoutValidation("Cookie", NeteaseSession.LoginCookie!);
			using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
				.ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
				return null;
			return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
		}
		catch
		{
			return null;
		}
	}

	/// <summary>
	/// 已登录时用登录态 Cookie 直连官方接口换真实可播地址（type=mp3，等级见 Level）。
	/// 官方 legacy 接口只认“Cookie 头 + br 参数”，不认 ?cookie= 查询参数（镜像接口亦不回传第三方登录态），
	/// 因此这里必须显式加 Cookie 请求头。未登录返回 null。
	/// </summary>
	public static async Task<string?> GetLoggedInPlayUrlAsync(long id)
	{
		if (!NeteaseSession.HasAuth)
			return null;
		try
		{
			string url = $"{OfficialPlayerUrl}?ids=[{id}]&br={LevelToBr(Level)}&encodeType=mp3";
			using var req = new HttpRequestMessage(HttpMethod.Get, url);
			req.Headers.TryAddWithoutValidation("Cookie", NeteaseSession.LoginCookie!);
			using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
				.ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode)
				return null;
			string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
			var arr = (JsonNode.Parse(json)?["data"] as JsonArray);
			if (arr == null || arr.Count == 0)
				return null;
			var first = arr[0];
			int code = (int)(first?["code"]?.GetValue<long>() ?? -1);
			string? murl = first?["url"]?.GetValue<string>();
			return code == 200 && !string.IsNullOrWhiteSpace(murl) ? murl! : null;
		}
		catch
		{
			return null;
		}
	}

		// ------------------------------------------------------------ 二维码登录

		public sealed record QrKey(string Unikey, byte[]? Png, string? Err);

		/// <summary>官方申请二维码 key + 二维码 PNG（直连 music.163.com，明文表单）。失败返回 Err。</summary>
		public static async Task<QrKey> LoginQrKeyAsync()
		{
			try
				{
					var enc = NeteaseEapi.Encrypt(OfficialUnikeyUrl, new { type = 1 });
					using var r1 = await _loginHttp.PostAsync(OfficialUnikeyUrl,
						new FormUrlEncodedContent(enc)).ConfigureAwait(false);
				if (!r1.IsSuccessStatusCode)
					return new QrKey("", null, $"取 key 接口 HTTP {(int)r1.StatusCode}");
				var j1 = JsonNode.Parse(await r1.Content.ReadAsStringAsync().ConfigureAwait(false)) as JsonObject;
				string unikey = j1?["unikey"]?.GetValue<string>() ?? "";
				if (string.IsNullOrWhiteSpace(unikey))
					return new QrKey("", null, "接口未返回二维码 key");

				// 官方「扫码即兑换」：二维码内容就是登录链接，用第三方生成器渲染 PNG，绕开易挂的镜像兑换。
				string qrUrl = QrImgApi + Uri.EscapeDataString("https://music.163.com/login?codekey=" + unikey);
				byte[]? png = null;
				try
				{
					using var r2 = await Http.GetAsync(qrUrl).ConfigureAwait(false);
					if (r2.IsSuccessStatusCode)
						png = await r2.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
				}
				catch { png = null; }
				return new QrKey(unikey, png, "");
			}
			catch (Exception ex)
			{
				return new QrKey("", null, "申请二维码异常：" + ex.Message);
			}
		}

		public sealed record QrCheckResult(int Code, string? Cookie, string? Nickname, string Message);

		/// <summary>官方轮询扫码状态。803=授权成功（返回 Cookie 供保存），800=过期/不存在，802=已扫码待确认，801=等待扫码。</summary>
		public static async Task<QrCheckResult> LoginQrCheckAsync(string unikey)
			{
				try
					{
						// client/login 走官方 eapi 加密通道：AES-ECB 加密 key+type 后一次到位返回 803 + 登录 Cookie。
						// 明文 /api 端点最多只到中间态 8821，换取不到真实令牌。
						var enc = NeteaseEapi.Encrypt(OfficialCheckUrl, new { type = 1, key = unikey });
						using var r = await _loginHttp.PostAsync(OfficialCheckUrl, new FormUrlEncodedContent(enc)).ConfigureAwait(false);
					if (!r.IsSuccessStatusCode)
						return new QrCheckResult(-1, null, null, $"接口瞬时异常(HTTP {(int)r.StatusCode})，自动重试中…");

					var j = JsonNode.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false)) as JsonObject;
					int code = (int)(j?["code"]?.GetValue<long>() ?? -1);
					// 兑换成功时登录 Cookie 主要经 Set-Cookie 头下发；个别响应也会在 body 带 "cookie" 字段，取到则优先用于保存。
					string? cookie = j?["cookie"]?.GetValue<string>()
						?? ExtractLoginCookie(r);
					string? nick = null;
					if (code == 803)
					{
						// 官方 803 一般不附 profile，昵称由调用方随后用 /login/status 权威取回。
						nick = j?["profile"]?["nickname"]?.GetValue<string>();
					}
					string msg = code switch
					{
						800 => "二维码已过期或不存在，正在自动刷新…",
						801 => "等待扫码…(请用网易云 App 扫码)",
						802 => "已扫码，请在手机上确认",
						803 => "登录成功",
						8821 => "风控：需要行为验证码，请改用 Cookie 导入",
						_ => $"未知状态({code})",
					};
					Log($"官方扫码轮询 unikey={unikey} code={code} msg={msg} 有Cookie={!string.IsNullOrEmpty(cookie)}");
					return new QrCheckResult(code, cookie, nick, msg);
				}
				catch (Exception ex)
				{
					Log($"扫码轮询异常：{ex.Message}");
					return new QrCheckResult(-1, null, null, "登录检查异常：" + ex.Message);
				}
			}

		// ------------------------------------------------------------ 下载整曲

	public enum DownloadStatus
	{
		/// <summary>已下载完整 MP3。</summary>
		Ok,
		/// <summary>服务可用但该曲拿不到流（无版权 / VIP / 404），不是网络错误。</summary>
		Unavailable,
		/// <summary>网络 / IO 错误。</summary>
		Error,
	}

	public sealed record DownloadOutcome(DownloadStatus Status, long Bytes, string Detail);

	/// <summary>
	/// 经官方重定向口下载整首 MP3 到 dest（dest 所在目录须已存在）。
	/// 有登录态时优先走官方 player/url（能放 VIP），失败再退回匿名 outer/url。
	/// 实测：开放版权曲 outer 302→CDN(m7xx.music.126.net) 且 Content-Type=audio/mpeg，可直接落地为 .mp3 交给 MediaPlayer。
	/// 失败时 Detail 尽量给出可定位原因（HTTP 状态 / 响应类型 / 长度 / 响应头片段），便于区分“风控”与“真无版权”。
	/// </summary>
	public static async Task<DownloadOutcome> DownloadSongAsync(long id, string dest)
	{
		bool useV1 = NeteaseSession.HasAuth;
		for (int attempt = 1; attempt <= 2; attempt++)
		{
			var o = useV1
				? await TryV1Async(id, dest).ConfigureAwait(false)
				: await TryOuterAsync(id, dest).ConfigureAwait(false);

			if (o.Status == DownloadStatus.Ok)
				return o;
			CleanupPartial(dest);

			if (attempt == 1)
			{
				await Task.Delay(250).ConfigureAwait(false);
				useV1 = false; // 登录口失败 → 再试匿名口（可能只是该等级取不到）
				continue;
			}
			return o with { Detail = o.Detail + $"（尝试{attempt}次）" };
		}
		return new DownloadOutcome(DownloadStatus.Error, 0, "unknown");
	}

	/// <summary>登录态经官方 player/url 取可播地址并整曲落地。</summary>
	private static async Task<DownloadOutcome> TryV1Async(long id, string dest)
	{
		string? url = await GetLoggedInPlayUrlAsync(id).ConfigureAwait(false);
		if (string.IsNullOrEmpty(url))
			return new DownloadOutcome(DownloadStatus.Unavailable, 0, "登录态取流未返回可播地址（无版权或需更高会员等级）");
		return await DownloadFromMp3UrlAsync(url, dest).ConfigureAwait(false);
	}

	/// <summary>直接按给定 mp3/CDN 直链落地整曲（不依赖本机登录态；供“服务器集中取流广播”时客户端使用）。</summary>
	public static Task<DownloadOutcome> DownloadFromUrlAsync(string url, string dest)
		=> DownloadFromMp3UrlAsync(url, dest);

	/// <summary>从任意 mp3 CDN 落地整曲（逐跳跟随，最多 6 跳），带音频类型与最小长度校验。</summary>
	private static async Task<DownloadOutcome> DownloadFromMp3UrlAsync(string startUrl, string dest)
	{
		string current = startUrl;
		for (int hop = 0; hop < 6; hop++)
		{
			try
			{
				using var r = await Http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead)
					.ConfigureAwait(false);
				if ((int)r.StatusCode >= 300 && (int)r.StatusCode < 400)
				{
					var loc = r.Headers.Location?.ToString();
					if (string.IsNullOrEmpty(loc))
						return new DownloadOutcome(DownloadStatus.Unavailable, 0, $"{hop + 1} 跳后无跳转地址");
					current = loc;
					continue;
				}
				if (!r.IsSuccessStatusCode)
					return new DownloadOutcome(DownloadStatus.Unavailable, 0, $"HTTP {(int)r.StatusCode}");
				if (!IsAudioContent(r))
				{
					string media = r.Content.Headers.ContentType?.MediaType ?? "无";
					long? len = r.Content.Headers.ContentLength;
					string head = await ReadHeadAsync(r).ConfigureAwait(false);
					return new DownloadOutcome(DownloadStatus.Unavailable, 0,
						$"返回非音频(类型={media} 长度={len}) 内容开头: {head}");
				}

				return await SaveBodyAsync(r, dest).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				CleanupPartial(dest);
				return new DownloadOutcome(DownloadStatus.Error, 0, ex.Message);
			}
		}
		return new DownloadOutcome(DownloadStatus.Unavailable, 0, "跳转超过 6 次仍未拿到音频");
	}

	/// <summary>匿名官方外链口取流：定位首个跳转/CDN 地址后逐跳跟随落地。（未登录默认路径，开放版权曲可直接放。）</summary>
	private static async Task<DownloadOutcome> TryOuterAsync(long id, string dest)
	{
		string requestUrl = string.Format(PlayUrlTemplate, id);
		try
		{
			// 第 1 步：打官方外链口，定位首个跳转/CDN 地址（不自动跟随，拿到 location）。
			string? target = null;
			string step1Note = "";
			using (var r1 = await Http.GetAsync(requestUrl).ConfigureAwait(false))
			{
				if ((int)r1.StatusCode >= 300 && (int)r1.StatusCode < 400)
				{
					var loc = r1.Headers.Location?.ToString();
					if (string.IsNullOrEmpty(loc))
						step1Note = $"步骤1 HTTP {(int)r1.StatusCode} 但无跳转地址";
					else
						target = loc;
				}
				else if (r1.IsSuccessStatusCode && IsAudioContent(r1))
				{
					target = requestUrl; // 个别情形直接 200 音频
				}
				else
				{
					step1Note = $"步骤1 HTTP {(int)r1.StatusCode} {r1.Content.Headers.ContentType?.MediaType ?? "无类型"}";
				}
			}

			if (target == null && step1Note.Length > 0)
				return new DownloadOutcome(DownloadStatus.Unavailable, 0, step1Note);

			// 第 2 步：从 CDN 起逐跳跟随（302 可能 http→https 或再跳一次），最多 6 跳。
			string current = target ?? requestUrl;
			for (int hop = 0; hop < 6; hop++)
			{
				using var r2 = await Http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead)
					.ConfigureAwait(false);
				if ((int)r2.StatusCode >= 300 && (int)r2.StatusCode < 400)
				{
					var loc = r2.Headers.Location?.ToString();
					if (string.IsNullOrEmpty(loc))
						return new DownloadOutcome(DownloadStatus.Unavailable, 0,
							$"{step1Note}；第{hop + 2}步 HTTP {(int)r2.StatusCode} 无跳转地址");
					current = loc;
					continue;
				}
				if (!r2.IsSuccessStatusCode)
					return new DownloadOutcome(DownloadStatus.Unavailable, 0,
						$"{step1Note}；第{hop + 2}步 HTTP {(int)r2.StatusCode}");

				if (!IsAudioContent(r2))
				{
					string media = r2.Content.Headers.ContentType?.MediaType ?? "无";
					long? len = r2.Content.Headers.ContentLength;
					string head = await ReadHeadAsync(r2).ConfigureAwait(false);
					return new DownloadOutcome(DownloadStatus.Unavailable, 0,
						step1Note + $"；第{hop + 2}步返回非音频(类型={media} 长度={len}) 内容开头: {head}");
				}

				return await SaveBodyAsync(r2, dest).ConfigureAwait(false);
			}
			return new DownloadOutcome(DownloadStatus.Unavailable, 0, "跳转超过 6 次仍未拿到音频");
		}
		catch (Exception ex)
		{
			CleanupPartial(dest);
			return new DownloadOutcome(DownloadStatus.Error, 0, ex.Message);
		}
	}

	/// <summary>
	/// 把响应体落到 dest：先写 dest.part，整曲下完再原子改名。
	/// 避免“下载被中断/超时/进程退出”留下半截文件被后续当成完整缓存播放（表现为歌播一半就跳下一首）。
	/// </summary>
	private static async Task<DownloadOutcome> SaveBodyAsync(HttpResponseMessage resp, string dest)
	{
		string part = dest + ".part";
		try
		{
			await using (var fs = File.Create(part))
			{
				// 响应体读取本身没有超时：CDN 挂死会让整首永远停在“下载中”，这里给落地加硬超时
				// （放宽到 120s 以便慢速线路也能整曲下完，避免被误截断）
				using var copyCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
				await resp.Content.CopyToAsync(fs, copyCts.Token).ConfigureAwait(false);
			}
			long bytes = new FileInfo(part).Length;
			if (bytes < 20000)
			{
				CleanupPartial(dest);
				return new DownloadOutcome(DownloadStatus.Unavailable, 0, "下载内容过短，疑似不可播（无版权/需VIP）");
			}
			File.Move(part, dest, true); // 完整落盘后才对外可见
			return new DownloadOutcome(DownloadStatus.Ok, bytes, "OK");
		}
		catch (Exception ex)
		{
			CleanupPartial(dest);
			return new DownloadOutcome(DownloadStatus.Error, 0, ex.Message);
		}
	}

	/// <summary>清掉目标文件与可能残留的半成品分片。</summary>
	private static void CleanupPartial(string dest)
	{
		try { if (File.Exists(dest)) File.Delete(dest); } catch { /* 忽略 */ }
		try { if (File.Exists(dest + ".part")) File.Delete(dest + ".part"); } catch { /* 忽略 */ }
	}

	private static async Task<string> ReadHeadAsync(HttpResponseMessage resp)
	{
		try
		{
			byte[] buf = new byte[96];
			await using var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
			int n = await s.ReadAsync(buf.AsMemory(0, buf.Length)).ConfigureAwait(false);
			string t = System.Text.Encoding.UTF8.GetString(buf, 0, n);
			return (t.Length <= 60 ? t : t[..60]).Replace('\r', ' ').Replace('\n', ' ');
		}
		catch { return ""; }
	}

	private static bool IsAudioContent(HttpResponseMessage resp)
	{
		string? media = resp.Content.Headers.ContentType?.MediaType;
		return media != null && media.StartsWith("audio", StringComparison.OrdinalIgnoreCase);
	}
}
