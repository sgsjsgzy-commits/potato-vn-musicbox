# 音乐盒 · PotatoVN MusicBox

给 [PotatoVN](https://potatovn.net/) 用的插件：**不启动游戏，直接播放 galgame 安装目录里的音乐**，就像 Steam 上单卖的原声包。

插件在侧边栏注册一个独立的「音乐盒」入口，点进去可以选游戏、浏览曲目、直接播放。扫描结果会缓存下来，第二次打开是秒开的。

## 功能

**曲库**
- 从游戏安装目录中提取音乐，支持松散音频文件与压缩归档
- 按游戏分组，左侧是游戏列表（封面 / 开发商 / 曲目数），右侧是曲目表
- 自动分类：背景音乐 / 语音 / 音效，可筛选（语音与音效默认不收录，可在设置里打开）
- 曲目搜索（标题、所在文件夹、归档名）
- 显示时长、容器格式、来源归档

**播放**
- 播放 / 暂停、上一首 / 下一首
- 播放全部、随机播放全部
- 顺序播放 / 列表循环 / 单曲循环
- 随机播放（Fisher–Yates，切歌时保留当前曲目）
- 拖动进度条定位、音量与静音
- 播放队列查看
- **离开页面音乐继续播放**，回到页面自动接上
- 记住上次播放的曲目与进度

**扫描**
- 每个游戏独立缓存，安装在哪个盘就把缓存放在同一块盘（默认 `游戏库\.musicbox`），不占系统盘
- 可在插件设置中改缓存位置、开关语音 / 音效、开关时长探测

## 支持的归档格式

| 格式 | 引擎 / 来源 | 说明 |
| --- | --- | --- |
| XP3 | KiriKiri | 支持原始与 zlib 压缩段；KrkrZ Hxv4 保护索引会明确提示 |
| LINK v3–v6 | KaGuYa | 含 BMR (LZSS) 解压 |
| YPF | YuRis | 名称长度混淆与 XOR 键还原 |
| RPA 2.0 / 3.0 / 3.2 / 4.0 | Ren'Py | zlib + Python pickle 索引 |
| PackFile / ARC20 | BGI / Ethornell / BURIKO | 含 `bw  ` 音频容器 |
| NSA | NScripter | 仅支持未压缩条目 |
| NEKOPACK4A | NekoSDK | 名称异或、zlib + 掩码头 |
| LAC / PAK | Leaf（WHITE ALBUM 2 等） | LZSS，含 PAK 条目尺寸头 |
| KIF | CatSystem2 / Frontwing | 含 `__key__.dat` 加密索引：Blowfish + MT，音频可解 |
| AFA / AlicArch | AliceSoft System 4 | zlib 索引，`AFF\0` 载荷去掩码 |
| PAC | Softpal / Amuse Craft | 0x804 索引，Ogg 直读 |
| BIN（FVP v2） | Favorite（樱花，萌放等） | 12 字节索引 + UTF-16 名称池 |
| BIN（ESC-ARC2） | Escu:de | 滚动密钥异或索引，`acp\0` LZW |
| BIN | 无签名 UTF-16 索引（3564 等） | 结构化校验后接受 |
| OWP | あねもい 系 | 整文件单字节异或的 Ogg |
| NOA | Entis GLS（悠之空等） | ERISA 目录树；`.mio` 为私有编码 |

松散文件（`.ogg` `.mp3` `.wav` `.flac` `.m4a` 等）始终支持。

## 音频解码

不依赖系统编解码器：

- **Ogg Vorbis**：由 [NVorbis](https://github.com/ioctlLR/NVorbis) 以纯托管代码解码。Windows 自带的 Media Foundation 没有 Vorbis 解码器（会报 `0xC00D36C4 SourceNotSupported`），这是必须自带解码器的原因。
- **MP3 / WAV**：NAudio 的帧读取器。
- **FLAC / M4A / AAC / WMA**：Media Foundation（Windows 10 及以上原生支持）。
- 音量在软件层施加，不同输出设备表现一致。

音频直接从归档流中按需解码，**不会把曲子先复制到临时目录**，因此打开一个几百 MB 的 XP3 也只需读一次索引。

## 安装

1. 从 Releases 下载 `plugin.pvnplugin.zip`
2. 在 PotatoVN 的插件页面选择「从文件安装」

## 开发

```
PotatoVN.MusicBox/
├─ PotatoVN.MusicBox.Core/     归档解析、音频探测、扫描（纯 .NET，无 UI 依赖）
├─ PotatoVN.MusicBox/          WinUI 3 插件本体
├─ PotatoVN.MusicBox.Cli/      扫描测试台（scan / archive / play）
└─ tools/install.ps1           安装到本机 PotatoVN 供调试
```

```bash
dotnet build PotatoVN.MusicBox/PotatoVN.App.PluginBase.csproj -c Debug
powershell -ExecutionPolicy Bypass -File tools/install.ps1
```

发布包在 `Release` 构建时自动生成：`artifacts/plugin.pvnplugin.zip`。

测试台示例：

```bash
dotnet run --project PotatoVN.MusicBox.Cli -- scan "E:/gal/某个游戏"
dotnet run --project PotatoVN.MusicBox.Cli -- archive "E:/gal/某个游戏/bgm.ypf"
dotnet run --project PotatoVN.MusicBox.Cli -- play "E:/gal/某个游戏/bgm.ypf::bgm/bgm_01.ogg"
```

## 已知限制

- **KrkrZ Hxv4 保护索引的 XP3**：文件名表用 ChaCha20 加密、内容按条目走 span key，密钥只能从运行中的游戏内存 dump，因此静态解析不可行。插件会识别并提示，而不是笼统报错。
- **CatSystem2 加密 KIF 的文件名**：目录树中的名称是替换密码，需要从游戏 exe 里取口令才能还原。音频本身的 Blowfish 加密由归档种子即可解开，所以曲目仍可播放，只是标题退化为 `m00000001` 这样的序号。
- **Entis `.mio`**（悠之空等）：引擎私有音频编码，未实现解码，会提示共有多少曲目不可播放。
- **TACTICS_ARC_FILE**：仅内置四个已知标题的异或密钥；本库中「くれよんちゅーりっぷ」不在其中。
- 只保留文件哈希的归档（如部分 NekoPack 变体）无法还原文件名
- NScripter 的 NBZ（bzip2）压缩条目未支持
