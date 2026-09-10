namespace AllMusicMod.Core;

/// <summary>极简播放状态标记（本工程无 MIDI 合成器，仅存放循环开关等静态标记）。</summary>
public static class AllMusicPlayback
{
    public static bool LoopOn { get; set; }
}