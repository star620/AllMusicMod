using AllMusicMod.Net;
using AllMusicMod.Netease;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace AllMusicMod.Core;

/// <summary>
/// 多人点歌的唯一权威裁决/应用/广播入口（路线A：网络只传控制，各端本地发声/拉流）。
/// 仅处理在线（MP3）曲目，服务端权威共享队列：点歌/排队/广播/自动下首。
///  - 权威进程 = 单机(netMode==1) 或 服务器(==3，含房主)；纯客户端(==2)只收到广播后本地应用。
///  - 在线曲：服务器只广播「TrackRef Key(含 id/时长/歌名) + 起播提前量」，各端自行下载/缓存后本地播放；
///    服务器按「广播后 lead + 曲长 + 尾缓冲」推进下一首；纯客户端用下载+Prepared 对齐起播点。
///  - 单机：无服务器，同一套权威逻辑直接本地应用（既不广播也不收包）。
/// </summary>
public static class AllMusicCoordinator
{
	/// <summary>在线曲起播提前量(ms)：广播后给各端留的下载/缓冲窗口，之后才计曲长。</summary>
	public const int NeteaseLeadMs = 2500;

	/// <summary>在线曲曲长计时尾缓冲(ms)。置 0：前一曲曲长一到立即推进下一首，
	/// 配合起播提前量(NeteaseLeadMs)把两曲间隔控制在约 3 秒内完成切换。</summary>
	public const int NeteaseTailMs = 0;

	/// <summary>取流看门狗(ms)：起播请求发出后超过该时长仍未真正发声（取流/下载卡死），判定该曲不可播并跳到下一首。</summary>
	public const int NeteaseStuckMs = 20000;

	/// <summary>服务器集中取流超时(ms)：超时即放弃直链，回退广播 ControlPlay 让各端自行取流，避免队列被挂起。</summary>
	public const int ServerFetchTimeoutMs = 12000;

	private static readonly double TickToMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;

	private static readonly object QueueLock = new();

	/// <summary>队列里的一首歌及其点歌人。</summary>
	private sealed record QueueEntry(TrackRef Track, string Adder);

	/// <summary>权威合并队列（1-base，播放/展示顺序一致，不含当前播放）。</summary>
	private static readonly List<QueueEntry> Queue = new();

	private static TrackRef? _current;
	/// <summary>当前曲目由谁点播（单机=本机名；服务器=发起请求的玩家名）。</summary>
	private static string _currentAdder = "";
	private static bool _netClockRunning;
	private static double _netRemainMs;
	private static long _lastNetTick;
	private static int _netNominalStartTick; // 服务器：广播时刻+lead 后的名义起播点(env ms)
	private static bool _netPaused;

	/// <summary>服务器：当前曲已取到并将广播/已广播的 CDN 直链（供中途加入的客户端直接追赶）。</summary>
	private static string? _currentCdnUrl;
	/// <summary>服务器：正在等待“带登录态取流”完成后广播（集中取流，成员无需各自登录）。</summary>
	private static bool _serverFetchPending;
	private static Task<string?>? _serverFetchTask;
	private static TrackRef? _serverFetchTr;
	/// <summary>服务器集中取流发起时刻（ms），用于超时兜底。</summary>
	private static int _serverFetchStartTick;
	/// <summary>本曲起播请求（RequestPlay）发出时刻（ms），用于取流卡死看门狗。</summary>
	private static int _netRequestTick;

	private static bool IsAuthority =>
		Main.netMode == NetmodeID.SinglePlayer || Main.netMode == NetmodeID.Server;

	private static bool IsServer => Main.netMode == NetmodeID.Server;

	private static bool CanAudibleLocal =>
		!Main.dedServ && (Main.netMode == NetmodeID.SinglePlayer || IsServer);

	private static void Log(string m) =>
		ModContent.GetInstance<AllMusicMod>().Logger.Info("[AllMusic] " + m);

	private static void ChatLine(string m, Color c)
	{
		if (Main.netMode == NetmodeID.Server)
			Log(m);
		else
			Main.NewText(m, c);
	}

	// ============================================================ 请求入口

