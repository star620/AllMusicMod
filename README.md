# AllMusic（AllMusicMod）
**作者**: 星梦（star620）
- **版本**: v0.1.0
> [!IMPORTANT]
> 本 Mod 是 **tModLoader 版在线音乐点播台（MP3 / 网易云）**，是 `MidiPlayer` / `MidiGmMod` 的姊妹工程——它专注"在线点歌听歌"，不含任何本地 MIDI 合成。
> 需要 **tModLoader 1.4.4.9**（Terraria 1.4.4.x，.NET 8）。音频解码走本机 `MediaPlayer`，无需额外依赖。
## 功能概述
- **网易云在线点歌**：在游戏内搜索歌曲、点歌进队列、顺序播放，无需退出游戏
- **扫码登录（可选）**：登录网易云后可播放 VIP / 会员曲与更高音质；也支持手动粘贴浏览器 Cookie 兜底（都保存在本地）
- **歌词显示**：独立歌词窗口，实时跟播滚动 + 当前行高亮，超长行自动换行，可切换显示翻译（跟随当前播放曲目）
- **点唱面板 UI（HEROsMod 观感）**：右下角常驻「点歌」入口，搜索 / 队列双页签，进度条与音量滑杆，只读播放信息，窗口可拖动；搜索框支持系统中文输入法
- **网易云登录态本地保存**：登录后 Cookie 永久落盘，下次进服自动恢复，不会丢账号
- **多人共享队列**：服务器权威裁决与广播，网络只传控制 / 快照，各客户端本地下载播放；服务器集中取流后广播 CDN 直链，客户端直接落地
- **稳健的播放流转**：下载与切歌带多重超时兜底（取流看门狗 / 服务器集中取流超时 / 下载体硬超时），避免队列永久静音；下载先写 `.part` 再原子改名，杜绝半截文件被当完整缓存
- **自动顺延**：上一首播完后立即切下一首，并后台预取队列下一首到本地缓存，切歌几乎无卡顿
## 使用说明
- 进入世界后，屏幕**右下角**会自动出现常驻「点歌」按钮，点击呼出点唱面板
- 点唱面板：
  - **搜索**页签：输入关键词（支持中文，系统输入法）→ 回车或点「搜索」；点击某行加进队列，右键直接播放
  - **队列**页签：显示待播队列与点歌人；**在某一行的左右键上连续右键两次**可把该首移出队列
  - 「歌词」按钮：打开 / 关闭歌词窗口；歌词窗口内「翻译」按钮切换译文显示
  - 「网易云」源按钮：**右键**弹出登录窗（扫码登录 / 退出登录 / 用记事本打开登录态文件 / 导入 Cookie）
  - 底部：停止播放并顺延下一首、音量滑杆、只读进度条
- 播放为自动顺序队列；如需打断当前播放，右键搜索结果某行即可强播
> [!NOTE]
>
> - 本 Mod **无命令入口**，所有操作都在面板内完成。
>
> - 权限为设计值：点歌 / 排队 / 强播全员可用。tML 环境暂无 OpTeam 判定，全局控制暂放行，后续版本将按 tML 命令权限收紧。
## 文件结构
- **音频缓存**：首次取流自动创建于 `<Terraria存档>/NeteaseCache/`，以 `net_<id>.mp3` 缓存，重播同曲秒开
- **登录态文件**：`<Terraria存档>/NeteaseCache/netease_login.txt`（含 `MUSIC_U=...` 的 Cookie 串，等同账号，请勿外传）
```
Terraria/
└── tModLoader/
    └── NeteaseCache/          # 播放缓存 + 登录态文件
```
## 源码结构
```
AllMusicMod/
├── AllMusicMod.cs             # Mod 入口：皮肤加载 + 收包转发
├── AllMusicSystem.cs          # 每帧推进协调器 + 打开 UI 时锁滚轮
├── AllMusicPlayer.cs          # 打开点唱 UI 时锁定玩家移动/操作
├── Core/
│   ├── AllMusicCoordinator.cs # 服务器权威：队列裁决、取流、切歌、广播
│   ├── TrackRef.cs            # 统一曲目引用（本地/网易云 Key 往返）
│   ├── Permission.cs          # 操作权限（tML 暂全放行）
│   └── AllMusicPlayback.cs    # 播放状态标记（循环开关，预留）
├── Net/
│   ├── AllMusicMessageType.cs # 自定义 ModPacket 协议枚举
│   └── AllMusicNetService.cs  # 网络收发、快照展示状态、加入对齐
├── Netease/
│   ├── NeteaseApi.cs          # 搜索 / eapi 扫码登录 / 登录态取流 / 歌词 / 下载
│   ├── NeteaseEapi.cs         # 官方 eapi 参数加密
│   ├── NeteaseLyric.cs        # LRC 歌词解析（lrc + 译文，二分定位当前行）
│   ├── NeteaseMp3Player.cs    # MP3 本地播放状态机（MediaPlayer + 相位机）
│   ├── NeteaseSession.cs      # 登录态 Cookie / 昵称本地保存
│   ├── NeteaseSong.cs         # 搜索结果单曲模型
│   └── MusicSources.cs        # 搜索 / 取流分发
├── UI/                        # 点唱面板、歌词窗口、UIKit 宿主
├── UIKit/                     # 移植自 HEROsMod (GPLv3) 的 UIKit 控件库
├── Images/UIKit/              # HEROsMod 皮肤位图（随包分发）
├── build.txt                  # 模组元信息（名称 / 版本）
└── scripts/                   # 构建脚本
```
## 联网说明与隐私
- 本 Mod 仅与以下网易云官方 / 镜像接口交互：`music.163.com` 的 eapi 登录、播放地址、歌词接口，以及搜索镜像服务
- **登录态 Cookie 只保存在你本机的 `<存档>/NeteaseCache/netease_login.txt`，绝不上报、不写入日志**。请勿把该文件发给他人，它等同于你的网易云账号
## 许可
- 本 Mod 的 UI 控件库与皮肤资源移植自 [HEROsMod](https://github.com/JavidPack/HEROsMod)，以 **GPLv3** 随仓库发布（见 `UIKit/` 各文件头与源码内声明）
- 其余代码：见仓库 `LICENSE`
## 版本记录
### v0.1.0
- 首版：网易云在线点歌（搜索 / 队列 / 顺序播放 / 右键强播）
- 网易云扫码登录 + Cookie 兜底，登录态本地持久化
- 独立歌词窗口：跟播滚动、当前行高亮、自动换行、翻译开关
- 服务器权威多人共享队列，服务器集中取流广播 CDN 直链
- 多重超时兜底 + `.part` 原子改名 + 队列预取，保证切歌流畅不卡静音
## 反馈
- 优先发 issue -> <https://github.com/star620/AllMusicMod/issues>