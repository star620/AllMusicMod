using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Media;
using AllMusicMod.Core;
using AllMusicMod.Net;
using Terraria;

namespace AllMusicMod.Netease;

public enum Mp3Phase
{
	Idle,
	Downloading,
	/// <summary>下载完成，等待到达广播的同步起播时刻（多人对齐用）。</summary>
	Prepared,
	Playing,
	Failed,
}

/// <summary>
/// 单机原型：网易云 MP3 的本地播放状态机（MediaPlayer 播放缓存目录里的 .mp3）。
/// 设计约束：
///  - 所有 XNA/MediaPlayer 与 PlaybackScheduler 的接触只允许在主线程（UI Tick / 点击回调）；
///    下载/搜索跑后台 Task，完成后由 Poll() 在主线程落定。
///  - MIDI 与 MP3 是互斥单流：MP3 起播前 Stop 掉 MIDI 合成；任意 MIDI 开始（PlaybackScheduler.Play）
///    时通过 OnMidiStarts() 停掉 MP3，并在停 MP3 期间挂起 MIDI 队列的自动顺延（防互抢）。
/// </summary>
public static class NeteaseMp3Player
{
	public const string CacheFolderName = "NeteaseCache";

	private static readonly object Gate = new();
	private static Mp3Phase _phase = Mp3Phase.Idle;
	private static TrackRef? _song;
	private static string _message = "";

	private static int _reqId;
	private static CancellationTokenSource? _cts;
	private static NeteaseApi.DownloadOutcome? _downloadDone;
	private static Song? _xnaSong;
	private static float _volume = 1f;
	private static bool _paused;
	/// <summary>目标起播时刻(ms)；多人同步广播带 lead 时在此刻之后才真正 Play。</summary>
	private static int _startAtTick;

	/// <summary>当前请求是否来自服务器直链(ControlPlayUrl)。</summary>
	private static bool _viaServerUrl;
	/// <summary>当前请求的服务器直链URL（仅 _viaServerUrl 时有意义）。</summary>
	private static string _directUrl = "";

	private static volatile bool _endedNaturally;
	private static volatile bool _expectedStop;
	private static bool _eventHooked;

	private static string CacheDir()
	{
		var dir = Path.Combine(Main.SavePath, CacheFolderName);
		Directory.CreateDirectory(dir);
		return dir;
	}

	public static string CachePath(in TrackRef tr)
	{
		string file = tr.Source switch
		{
			MusicSource.Netease => $"net_{tr.NetId}.mp3",
			_ => "midi_invalid.mp3",
		};
		return Path.Combine(CacheDir(), file);
	}

	/// <summary>供 UI 每帧展示。快照拷贝，不持锁跨调用。</summary>
	public static void GetStatus(out Mp3Phase phase, out TrackRef? song, out string message, out bool paused)
	{
		lock (Gate)
		{
			phase = _phase;
			song = _song;
			message = _message;
			paused = _paused;
		}
	}

	/// <summary>主线程每帧调用：落定下载结果 / 到达同步起播点 / 检测自然播完。</summary>
	public static void Poll()
	{
		if (Main.dedServ)
			return;
		EnsureEvent();

		NeteaseApi.DownloadOutcome? done = null;
		lock (Gate)
		{
			if (_downloadDone != null)
			{
				done = _downloadDone;
				_downloadDone = null;
			}
		}

		bool tryStart = false;
		if (done != null)
		{
			lock (Gate)
			{
				if (_phase == Mp3Phase.Downloading)
				{
					if (done.Status != NeteaseApi.DownloadStatus.Ok)
					{
						_phase = Mp3Phase.Failed;
						_message = done.Detail;
						// 把真实失败原因打到游戏聊天/日志，便于定位（风控？无版权？网络？）
						string tag = _song?.SourceTag ?? "[在线]";
						AllMusicNetService.Messsage($"{tag}取流失败：{done.Detail}", Color.Orange);
					}
					else if (Environment.TickCount >= _startAtTick)
					{
						tryStart = true; // 到起播点了，直接播
					}
					else
					{
						_phase = Mp3Phase.Prepared;
						_message = "已就绪，等待同步播放…";
					}
				}
			}
		}
		else
		{
			lock (Gate)
			{
				if (_phase == Mp3Phase.Prepared && Environment.TickCount >= _startAtTick)
					tryStart = true;
			}
		}

		if (tryStart)
			StartLocalPlayback();

		bool endedByState = false;
		lock (Gate)
		{
			// FNA 的 MediaStateChanged 不一定可靠：直接轮询“播完即 Stopped”作为兜底。
			// 起播后留 2s 防抖，避免 Play() 尚未真正出声前 State 短暂仍是 Stopped 被误判。
			if (_phase == Mp3Phase.Playing && !_paused &&
				Environment.TickCount - _playStartedTick > 2000 &&
				TryStateStopped() &&
				!_expectedStop)
			{
				endedByState = true;
			}
		}

		if (_endedNaturally || endedByState)
		{
			_endedNaturally = false;
			lock (Gate)
			{
				// 事件驱动的“停止”同样要求已稳定播放 >2s 才算自然播完：
				// 起播/切歌瞬间的状态抖动不能把正在播的曲目置回 Idle（否则会莫名跳歌）。
				if (_phase == Mp3Phase.Playing && !_paused &&
					Environment.TickCount - _playStartedTick > 2000 && !_expectedStop)
				{
					_phase = Mp3Phase.Idle;
					_song = null;
					_message = "";
					_paused = false;
				}
			}
		}
	}