	public static bool HandleRequest(int whoAmI, AllMusicMessageType op, string? arg, out string? reason)
	{
		reason = null;
		if (!Permission.Can(whoAmI, PermFor(op)))
		{
			reason = PermFor(op) == AllMusicPerm.Elevated
				? "该操作需要 OP（提升）权限"
				: "无权限执行该操作";
			return false;
		}

		lock (QueueLock)
		{
			string adder = ResolveAdder(whoAmI);
			switch (op)
			{
				case AllMusicMessageType.RequestPlay:
					PlayRequest(arg, adder);
					break;
				case AllMusicMessageType.RequestForcePlay:
					ForcePlay(arg, adder);
					break;
				case AllMusicMessageType.RequestQueueAdd:
					QueueAdd(arg, adder);
					break;
				case AllMusicMessageType.RequestQueueRemove:
					QueueRemove(arg);
					break;
				case AllMusicMessageType.RequestStop:
					StopCurrent();
					TryAutoStart();
					break;
				case AllMusicMessageType.RequestSkip:
					SkipToNext();
					break;
				case AllMusicMessageType.RequestPause:
					PauseCurrent();
					break;
				case AllMusicMessageType.RequestResume:
					ResumeCurrent();
					break;
				case AllMusicMessageType.RequestLoop:
					bool on = arg == "1";
					AllMusicPlayback.LoopOn = on;
					if (IsServer)
						SendControl(AllMusicMessageType.ControlLoop, on ? "1" : "0");
					break;
				case AllMusicMessageType.RequestQueueMove:
				default:
					break; // 上移/下移暂未实现
			}
			// 服务器集中取流期间暂缓广播：等直链就绪后由 Tick 统一广播（避免客户端先自取流又被打断）
			if (!_serverFetchPending)
				SyncUiAndBroadcast();
		}
		return true;
	}

	private static AllMusicPerm PermFor(AllMusicMessageType op) => op switch
	{
		AllMusicMessageType.RequestPlay or AllMusicMessageType.RequestForcePlay or AllMusicMessageType.RequestQueueAdd => AllMusicPerm.Any,
		_ => AllMusicPerm.Elevated,
	};

	/// <summary>把发起请求的 whoAmI 解析为可显示的点歌人名字（单机=本机名，服务器=对应玩家名）。</summary>
	private static string ResolveAdder(int whoAmI)
	{
		try
		{
			if (whoAmI >= 0 && whoAmI < Main.maxPlayers && Main.player[whoAmI] is { } pl)
			{
				string n = (pl.name ?? "").Trim();
				if (n.Length > 0)
					return n;
			}
		}
		catch { /* 忽略：解析失败回退空 */ }
		return "";
	}

	/// <summary>点歌人非空时给聊天提示追加“由 X 点”。</summary>
	private static string AddNote(string adder)
		=> string.IsNullOrEmpty(adder) ? "" : $"（由{adder}点）";

	// ---- 请求处理（须持 QueueLock）----

	private static void PlayRequest(string? arg, string adder)
	{
		var tr = TrackRef.Parse(arg ?? "");
		if (!PrepareEntry(ref tr))
			return;

		if (_current != null)
		{
			Queue.Add(new QueueEntry(tr, adder));
			ChatLine($"已加入播放队列（#{Queue.Count}）：{tr.DisplayName}{AddNote(adder)}", Color.White);
			return;
		}
		PlayNow(tr, adder);
	}

	private static void QueueAdd(string? arg, string adder)
	{
		var tr = TrackRef.Parse(arg ?? "");
		if (!PrepareEntry(ref tr))
			return;
		Queue.Add(new QueueEntry(tr, adder));
		ChatLine($"已加入播放队列（#{Queue.Count}）：{tr.DisplayName}{AddNote(adder)}", Color.White);
	}

	/// <summary>右键强播：校验通过后打断当前播放，立即改播该曲（不清理队列）。</summary>
	private static void ForcePlay(string? arg, string adder)
	{
		var tr = TrackRef.Parse(arg ?? "");
		if (!PrepareEntry(ref tr))
			return;
		bool wasPlaying = _current != null;
		StopCurrent(); // 停掉当前（含本地 MP3 与服务器 ControlStop），再立即起播新曲
		PlayNow(tr, adder);
		ChatLine(wasPlaying
			? $"已切换并播放：{tr.DisplayName}{AddNote(adder)}"
			: $"正在播放：{tr.DisplayName}{AddNote(adder)}", wasPlaying ? Color.Gold : Color.White);
	}

