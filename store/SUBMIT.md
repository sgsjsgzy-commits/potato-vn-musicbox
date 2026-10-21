# 音乐盒 · 上架投稿材料

打包后到 https://plugin.potatovn.net 完成 4 步即可上架。

## 1. 准备产物

在仓库根目录:
```powershell
dotnet build PotatoVN.MusicBox/PotatoVN.App.PluginBase.csproj -c Release
```
产物:`PotatoVN.MusicBox/artifacts/plugin.pvnplugin.zip`

## 2. 到平台申请

1. 登录 https://plugin.potatovn.net(与 PotatoVN 客户端同一个账号)
2. 右上角 **创建插件** → 填:
   - 名称:`音乐盒`
   - 简述:不启动游戏,直接浏览并播放 galgame 安装目录里的 BGM
3. 提交,等 3 天内审核

## 3. 配置 plugin-info

审核过后 **我的插件 → 音乐盒 → 版本管理**:

- 图标:`store/icon.png`(512×512,在仓库 `store/` 目录)
- `plugin-info.json` 字段填本目录同名文件里的内容
  - `guid` 必须是 `43537402-ca22-49ac-a82d-b23af37b65d8`(与代码 `Plugin.cs` 中 `PluginInfo.Id` 一致)
  - `types` 目前填 `40`(界面优化 32 | 功能优化 8)。也可以用平台的[类型计算器](https://potatovn.net/development/client-plugin/type-calculator.html)
- 插件详细描述(detailed)可以直接用 `plugin-info.json` 里的 `description_detailed` 字段

## 4. 上传 plugin 包

**版本管理 → 新建版本** → 选本仓库构建出的 `plugin.pvnplugin.zip`。

- 首次上传需要审核,通过后立刻在商店列表里能看到
- 之后再发版本不用审核
- 版本号递增即可,首个版本任意(建议 `1.0.0`)

## 完成后确认

- [ ] 商店列表 + 详情页 icon 显示
- [ ] 在 PotatoVN 设置里清空插件目录,从商店搜索"音乐盒"能装上
- [ ] 新装用户首次进入能看到游戏列表、能扫出 BGM