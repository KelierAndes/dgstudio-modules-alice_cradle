# Alice in Cradle 联动（alice_cradle）

依赖：无（仅 Python 标准库）。**游戏端模组及其全部依赖随本仓库携带**：
模组源码在 `AliceInCradleLink/`，编译产物在 `modules/alice_cradle/mods/`，
BepInEx 5 发行包在 `modules/alice_cradle/vendor/`（自动安装 BepInEx 用），
编译引用程序集在 `_vendor/bepinex-core/`。

模组（`AliceInCradleLink`，BepInEx 5 插件）读取 HP / MP / EP 等数值并上报；只做数据采集，不含强度换算——换算全部由模块的映射表完成：

* `POST /data`：MOD 周期上报命名通道（`HP`、`MP`、`EP`、`Hurt`、`Heal`、`MpLost`、`MpGain`、`Orgasm`、`Orgasming` 等），可在「联动」页把它们映射到任意核心参数或头像参数（表达式 `{HP}/{HPmax}*200` 这类四则运算）；
* `GET /data`：返回输出映射表求值结果（设备状态回传游戏显示），字段名可自由改名；
* 默认服务地址 `127.0.0.1:8920`，在联动页模块卡片内配置。

## 游戏模组安装

模块页提供**一键安装**：

1. 打开「模块」页，在「Alice in Cradle 联动」卡片的**游戏模组**行填入游戏根目录（含 BepInEx 的那一层），或点「自动扫描」搜索。
2. 点安装，模组会被释放到 `BepInEx/plugins/AliceInCradleLink/`。路径会被记住，之后可一键安装。
3. 启动游戏，回到「联动」页把模组上报的通道映射到核心参数或头像参数。

目标目录**缺 BepInEx 时自动安装模块携带的 BepInEx 5 发行包**再释放模组——只要选中的是游戏根目录（含游戏主程序 exe）即可，全程无需联网。该能力同时以通用接口暴露给模块代码（`ctx.scan_game_roots()` / `ctx.install_game_mod(root)`，见 DGStudio 模块开发文档 §2.3）。

## 游戏端模组（源码与编译）

* 源码：`AliceInCradleLink/`（BepInEx 5 / netstandard2.1，引用游戏 `Managed` 目录与 `_vendor/bepinex-core`）；
* 编译：`dotnet build -t:Deploy`——dll 同步到游戏 `BepInEx/plugins/AliceInCradleLink/`（存在时）与本仓库 `modules/alice_cradle/mods/`；游戏路径可用 `-p:GameDir="..."` 覆盖；
* 修改模组后重新编译提交，用户更新模块即获得新版（`mods/` 载荷随模块分发）。

## 安装

在 DGStudio「模块」页的在线列表中获取本模块，安装时优先合并仓库自带的
依赖，卸载 / 更新即热重载生效。也可手动把本仓库 `modules/<模块 id>/`
文件夹整个放入应用目录的 `modules/` 下。