	/// <summary>在线曲先校验来源引用字段非空；本工程无本地 MIDI。返回 false 表示应拒绝该条目。</summary>
	private static bool PrepareEntry(ref TrackRef tr)
	{
		if (tr.Name.Length == 0 || !tr.IsNet)
			return false;
		return IsValidNetRef(tr);
	}

	/// <summary>在线曲目的来源引用是否有效（网易云看数字 id）。</summary>
	private static bool IsValidNetRef(in TrackRef tr) => tr.Source switch
	{
		MusicSource.Netease => tr.NetId > 0,
		_ => false,
	};

	private static void QueueRemove(string? arg)
	{
		if (!int.TryParse(arg, out int idx) || idx < 1 || idx > Queue.Count)
			return;
		Queue.RemoveAt(idx - 1);
	}

	private static void StopCurrent()
	{
		_current = null;
		_currentAdder = "";
		_netClockRunning = false;
		_netPaused = false;
		_currentCdnUrl = null;
		CancelPendingFetch();
		if (IsServer)
			SendControl(AllMusicMessageType.ControlStop, null);
		if (!Main.dedServ)
			NeteaseMp3Player.StopMp3();
	}

	/// <summary>丢弃未完成的“服务器取流”状态（须持 QueueLock）。</summary>
	private static void CancelPendingFetch()
	{
		_serverFetchPending = false;
		_serverFetchTask = null;
		_serverFetchTr = null;
	}

	/// <summary>确保服务器进程已加载本机保存的网易云登录态（取流用）。</summary>
	private static bool EnsureServerAuth()
	{
		if (!NeteaseSession.HasAuth)
			NeteaseSession.Load();
		return NeteaseSession.HasAuth;
	}

	private static void SkipToNext()
	{
		StopCurrent();
		TryAutoStart();
	}

	private static void PauseCurrent()
	{
		var cur = _current;
		if (cur == null || !cur.Value.IsNet)
			return;
		_netPaused = true;
		if (!Main.dedServ)
		{
			// 正在发声→暂停；还没开始(下载/待播)→取消本地准备，恢复时再拉
			NeteaseMp3Player.GetStatus(out var phase, out _, out _, out _);
			if (phase == Mp3Phase.Playing)
				NeteaseMp3Player.Pause();
			else if (phase is Mp3Phase.Downloading or Mp3Phase.Prepared)
				NeteaseMp3Player.StopMp3();
		}
		if (IsServer)
			SendControl(AllMusicMessageType.ControlPause, null);
	}

	private static void ResumeCurrent()
	{
		var cur = _current;
		if (cur == null || !cur.Value.IsNet)
			return;
		_netPaused = false;
		if (!Main.dedServ)
		{
			NeteaseMp3Player.GetStatus(out var phase, out _, out _, out _);
			if (phase == Mp3Phase.Playing)
				NeteaseMp3Player.Resume();
			else if (phase is Mp3Phase.Idle or Mp3Phase.Failed)
				NeteaseMp3Player.RequestPlay(cur.Value, 0); // 之前被取消，重新拉流
		}
		if (IsServer)
			SendControl(AllMusicMessageType.ControlResume, null);
	}

	// ---- 起播（须持 QueueLock）----

	private static void PlayNow(TrackRef tr, string adder)
	{
		StartNetNow(tr, adder);
	}

	private static void StartNetNow(TrackRef tr, string adder)
	{
		_current = tr;
		_currentAdder = adder;
		_netClockRunning = false;
		_netPaused = false;
		_netRequestTick = Environment.TickCount; // 看门狗基准：本曲取流/起播从此刻开始计时

		// 服务器若已保存网易云登录态：先集中取真实 CDN 直链再广播（成员无需各自登录/会员）。
		// 取流是网络 I/O，放后台；完成后由 Tick 在主线程补广播（不阻塞服务器主循环）。
		bool serverFetch = IsServer && tr.IsNet && IsValidNetRef(tr) && EnsureServerAuth();
		if (serverFetch)
		{
			_serverFetchPending = true;
			_serverFetchTr = tr;
			_serverFetchStartTick = Environment.TickCount;
			long id = tr.NetId;
			_serverFetchTask = Task.Run(() => NeteaseApi.GetLoggedInPlayUrlAsync(id));
			return;
		}

		AnnouncePlay(tr, null);

		if (tr.DurationMs <= 0)
			Log($"在线曲目时长缺失：{tr.DisplayName}");
	}