	private static int _playStartedTick = Environment.TickCount;

	private static bool TryStateStopped()
	{
		try { return MediaPlayer.State == MediaState.Stopped; }
		catch { return false; }
	}

	// ------------------------------------------------------------- 主线程操作

	/// <summary>
	/// 请求播放一首远程在线曲（点歌/同步广播，仅支持网易云）。缓存命中则立即(或 delayMs 后)播放，
	/// 否则后台按来源取流下载后按时刻播放。delayMs：多人广播起播提前量。
	/// </summary>
	public static void RequestPlay(TrackRef tr, int delayMs = 0)
		=> RequestPlayCore(tr, delayMs, null, false);

	/// <summary>按服务器广播的 CDN 直链请求播放（ControlPlayUrl）。服务器带登录态取到的地址，视为权威来源。</summary>
	public static void RequestPlayUrl(TrackRef tr, int delayMs, string url)
		=> RequestPlayCore(tr, delayMs, string.IsNullOrWhiteSpace(url) ? null : url, true);

	private static void RequestPlayCore(TrackRef tr, int delayMs, string? directUrl, bool force)
	{
		if (Main.dedServ || !tr.IsNet || string.IsNullOrEmpty(tr.Name))
			return;
		if (tr.Source == MusicSource.Netease && tr.NetId <= 0)
			return;
		EnsureEvent();

		string dest = CachePath(tr);

		lock (Gate)
		{
			// 正在发声的同曲：不重复起播（即使直链重复推送也忽略）
			if (_phase == Mp3Phase.Playing && _song is { } play && play.Key == tr.Key)
				return;

			// 正在准备/下载的同曲：
			if ((_phase == Mp3Phase.Downloading || _phase == Mp3Phase.Prepared) &&
				_song is { } same && same.Key == tr.Key)
			{
				bool directSame = force && _viaServerUrl && directUrl != null && _directUrl == directUrl;
				if (!force || directSame)
					return; // 重复广播 / 与本机自取流重复
				// force：用服务器直链替换掉本机（大概率无VIP权限）的自取流
				_cts?.Cancel();
				_reqId++;
			}

			// 换成别的歌：若旧歌正在发声，立即停（切歌/下一首语义）
			bool wasAudible = _phase == Mp3Phase.Playing;
			if (wasAudible && _song is { } old && old.Key != tr.Key)
			{
				_expectedStop = true;
				try { MediaPlayer.Stop(); } catch { /* 无设备忽略 */ }
				_xnaSong = null;
			}

			_cts?.Cancel();
			_reqId++;
			int myId = _reqId;
			_cts = new CancellationTokenSource();
			var token = _cts.Token;

			_phase = Mp3Phase.Downloading;
			_song = tr;
			_message = "正在获取播放流…";
			_paused = false;
			_endedNaturally = false;
			_expectedStop = false;
			_startAtTick = Environment.TickCount + Math.Max(0, delayMs);
			_viaServerUrl = directUrl != null;
			_directUrl = directUrl ?? "";

			Task.Run(() => DownloadAndReportAsync(myId, tr, dest, token, directUrl), token);
		}
	}

	/// <summary>暂停当前网易云 MP3（多人 ControlPause / 单机暂停）。主线程。</summary>
	public static void Pause()
	{
		if (Main.dedServ)
			return;
		bool doPause = false;
		lock (Gate)
		{
			if (_phase == Mp3Phase.Playing && !_paused)
			{
				_paused = true;
				doPause = true;
			}
		}
		if (doPause)
		{
			try { MediaPlayer.Pause(); } catch { /* 忽略 */ }
		}
	}

	/// <summary>恢复当前网易云 MP3。主线程。</summary>
	public static void Resume()
	{
		if (Main.dedServ)
			return;
		bool doResume = false;
		lock (Gate)
		{
			if (_phase == Mp3Phase.Playing && _paused)
			{
				_paused = false;
				doResume = true;
			}
		}
		if (doResume)
		{
			try { MediaPlayer.Resume(); } catch { /* 忽略 */ }
		}
	}

