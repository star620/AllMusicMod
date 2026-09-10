using System.Globalization;

namespace AllMusicMod.Core;

/// <summary>点歌来源。Midi=本地文件；Netease=网易云在线曲（唯一在线取流源）。</summary>
public enum MusicSource : byte
{
	Midi,
	Netease,
}

/// <summary>
/// 统一曲目引用：一条队列项要么是本地 MIDI 歌（文件名），要么是网易云在线单曲。
/// 网络与队列快照用字符串 Key 传输，兼容旧的纯 MIDI 包格式（MIDI 的 Key 就是文件名本身）。
/// 远程 Key 带来源前缀避免冲突：WYY&lt;US&gt;id&lt;US&gt;dur&lt;US&gt;name。
/// </summary>
public readonly struct TrackRef
{
	private const string PNet = "WYY\u001F";   // 网易云：id 为数字

	public MusicSource Source { get; }
	public string Name { get; }
	/// <summary>网易云歌曲数字 id（Source==Netease 时有效）。</summary>
	public long NetId { get; }
	public int DurationMs { get; }

	// 兼容旧字段名（旧代码写 NetDurationMs）
	public int NetDurationMs => DurationMs;

	private TrackRef(MusicSource source, string name, long netId, int durationMs)
	{
		Source = source;
		Name = name ?? "";
		NetId = netId;
		DurationMs = durationMs;
	}

	/// <summary>是否远程在线曲（非本地 MIDI）。</summary>
	public bool IsNet => Source != MusicSource.Midi;

	public string SourceTag => Source switch
	{
		MusicSource.Netease => "[网易云]",
		_ => "",
	};

	public static TrackRef Midi(string name) => new(MusicSource.Midi, name, 0, 0);

	public static TrackRef Net(long id, int durationMs, string name)
		=> new(MusicSource.Netease, name, id, durationMs);

	/// <summary>入队/展示用的底层 Key（也即网络传输串）。</summary>
	public string Key => Source switch
	{
		MusicSource.Netease => $"{PNet}{NetId}\u001F{DurationMs}\u001F{Name}",
		_ => Name,
	};

	/// <summary>带来源标签的展示名（队列/列表/正在播放用）。</summary>
	public string DisplayName => IsNet ? Name + " " + SourceTag : Name;

	/// <summary>把网络/队列里的 Key 还原为 TrackRef；MIDI 名字原样返回（向后兼容旧数据）。</summary>
	public static TrackRef Parse(string key)
	{
		if (string.IsNullOrEmpty(key))
			return Midi(key ?? "");

		if (key.StartsWith(PNet, StringComparison.Ordinal))
		{
			var p = Fields(key, PNet);
			if (p != null && long.TryParse(p.Value.id, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
				return Net(id, p.Value.dur, p.Value.name);
			return Midi(key);
		}

		return Midi(key);
	}

	private static (string id, int dur, string name)? Fields(string key, string prefix)
	{
		var parts = key.Substring(prefix.Length).Split('\u001F');
		if (parts.Length < 3)
			return null;
		if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int dur))
			return null;
		return (parts[0], dur, string.Join('\u001F', parts.Skip(2)));
	}

	public override string ToString() => DisplayName;
}