	/// <summary>真正向各端广播并(若可发声)本地起播。cdnUrl 非空时下发 ControlPlayUrl，否则回退 ControlPlay（各端自取流）。</summary>
	private static void AnnouncePlay(TrackRef tr, string? cdnUrl)
	{
		_netRemainMs = tr.NetDurationMs + (IsServer ? NeteaseTailMs : 0);
		_lastNetTick = System.Diagnostics.Stopwatch.GetTimestamp();
		_netNominalStartTick = Environment.TickCount + (IsServer ? NeteaseLeadMs : 0);
		_currentCdnUrl = IsServer ? cdnUrl : null;

		if (IsServer)
		{
			if (!string.IsNullOrEmpty(cdnUrl))
				SendServerUrl(cdnUrl, tr.Key, NeteaseLeadMs);
			else
				SendControl(AllMusicMessageType.ControlPlay, tr.Key, NeteaseLeadMs);
		}

		// 单机/房主机本地拉流（lead 0：能出声就开始，在线端点尽量对齐）
		if (CanAudibleLocal)
			NeteaseMp3Player.RequestPlay(tr, 0);

		// 预取队列下一首：让“上一首播完后”的切换不再等下载，稳定落在数秒内
		PrefetchNext();
	}

	/// <summary>后台预取队列下一首到本地缓存（失败静默：真正起播时会重新取流）。</summary>
	private static void PrefetchNext()
	{
		TrackRef next;
		lock (QueueLock)
		{
			if (Queue.Count == 0)
				return;
			next = Queue[0].Track;
		}
		if (!next.IsNet || !IsValidNetRef(next))
			return;
		string dest = NeteaseMp3Player.CachePath(next);
		try
		{
			if (File.Exists(dest) && new FileInfo(dest).Length >= 20000)
				return; // 已有完整缓存，无需预取
		}
		catch { return; }

		Task.Run(async () =>
		{
			try { await MusicSources.DownloadAsync(next, dest).ConfigureAwait(false); }
			catch { /* 预取失败忽略 */ }
		});
	}

	/// <summary>广播服务器直链播放消息：+ string key + string url + int leadMs。</summary>
	private static void SendServerUrl(string url, string key, int leadMs)
	{
		try
		{
			var p = ModContent.GetInstance<AllMusicMod>().GetPacket();
			p.Write((byte)AllMusicMessageType.ControlPlayUrl);
			p.Write(key);
			p.Write(url);
			p.Write(leadMs);
			p.Send();
		}
		catch (Exception ex) { Log("直链广播失败: " + ex.Message); }
	}

	/// <summary>由 Tick 主线程调用（须持 QueueLock）：取流完成后补广播/起播；超时则放弃直链回退各端自取。</summary>
	private static bool TickServerFetch()
	{
		if (_serverFetchTask == null || _serverFetchTr is not { } pending)
		{
			CancelPendingFetch();
			return false;
		}
		if (!_serverFetchTask.IsCompleted)
		{
			// 集中取流长时间无响应：放弃直链，改广播 ControlPlay 让各端自行取流，避免整条队列被挂住
			if (Environment.TickCount - _serverFetchStartTick < ServerFetchTimeoutMs)
				return false;
			CancelPendingFetch();
			Log($"服务器集中取流超时({ServerFetchTimeoutMs / 1000}s)，回退各端自取流：{pending.DisplayName}");
			if (_current is { } stuck && stuck.Key == pending.Key)
			{
				AnnouncePlay(stuck, null);
				return true;
			}
			return false;
		}

		string? url;
		try { url = _serverFetchTask.Result; } catch { url = null; }
		CancelPendingFetch();

		// 取流期间曲目可能已被停/换，过期结果直接丢弃
		if (_current is not { } cur || cur.Key != pending.Key)
			return false;

		if (string.IsNullOrEmpty(url))
			Log($"服务器登录态取流未返回直链（可能无版权/需更高会员），回退各端自取：{pending.DisplayName}");
		else
			Log($"服务器已带登录态取到直链并广播：{pending.DisplayName}");

		AnnouncePlay(cur, url);
		return true;
	}

