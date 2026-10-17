# Chrome 下载接管扩展

## 目录

| 文件 | 说明 |
| --- | --- |
| `src\` | 扩展源码（`manifest.json` + `background.js`），改这里 |
| `DownloaderHandoff.crx` | 签名后的分发包，绿色版随程序分发，供 Chrome 外部安装 |

构建时会把 crx 拷到 `Downloader.exe` 同目录 —— 程序只认这个位置的 crx，Chrome 也从这里装。

## 为什么仓库里没有签名私钥

签名私钥 `DownloaderHandoff.pem` 由上游维护者保管，**刻意不入库**：私钥等于"用同一个扩展 ID 签名"的能力，
谁拿到它就能签出同 ID 的恶意 crx 去覆盖用户已装的版本，所以永不开源。

但**没有它，本扩展照常可用**——扩展 ID 由密钥对里的*公钥*决定，而公钥就明文写在 `manifest.json` 的 `key`
字段里。日常两条路都碰不到私钥：

- 开发者模式「加载已解压」指向 `extension\src\`：ID 由 manifest 的 `key` 固定；
- 外部安装：直接用仓库里已签好的 `DownloaderHandoff.crx`。

只有「改了源码并重新签名成 crx」才需要私钥：上游维护者用原 pem（ID 不变）；fork 者应生成自己的私钥
（`tools\pack-extension.ps1 -NewKey`），得到自己的新 ID，并按下面「三处必须对齐的 ID」同步改配套位置。

## 三处必须对齐的 ID

扩展 ID 由 `DownloaderHandoff.pem` 这把私钥唯一决定，当前是：

```
mhdhfggpookgphfnepgemppfhjdpdhhc
```

它同时作用在三个地方，任何一个对不上链路就断：

1. `.crx` 的签名（由 pem 决定）
2. `manifest.json` 里的 `key`（由同一把私钥导出，保证「解压加载」和「安装 crx」拿到同一个 ID）
3. `com.downloader.host.json` 的 `allowed_origins` 与外部安装时的注册表键名

所以：**换私钥 = 换 ID = 三处全部要重来**。反过来，只要不动 pem，ID 永远不变。

## 重新打包

改完 `src\` 里任何文件后：

```powershell
.\tools\pack-extension.ps1            # 有上游私钥时（原仓库维护者）
.\tools\pack-extension.ps1 -NewKey   # 没有私钥时，生成你自己的（ID 会变）
```

脚本调用 Chrome 自带的打包器。Chrome 的约定是把 `<目录名>.crx` 写在被打包目录的上一级，
所以源码必须放在 `src\` 这一层 —— 否则产物会被套进 zip 里再打一次。

**更新扩展必须把 `manifest.json` 的 `version` 调大**：外部安装靠注册表里的 version 判断要不要重装，
版本号不变的话 Chrome 不会替换已装的旧版。

## 装机与拆机

日常不需要手动操作：程序设置页的「启用 Chrome 下载接管」开关负责写/撤以下三处（只写 HKCU，不需要管理员权限）。

| 项 | 位置 |
| --- | --- |
| 宿主清单 | `<exe 目录>\com.downloader.host.json` |
| 宿主注册 | `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.downloader.host` |
| 外部扩展登记 | `HKCU\Software\Google\Chrome\Extensions\<扩展 ID>`（path + version） |
| Chrome 策略白名单 | `HKCU\Software\Policies\Google\Chrome\ExtensionInstallAllowlist` |

最后一项是必需的：Chrome 128 之后只允许商店扩展，非商店扩展必须靠策略白名单放行，
否则扩展开关是灰的、点不开。代价是开启期间 Chrome 会显示「由贵单位管理」，撤回后消失。

开关状态不写进 `settings.json`，每次从注册表与磁盘实测 —— 所以删掉 `data\` 也照样能认定当前状态、
也照样能撤干净。撤走外部扩展登记后 **Chrome 会自己卸载该扩展**（这是官方规定的卸载方式）。

装机没有独立脚本：唯一入口就是程序设置页那个开关，避免同一套逻辑出现两份实现。
排障看设置页的状态行（会列出缺哪几项）与 `data\logs\trace.log` 里 `integration:` 开头的行。

## 已知限制

- Chrome 会忽略命令行传来的 `chrome://` 内部地址，所以程序无法把浏览器直接导航到扩展页
- Native Messaging / 外部扩展 / 策略白名单都不是 Google 承诺稳定的公开契约，Chrome 改策略时可能静默失效
- 根治办法是上架 Chrome 应用商店（Unlisted），那是 IDM 走的路
