# Alice in Cradle 联动（alice_cradle）

依赖：无（仅 Python 标准库）。

游戏侧使用随附的 **BepInEx 模组**（`AliceInCradleLink`，本模块 `mods/` 目录内）读取 HP / MP / EP 等数值并上报；模组只做数据采集，不含强度换算——换算全部由模块的映射表完成：

* `POST /data`：MOD 周期上报命名通道（`HP`、`MP`、`EP`、`Hurt`、`Heal`、`MpLost`、`MpGain`、`Orgasm`、`Orgasming` 等），可在「联动」页把它们映射到任意核心参数或头像参数（表达式 `{HP}/{HPmax}*200` 这类四则运算）；
* `GET /data`：返回输出映射表求值结果（设备状态回传游戏显示），字段名可自由改名；
* 默认服务地址 `127.0.0.1:8920`，在联动页模块卡片内配置。

## 游戏模组安装

模块页提供**一键安装**：

1. 确认游戏已安装 BepInEx（未安装时模块会提示）。
2. 打开「模块」页，在「Alice in Cradle 联动」卡片的**游戏模组**行填入游戏根目录（含 BepInEx 的那一层），或点「自动扫描」搜索。
3. 点安装，模组会被释放到 `BepInEx/plugins/AliceInCradleLink/`。路径会被记住，之后可一键安装。
4. 启动游戏，回到「联动」页把模组上报的通道映射到核心参数或头像参数。

目标目录**缺 BepInEx 时会自动安装**内置的 BepInEx 5 发行包再释放模组——只要选中的是游戏根目录（含游戏主程序 exe）即可。BepInEx 发行包随 DGStudio 主程序分发（`_vendor/`）。

## 安装

在 DGStudio「模块」页的在线列表中获取本模块，安装时自动读取本仓库
`requirements.txt` 并 pip 补装依赖，卸载 / 更新即热重载生效。
也可手动把本仓库内容整个放入应用目录 `modules/<模块 id>/`。
