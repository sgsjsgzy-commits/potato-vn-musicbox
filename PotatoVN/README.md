<p align="center">
<img src="GalgameManager/Assets/Pictures/Potato.png" width="80px"/>
</p>

<div align="center">
    
# PotatoVN
  
[简体中文](https://github.com/GoldenPotato137/PotatoVN/blob/dev/README.md) 
|
[English](https://github.com/GoldenPotato137/PotatoVN/tree/dev/docs/README_EN.md)


![123](https://img.shields.io/endpoint?color=blue&label=Microsoft%20Store%20Rating&url=https%3A%2F%2Fmicrosoft-store-badge.fly.dev%2Fapi%2Frating%3FstoreId%3D9P9CBKD5HR3W)
[![FOSSA Status](https://app.fossa.com/api/projects/git%2Bgithub.com%2FGoldenPotato137%2FPotatoVN.svg?type=shield)](https://app.fossa.com/projects/git%2Bgithub.com%2FGoldenPotato137%2FPotatoVN?ref=badge_shield)
[![Telegram](https://img.shields.io/badge/Telegram%E5%90%B9%E6%B0%B4%E7%BE%A4-Join-green)](https://t.me/potato_vn)

一个VisualNovel管理工具，旨在为galgame屯屯鼠们提供一个方便的游戏管理平台。
</div>

## 功能
* 自动检索文件夹内的游戏
* 自动从多个数据库中获取游戏信息 （目前支持[bangumi](https://bgm.tv/)、[visual novel database](https://vndb.org/)），并从账户中同步游玩状态
* 将游戏存档与云端同步 (此功能需要电脑上具有任意一款同步软件来同步存档文件夹，如OneDrive、NextCloud、坚果云、百度云同步盘等)
* 统计游戏游玩时间
* 从压缩包中自动解压游戏，并自动识别且添加到游戏库中

## 安装
> **对于windows10用户:** windows10用户需要额外安装[Segoe Fluent 图标字体](https://aka.ms/SegoeFluentIcons)

<p align="center">
    <a href="https://apps.microsoft.com/detail/9p9cbkd5hr3w?cid=github&mode=full">
        <img src="https://get.microsoft.com/images/zh-cn%20dark.svg" alt="Microsoft Store"/>
    </a>
    &nbsp; <!-- 添加一个空格，防止两个按钮紧贴着 -->
    <a href="https://github.com/GoldenPotato137/PotatoVN/releases">
        <img src="docs/DownloadBadge.svg" height="43" alt="Sideload Download"/>
    </a>
</p>

## 翻译

PotatoVN使用crowdin来进行本地化，欢迎在[crowdin](https://crowdin.com/project/potatovn)将PotatoVN带到您的语言当中。

## 开发者相关
本程序是一个WinUI3的应用，要编译本程序，请参考[微软文档](https://learn.microsoft.com/zh-cn/windows/apps/windows-app-sdk/set-up-your-development-environment?tabs=cs-vs-community%2Ccpp-vs-community%2Cvs-2022-17-1-a%2Cvs-2022-17-1-b)
安装相应的开发环境。

本程序使用MVVM架构，基于[TemplateStudio](https://github.com/microsoft/TemplateStudio/tree/main/docs/WinUI)生成的框架开发。

欢迎各位感兴趣的dalao在[这里](https://github.com/GoldenPotato137/PotatoVN/discussions/categories/%E5%BC%80%E5%8F%91%E7%8A%B6%E6%80%81)查看目前急需解决的问题，PotatoVN永远欢迎各位的加入~

## 致谢
感谢以下组织对本项目的支持❤️

| 赞助方 | 支持内容 |
|----------------------------------------------------------------------------------------------------------------------|-------------------------------------------------|
| <p align="center"><a href="https://signpath.io/"><img src="https://github.com/user-attachments/assets/2de96f21-4e01-4d2b-8d22-72aae5784906" alt="signpath logo" width="160"></a></p> | 为PotatoVN提供了免费的侧载版安装包签名，极大方便了不方便使用微软商店的用户的下载与安装 |
| <p align="center"><a href="https://www.repoflow.io/"><img src="https://github.com/user-attachments/assets/f755e596-bba1-441d-bf61-5f073c95aa61" alt="repoflow logo" width="160px"></a></p> | [RepoFlow](https://www.repoflow.io)，一个支持自托管的包管理平台，为我们的插件管理平台提供了正版授权，极大方便了用户下载插件与开发者上传自己的插件 |
| <a href="https://www.jetbrains.com/"><img src="https://resources.jetbrains.com/storage/products/company/brand/logos/jetbrains.png" alt="JetBrains logo." width="160px"></a> | 提供了免费正版授权的强大IDE，其中的.Net开发工具`Rider`极大方便了PotatoVN的开发 |

## Code signing policy
* Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).
* Committerss: [PotatoVN Committers](https://github.com/GoldenPotato137/PotatoVN/graphs/contributors)
* Approver: [GoldenPotato137](https://github.com/GoldenPotato137)
* Privacy policy: [Privacy policy](https://potatovn.net/usage/how-to-use/privacy-policy.html)