	private static async Task DownloadAndReportAsync(int myId, TrackRef tr, string dest, CancellationToken token, string? directUrl)
	{
		try
		{
			// 已有缓存 → 直接可用（也顺手验证长度）
			if (File.Exists(dest) && new FileInfo(dest).Length >= 20000)
			{
				ReportDone(myId, new NeteaseApi.DownloadOutcome(NeteaseApi.DownloadStatus.Ok, 0, "cache"));
				return;
			}

			NeteaseApi.DownloadOutcome outcome;
			if (!string.IsNullOrEmpty(directUrl))
			{
				// 服务器直链（带登录态取到）→ 直接落地；失效时回退本机自取流
				outcome = await NeteaseApi.DownloadFromUrlAsync(directUrl!, dest).ConfigureAwait(false);
				if (outcome.Status != NeteaseApi.DownloadStatus.Ok)
				{
					try { if (File.Exists(dest)) File.Delete(dest); } catch { /* 忽略 */ }
					outcome = await MusicSources.DownloadAsync(tr, dest).ConfigureAwait(false);
				}
			}
			else
			{
				outcome = await MusicSources.DownloadAsync(tr, dest).ConfigureAwait(false);
			}
			ReportDone(myId, outcome);
		}
		catch (OperationCanceledException)
		{
			// 用户切歌 / 停播导致取消，静默
		}
		catch (Exception ex)
		{
			ReportDone(myId, new NeteaseApi.DownloadOutcome(NeteaseApi.DownloadStatus.Error, 0, ex.Message));
		}
	}

	private static void ReportDone(int myId, NeteaseApi.DownloadOutcome outcome)
	{
		lock (Gate)
		{
			if (myId != _reqId || _phase != Mp3Phase.Downloading)
				return; // 已过期的下载结果直接丢弃
			_downloadDone = outcome;
		}
	}

	/// <summary>主线程：把已下载文件交给 MediaPlayer 解码播放（验证 MP3 解码链）。</summary>
	private static void StartLocalPlayback()
	{
		TrackRef? song;
		lock (Gate)
		{
			if (_phase != Mp3Phase.Downloading && _phase != Mp3Phase.Prepared)
				return;
			song = _song;
		}
		if (song == null)
			return;

		string file = CachePath(song.Value);
		try
		{
			// 切换期屏蔽状态事件：Stop()/Play() 都会触发 MediaStateChanged，
			// 若不屏蔽，刚起播的曲目会被当成“自然播完”立刻置回 Idle
			// （表现为：歌其实在响，但进度条消失、20 秒后被看门狗切走）。
			_expectedStop = true;
			MediaPlayer.Stop();

			var s = Song.FromUri(Path.GetFileName(file), new Uri(Path.GetFullPath(file)));
			_xnaSong = s;
			MediaPlayer.Volume = MathHelper.Clamp(_volume, 0f, 1f);
			_playStartedTick = Environment.TickCount;
			MediaPlayer.Play(s);

			_endedNaturally = false;   // 清掉切换期可能残留的伪“播完”标记
			_expectedStop = false;     // 起播完成：此后真正的停止才视为自然播完
			_paused = false;

			lock (Gate)
			{
				if (_song is { } cur && cur.Key == song.Value.Key)
				{
					_phase = Mp3Phase.Playing;
					_message = "已开始播放（MP3 解码 OK）";
				}
			}
		}
		catch (Exception ex)
		{
			lock (Gate)
			{
				if (_song is { } cur && cur.Key == song.Value.Key)
				{
					_phase = Mp3Phase.Failed;
					_message = "MP3 播放失败：" + ex.Message;
				}
			}
		}
	}

	/// <summary>停掉当前 MP3（UI 停止按钮等调用）。主线程。</summary>
	public static void StopMp3()
	{
		if (Main.dedServ)
			return;
		EnsureEvent();

		_expectedStop = true;
		lock (Gate)
		{
			_cts?.Cancel();
			_reqId++;
			_phase = Mp3Phase.Idle;
			_song = null;
			_message = "";
			_downloadDone = null;
			_paused = false;
			_startAtTick = Environment.TickCount;
			_viaServerUrl = false;
			_directUrl = "";
		}
		try
		{
			MediaPlayer.Stop();
		}
		catch { /* 无设备等场景忽略 */ }
		_xnaSong = null;
	}

	public static void SetVolume(float v)
	{
		if (Main.dedServ)
			return;
		_volume = MathHelper.Clamp(v, 0f, 1f);
		try
		{
			MediaPlayer.Volume = _volume;
		}
		catch { /* 无设备忽略 */ }
	}

	public static float Volume => _volume;

	/// <summary>当前正在播放的本地位置(ms)。非播放态返回 0。主线程调用。</summary>
	public static long GetPositionMs()
	{
		lock (Gate)
		{
			if (_phase != Mp3Phase.Playing)
				return 0;
		}
		try { return (long)MediaPlayer.PlayPosition.TotalMilliseconds; }
		catch { return 0; }
	}

	/// <summary>本地是否在真实发声（无视内部相位）。权威侧用它纠偏“相位被事件抖动误判为未起播”的曲目。主线程调用。</summary>
	public static bool IsAudible()
	{
		if (Main.dedServ)
			return false;
		try { return MediaPlayer.State == MediaState.Playing; }
		catch { return false; }
	}

	private static void EnsureEvent()
	{
		if (_eventHooked)
			return;
		_eventHooked = true;
		MediaPlayer.MediaStateChanged += (_, _) =>
		{
			// 我们主动 Stop 时置 _expectedStop，避免把主动停止误判为“自然播完”。
			if (MediaPlayer.State == MediaState.Stopped && !_expectedStop)
				_endedNaturally = true;
		};
	}
}