	// ---- 队列推进（须持 QueueLock）----

	/// <summary>当前空闲且队列非空时弹出下一首。返回是否发生了起播（用于判断是否需要广播快照）。</summary>
	private static bool TryAutoStart()
	{
		if (_current != null || Queue.Count == 0)
			return false;
		var entry = Queue[0];
		Queue.RemoveAt(0);
		PlayNow(entry.Track, entry.Adder);
		return true;
	}

	private static bool AdvanceCurrent()
	{
		StopCurrent();
		bool started = TryAutoStart();
		return started || _current == null; // 无论是否起播，都发生了状态变化需广播
	}

	/// <summary>每帧权威推进：在线曲看名义计时/本地播放状态。返回是否发生变更。</summary>
	public static bool Tick()
	{
		if (!IsAuthority)
			return false;

		lock (QueueLock)
		{
			bool changed;
			if (_serverFetchPending)
			{
				// 服务器集中取流中：等后台取到直链后在主线程补广播
				changed = TickServerFetch();
			}
			else if (_current == null)
			{
				changed = TryAutoStart();
			}
			else
			{
				changed = TickNetClock(_current.Value);
			}

			// 起播刚切到“等待服务器取流”时暂缓广播，由取流完成那一帧统一下发
			if (changed && !_serverFetchPending)
				SyncUiAndBroadcast();
			return changed;
		}
	}

	private static bool TickNetClock(TrackRef tr)
	{
		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		double dtMs = (now - _lastNetTick) * TickToMs;
		_lastNetTick = now; // 先推进基准，暂停的时长不会在恢复后被一笔算进

		if (_netPaused)
			return false;

		if (IsServer)
		{
			// 服务器按名义计时：广播时刻 + lead 后才开始扣曲长
			if (!_netClockRunning && Environment.TickCount >= _netNominalStartTick)
				_netClockRunning = true;
			if (_netClockRunning)
				_netRemainMs -= dtMs;
		}
		else
		{
			// 单机：跟随本地播放器真实状态
			NeteaseMp3Player.GetStatus(out var phase, out var song, out _, out bool paused);

			// 纠偏：本地其实在发声（MediaPlayer 状态为 Playing），只是内部相位被切歌瞬间的状态事件
			// 误判为 Idle → 直接接管计时，绝不能在此时把正在播的歌当“未起播”跳掉。
			if (phase == Mp3Phase.Idle && !_netClockRunning && NeteaseMp3Player.IsAudible())
			{
				_netClockRunning = true;
				_netRemainMs = tr.NetDurationMs;
			}

			// 看门狗：起播请求发出后长时间停在“下载/待播”（取流卡死、下载无响应）→ 跳过该曲，
			// 否则 _netClockRunning 永远不为真，队列会永久静音卡住。
			if (phase is Mp3Phase.Downloading or Mp3Phase.Prepared &&
				Environment.TickCount - _netRequestTick > NeteaseStuckMs)
			{
				Log($"在线曲取流超时({NeteaseStuckMs / 1000}s)，跳过：{tr.DisplayName}");
				ChatLine($"「{tr.DisplayName}」取流超时，已跳到下一首", Color.Orange);
				return AdvanceCurrent();
			}

			if (phase == Mp3Phase.Failed || (phase == Mp3Phase.Idle && _netClockRunning))
			{
				Log($"在线播放结束/不可用：{tr.DisplayName}");
				return AdvanceCurrent();
			}
			// 空闲且确实无声、本曲从未起播（起播请求未生效）：交给看门狗兜底跳过
			if (phase == Mp3Phase.Idle && !_netClockRunning && !NeteaseMp3Player.IsAudible() &&
				Environment.TickCount - _netRequestTick > NeteaseStuckMs)
			{
				Log($"在线曲未能起播，跳过：{tr.DisplayName}");
				return AdvanceCurrent();
			}
			if (phase == Mp3Phase.Playing && !paused)
			{
				if (!_netClockRunning)
					_netClockRunning = true; // 真正出声起算
				_netRemainMs -= dtMs;
			}
		}

		// 曲长名义计时只用于服务器（无本地声音/需要给各端统一节拍）。
		// 单机若沿用名义曲长，一旦搜索元数据时长缺失或偏短，会“播一半就跳下一首”，
		// 因此单机一律以本地播放器真实播完为准（本地相位 → Idle 时推进）。
		if (IsServer && _netClockRunning && _netRemainMs <= 0)
		{
			Log($"在线曲长计时到点：{tr.DisplayName}");
			return AdvanceCurrent();
		}
		return false;
	}

