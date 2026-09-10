using System.Text.Json.Nodes;

namespace AllMusicMod.Netease;

/// <summary>
/// 网易云搜索结果中的一首歌（单机原型仅用展示字段 + id 拉播放直链）。
/// </summary>
public sealed class NeteaseSong
{
	public long Id { get; init; }
	public string Name { get; init; } = "";
	public string Artists { get; init; } = "";
	/// <summary>时长 ms。</summary>
	public int DurationMs { get; init; }
	/// <summary>0 免费 / 1 VIP / 4 付费 / 8 单曲&专辑收费（是否真的放不了以下载结果为准）。</summary>
	public int Fee { get; init; }

	public string FeeTag => Fee switch
	{
		1 => "[VIP]",
		4 => "[付费]",
		8 => "[专辑]",
		_ => "",
	};

	/// <summary>从 binaryify 搜索接口的单曲 JSON 节点解析；失败返回 null。</summary>
	public static NeteaseSong? FromJson(JsonNode? n)
	{
		if (n == null)
			return null;
		try
		{
			long id = n["id"]?.GetValue<long>() ?? 0;
			if (id <= 0)
				return null;

			string name = n["name"]?.GetValue<string>() ?? "";

			var artistParts = new List<string>();
			var artists = n["artists"]?.AsArray() ?? n["ar"]?.AsArray();
			if (artists != null)
			{
				foreach (var a in artists)
				{
					var an = a?["name"]?.GetValue<string>();
					if (!string.IsNullOrEmpty(an))
						artistParts.Add(an!);
				}
			}

			int dur = n["duration"]?.GetValue<int>() ?? 0;
			int fee = n["fee"]?.GetValue<int>() ?? 0;

			if (name.Length == 0 && artistParts.Count == 0)
				return null;

			return new NeteaseSong
			{
				Id = id,
				Name = name,
				Artists = string.Join("/", artistParts),
				DurationMs = dur,
				Fee = fee,
			};
		}
		catch
		{
			return null;
		}
	}
}
