using System.Net;
using AllMusicMod.Core;

namespace AllMusicMod.Netease;

/// <summary>聚合搜索结果行：Track 参与点歌/入队，其余仅供列表展示。</summary>
public sealed record SongHit(TrackRef Track, string Artists, string Note);

/// <summary>
/// 网易云在线源的搜索与取流分发（唯一在线源，已移除酷狗/QQ）。
///  - 搜索：见 NeteaseApi（binaryify 镜像）。
///  - 取流：见 NeteaseApi（匿名 outer/url；登录态直连官方 player/url 带 Cookie 取 VIP 流）。
/// 播放流先落地到本地缓存文件，再交给 MediaPlayer 播放。
/// </summary>
public static class MusicSources
{
	// -------------------------------------------------------------- 搜索

	/// <summary>按网易云搜索。UI 每次只查该源。</summary>
	public static Task<List<SongHit>> SearchAsync(MusicSource source, string keyword, int limit = 30)
	{
		if (string.IsNullOrWhiteSpace(keyword))
			return Task.FromResult(new List<SongHit>());
		return source switch
		{
			MusicSource.Netease => NeteaseSearchAsync(keyword, limit),
			_ => Task.FromResult(new List<SongHit>()),
		};
	}

	private static async Task<List<SongHit>> NeteaseSearchAsync(string keyword, int limit)
	{
		try
		{
			var songs = await NeteaseApi.SearchAsync(keyword, Math.Clamp(limit, 1, 60)).ConfigureAwait(false);
			return songs
				.Select(s => new SongHit(TrackRef.Net(s.Id, s.DurationMs, s.Name), s.Artists, s.FeeTag))
				.ToList();
		}
		catch { return new List<SongHit>(); }
	}

	// ------------------------------------------------------------ 统一下载

	/// <summary>按来源把整首音频下载到 dest（dest 所在目录须已存在）。</summary>
	public static async Task<NeteaseApi.DownloadOutcome> DownloadAsync(TrackRef tr, string dest)
	{
		try
		{
			if (tr.Source != MusicSource.Netease || tr.NetId <= 0)
				return new NeteaseApi.DownloadOutcome(NeteaseApi.DownloadStatus.Unavailable, 0,
					"非网易云曲目，无法取流");

			return await NeteaseApi.DownloadSongAsync(tr.NetId, dest).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			try { if (File.Exists(dest)) File.Delete(dest); } catch { /* 忽略 */ }
			return new NeteaseApi.DownloadOutcome(NeteaseApi.DownloadStatus.Error, 0, ex.Message);
		}
	}
}