	// ============================================================ 客户端应用（纯客户端 / 收到的 Control*）

	/// <summary>客户端收到 ControlPlayUrl：用服务器下发的 CDN 直链直接播放（无需本机登录态，覆盖本机自取流）。</summary>
	public static void ApplyControlUrl(string key, string url, int leadMs)
	{
		var tr = TrackRef.Parse(key ?? "");
		if (tr.IsNet && IsValidNetRef(tr) && !string.IsNullOrWhiteSpace(url))
			NeteaseMp3Player.RequestPlayUrl(tr, Math.Max(0, leadMs), url);
	}

	/// <summary>客户端收到 Control*（或历史单机直连路径）应用：各自本地发声/拉流。此方法仅纯客户端走。</summary>
	public static void ApplyControl(AllMusicMessageType control, string? arg, int leadMs)
	{
		switch (control)
		{
			case AllMusicMessageType.ControlPlay:
				var tr = TrackRef.Parse(arg ?? "");
				if (tr.IsNet && IsValidNetRef(tr))
					NeteaseMp3Player.RequestPlay(tr, Math.Max(0, leadMs));
				break;
			case AllMusicMessageType.ControlStop:
				if (!Main.dedServ)
					NeteaseMp3Player.StopMp3();
				break;
			case AllMusicMessageType.ControlPause:
				if (!Main.dedServ)
					NeteaseMp3Player.Pause();
				break;
			case AllMusicMessageType.ControlResume:
				if (!Main.dedServ)
					NeteaseMp3Player.Resume();
				break;
			case AllMusicMessageType.ControlLoop:
				AllMusicPlayback.LoopOn = arg == "1";
				break;
		}
	}

	// ============================================================ 展示态与广播

	/// <summary>把权威态写进 AllMusicNetService（单机 UI 与服务器同步都读这套），服务器另广播快照。</summary>
	private static void SyncUiAndBroadcast()
	{
		lock (QueueLock)
		{
			AllMusicNetService.QueueItems.Clear();
			foreach (var e in Queue)
				AllMusicNetService.QueueItems.Add(new AllMusicNetService.QueueItem(e.Track.DisplayName, e.Adder));

			var cur = _current;
			AllMusicNetService.CurrentName = cur?.DisplayName;
			AllMusicNetService.CurrentIsNet = cur?.IsNet ?? false;
			AllMusicNetService.IsPlaying = cur != null;
			AllMusicNetService.CurrentAdder = cur == null ? "" : _currentAdder;
			AllMusicNetService.CurrentNetId = cur.HasValue && cur.Value.IsNet ? cur.Value.NetId : 0;
			AllMusicNetService.IsPaused = cur switch
			{
				null => false,
				_ when cur.Value.IsNet => _netPaused,
				_ => false,
			};

			if (IsServer)
			{
				Broadcast(QueueSnapshotPayload());
				Broadcast(CurrentPlayingPayload());
			}
		}
	}

	/// <summary>新玩家加入对齐：当前播放 + 队列快照 + 循环状态；正在放直链曲时补发直链供其直接追赶。</summary>
	public static void SendInitialToClient(int whoAmI)
	{
		if (!IsServer)
			return;
		lock (QueueLock)
		{
			SendTo(QueueSnapshotPayload(), whoAmI);
			SendTo(CurrentPlayingPayload(), whoAmI);
			if (_current is { } cur && cur.IsNet && IsValidNetRef(cur)
				&& !string.IsNullOrEmpty(_currentCdnUrl))
				SendTo(ServerUrlPayload(cur.Key, _currentCdnUrl!, 0), whoAmI);
			SendTo(LoopStatePayload(), whoAmI);
		}
	}

