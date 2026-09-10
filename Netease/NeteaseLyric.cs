using System.Globalization;
using System.Text.Json.Nodes;

namespace AllMusicMod.Netease;

/// <summary>一行歌词（时间戳 + 原文 + 对应翻译；翻译为空串表示该句没有译文）。</summary>
public sealed record LyricLine(int TimeMs, string Text, string Translation);

/// <summary>一首歌的歌词文档：时间轴 + 按播放位置定位当前行。</summary>
public sealed class LyricDoc
{
	public IReadOnlyList<LyricLine> Lines { get; }

	public LyricDoc(IReadOnlyList<LyricLine> lines) => Lines = lines;

	public bool IsEmpty => Lines.Count == 0;

	/// <summary>按播放位置(ms)定位当前行下标；还没唱到第一句时返回 -1。</summary>
	public int IndexAt(long posMs)
	{
		int lo = 0, hi = Lines.Count - 1, best = -1;
		while (lo <= hi)
		{
			int mid = (lo + hi) >> 1;
			if (Lines[mid].TimeMs <= posMs)
			{
				best = mid;
				lo = mid + 1;
			}
			else
			{
				hi = mid - 1;
			}
		}
		return best;
	}
}

/// <summary>
/// 网易云歌词取词与解析（官方 /api/song/lyric，匿名即可取，带登录 Cookie 覆盖更全）。
/// 解析标准 LRC：支持 [mm:ss]、[mm:ss.xx]、[mm:ss.xxx]、一行多时间戳；
/// 非时间标签行（[ti:]/[by:] 等）与空文本行丢弃。
/// </summary>
public static class NeteaseLyric
{
	private static readonly Dictionary<long, LyricDoc> Cache = new();
	private static readonly object Gate = new();

	/// <summary>取歌词（按歌曲 id 内存缓存）。网络失败或没有歌词时返回空文档。</summary>
	public static async Task<LyricDoc> FetchAsync(long netId)
	{
		if (netId <= 0)
			return Empty();

		lock (Gate)
		{
			if (Cache.TryGetValue(netId, out var hit))
				return hit;
		}

		string? json = await NeteaseApi.GetLyricJsonAsync(netId).ConfigureAwait(false);
		LyricDoc doc = Parse(json);
		lock (Gate)
			Cache[netId] = doc; // 失败结果一并缓存，避免同一首反复请求
		return doc;
	}

	/// <summary>解析歌词 JSON（lrc.lyric 原文 + tlyric.lyric 译文）。json 为空/异常时返回空文档。</summary>
	public static LyricDoc Parse(string? json)
	{
		var lines = new List<LyricLine>();
		try
		{
			if (!string.IsNullOrWhiteSpace(json))
			{
				var root = JsonNode.Parse(json);
				string lrc = root?["lrc"]?["lyric"]?.GetValue<string>() ?? "";
				Dictionary<int, string> trans = ParseTranslations(root?["tlyric"]?["lyric"]?.GetValue<string>() ?? "");
				foreach (var (ms, text) in ParseLrc(lrc))
					lines.Add(new LyricLine(ms, text, trans.TryGetValue(ms, out string? t) ? t : ""));
			}
		}
		catch { /* 解析失败按“无歌词”处理 */ }

		lines.Sort(static (a, b) => a.TimeMs.CompareTo(b.TimeMs));
		return new LyricDoc(lines);
	}

	/// <summary>解析翻译歌词为「时间戳 → 译文」表（按时间戳对齐原文；空译文忽略）。</summary>
	private static Dictionary<int, string> ParseTranslations(string tlyric)
	{
		var map = new Dictionary<int, string>();
		if (string.IsNullOrWhiteSpace(tlyric))
			return map;
		foreach (var (ms, text) in ParseLrc(tlyric))
		{
			if (!map.ContainsKey(ms))
				map[ms] = text;
		}
		return map;
	}

	private static LyricDoc Empty() => new(Array.Empty<LyricLine>());

	/// <summary>解析 LRC 文本为 (时间ms, 文本) 序列。</summary>
	private static IEnumerable<(int Ms, string Text)> ParseLrc(string lrc)
	{
		foreach (string raw in lrc.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
		{
			var times = new List<int>();
			int i = 0;
			while (i < raw.Length && raw[i] == '[')
			{
				int close = raw.IndexOf(']', i + 1);
				if (close < 0)
					break;
				if (!TryParseTime(raw.Substring(i + 1, close - i - 1), out int ms))
					break; // [ti:] / [by:] 之类元信息标签，不是时间轴
				times.Add(ms);
				i = close + 1;
			}
			if (times.Count == 0)
				continue;

			string text = raw[i..].Trim();
			if (text.Length == 0)
				continue; // 空文本行在播放器里是“间隙”，不占显示行

			foreach (int t in times)
				yield return (t, text);
		}
	}

	/// <summary>解析时间标签 mm:ss / mm:ss.xx / mm:ss.xxx（小数位不足 3 位按百分秒补齐）。</summary>
	private static bool TryParseTime(string tag, out int ms)
	{
		ms = 0;
		int colon = tag.IndexOf(':');
		if (colon <= 0 || colon == tag.Length - 1)
			return false;
		if (!int.TryParse(tag.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out int min))
			return false;

		string secPart = tag[(colon + 1)..];
		int dot = secPart.IndexOf('.');
		string secStr = dot < 0 ? secPart : secPart[..dot];
		string fracStr = dot < 0 ? "" : secPart[(dot + 1)..];
		if (!int.TryParse(secStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec))
			return false;

		int frac = 0;
		if (fracStr.Length > 0)
		{
			string f = fracStr.Length > 3 ? fracStr[..3] : fracStr.PadRight(3, '0');
			int.TryParse(f, NumberStyles.Integer, CultureInfo.InvariantCulture, out frac);
		}

		ms = (min * 60 + sec) * 1000 + frac;
		return true;
	}
}
