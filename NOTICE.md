# 来源与许可说明

本仓库包含两个来源的代码，许可不同，分别说明如下。

## 1. PotatoVN 宿主源码（`PotatoVN/`）

本仓库的 `PotatoVN/` 目录是 [GoldenPotato137/PotatoVN](https://github.com/GoldenPotato137/PotatoVN)
的源码副本，版权归 PotatoVN 及其贡献者所有，遵循 **Apache License 2.0**。

- 原始许可证：[PotatoVN/LICENSE](PotatoVN/LICENSE)
- 上游项目：https://github.com/GoldenPotato137/PotatoVN
- 副本对应的版本：`1.10.2.0`（见 `PotatoVN/GalgameManager/Package.appxmanifest`）

### 为什么包含这份代码

插件项目通过 `ProjectReference` 引用宿主提供的插件接口
（`PotatoVN/GalgameManager.WinApp.Base`），编译期需要该项目的源码与程序集。
该引用标记为 `Private=false`，插件发布包中**不会**包含宿主程序集。

### 对本副本的改动

- 未修改任何宿主源文件。
- 副本中可能缺少上游 `dev` 分支在此之后的提交；如需最新版本，请以
  上游仓库为准。
- 构建产物（`bin/`、`obj/`）在提交前已排除。

### 分发要求

Apache-2.0 要求再分发时保留许可证与版权声明。本仓库已保留
`PotatoVN/LICENSE`，任何基于 `PotatoVN/` 目录的再分发同样需要保留该文件
并注明来源。

## 2. 音乐盒插件（`PotatoVN.MusicBox*`、`tools/`）

插件本体、核心库与测试台为本项目原创，以 **MIT License** 发布，
完整文本见 [LICENSE](LICENSE)。

### 第三方依赖

| 依赖 | 许可 | 用途 |
| --- | --- | --- |
| [NAudio](https://github.com/naudio/NAudio) | MIT | MP3 / WAV 解码，WinMM 输出 |
| [NVorbis](https://github.com/ioctlLR/NVorbis) | MIT | Ogg Vorbis 纯托管解码 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | MVVM 基础设施 |

`NAudio.Core`、`NAudio.WinMM`、`NAudio.Wasapi` 与 `NVorbis` 以 DLL 形式随插件包分发，
其许可证文本可在各自仓库中查阅。

## 3. 归档格式的实现来源

归档解析的格式细节参考了以下公开实现，均为独立重写，未复制其代码：

- [GARbro](https://github.com/morkt/GARbro)（MIT）— XP3、LINK、LAC、KIF、AFA、
  NEKOPACK、Softpal PAC、Entis NOA 的格式说明
- [akaibu](https://github.com/Forlos/akaibu)（MIT）— Tactics arc 的格式说明与已知密钥表
- [Crass / ESCARC](https://github.com/shangjiaxuan/Crass-source)（GPL，仅参考格式文档）
  — Escu:de ESC-ARC2 的结构说明
- [GalTransl-GalGameReverse-Skill](https://github.com/GalTransl/GalTransl-GalGameReverse-Skill)
  （GPL-3.0，仅参考格式文档）— CatSystem2 KIF 的加密流程说明

其中 Crass 与 GalTransl 的许可为 GPL，本项目只参考其**格式文档**而非代码，
因此不影响本项目的 MIT 许可。若后续直接移植其代码，需要相应调整许可。

## 4. 关于游戏资源

本仓库不包含任何游戏本体、游戏资源或解包产物。插件的用途是让用户播放
**自己已安装的游戏**中的音乐，所有解析都在用户本机进行。