	private static byte[] ServerUrlPayload(string key, string url, int lead)
	{
		using var ms = new MemoryStream();
		using var w = new BinaryWriter(ms);
		w.Write((byte)AllMusicMessageType.ControlPlayUrl);
		w.Write(key);
		w.Write(url);
		w.Write(lead);
		return ms.ToArray();
	}

	private static byte[] QueueSnapshotPayload()
	{
		using var ms = new MemoryStream();
		using var w = new BinaryWriter(ms);
		w.Write((byte)AllMusicMessageType.QueueSnapshot);
		w.Write(Queue.Count);
		foreach (var e in Queue)
		{
			w.Write(e.Track.Key);
			w.Write(e.Adder);
		}
		return ms.ToArray();
	}

	private static byte[] CurrentPlayingPayload()
	{
		using var ms = new MemoryStream();
		using var w = new BinaryWriter(ms);
		w.Write((byte)AllMusicMessageType.CurrentPlaying);
		bool idle = _current == null;
		w.Write(idle);
		w.Write(_current?.Key ?? "");
		bool paused = _current switch
		{
			null => false,
			_ when _current.Value.IsNet => _netPaused,
			_ => false,
		};
		w.Write(paused);
		w.Write(_currentAdder ?? ""); // 当前曲点歌人（追加字段）
		return ms.ToArray();
	}

	private static byte[] LoopStatePayload()
	{
		using var ms = new MemoryStream();
		using var w = new BinaryWriter(ms);
		w.Write((byte)AllMusicMessageType.ControlLoop);
		w.Write(AllMusicPlayback.LoopOn ? "1" : "0");
		return ms.ToArray();
	}

	private static void SendControl(AllMusicMessageType control, string? arg)
	{
		try
		{
			var p = ModContent.GetInstance<AllMusicMod>().GetPacket();
			p.Write((byte)control);
			if (arg != null && (control == AllMusicMessageType.ControlPlay || control == AllMusicMessageType.ControlLoop))
				p.Write(arg);
			p.Send();
		}
		catch (Exception ex) { Log("广播失败: " + ex.Message); }
	}

	private static void SendControl(AllMusicMessageType control, string? arg, int leadMs)
	{
		try
		{
			var p = ModContent.GetInstance<AllMusicMod>().GetPacket();
			p.Write((byte)control);
			if (arg != null)
				p.Write(arg);
			if (control == AllMusicMessageType.ControlPlay)
				p.Write(leadMs);
			p.Send();
		}
		catch (Exception ex) { Log("广播失败: " + ex.Message); }
	}

	private static void Broadcast(byte[] payload)
	{
		try
		{
			var p = ModContent.GetInstance<AllMusicMod>().GetPacket();
			foreach (var b in payload) p.Write(b);
			p.Send();
		}
		catch (Exception ex) { Log("广播失败: " + ex.Message); }
	}

	private static void SendTo(byte[] payload, int whoAmI)
	{
		try
		{
			var p = ModContent.GetInstance<AllMusicMod>().GetPacket();
			foreach (var b in payload) p.Write(b);
			p.Send(toClient: whoAmI);
		}
		catch (Exception ex) { Log("定向发送失败: " + ex.Message); }
	}

	/// <summary>离开世界/回主菜单时清场：清空本进程内残留的权威状态与本地播放，防止带进下一局。</summary>
	public static void ResetSession()
	{
		lock (QueueLock)
		{
			Queue.Clear();
			_current = null;
			_currentAdder = "";
			_netClockRunning = false;
			_netPaused = false;
			_netRemainMs = 0;
			_currentCdnUrl = null;
			CancelPendingFetch();

			AllMusicPlayback.LoopOn = false;
			if (!Main.dedServ)
				NeteaseMp3Player.StopMp3();

			AllMusicNetService.QueueItems.Clear();
			AllMusicNetService.CurrentName = null;
			AllMusicNetService.CurrentIsNet = false;
			AllMusicNetService.IsPlaying = false;
			AllMusicNetService.IsPaused = false;
			AllMusicNetService.CurrentAdder = "";
			AllMusicNetService.CurrentNetId = 0;
		}
	}
}