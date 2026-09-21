<p align="center">
  <img src="docs/hero.png" width="720" alt="CodexOrbit">
</p>

# CodexOrbit

**中文 · [English](README_EN.md)**

[![stars](https://img.shields.io/github/stars/Reality-JH/CodexOrbit?style=flat-square)](../../stargazers)
[![release](https://img.shields.io/github/v/release/Reality-JH/CodexOrbit?style=flat-square)](../../releases)
[![downloads](https://img.shields.io/github/downloads/Reality-JH/CodexOrbit/total?style=flat-square)](../../releases)
[![issues](https://img.shields.io/github/issues/Reality-JH/CodexOrbit?style=flat-square)](../../issues)
[![license](https://img.shields.io/badge/license-CC_BY--NC--SA-8b7cf6?style=flat-square)](LICENSE)
![platform](https://img.shields.io/badge/platform-Windows%2010%2F11-0078d4?style=flat-square)
![runtime](https://img.shields.io/badge/.NET_Framework_4.x-zero_deps-512bd4?style=flat-square)
![size](https://img.shields.io/badge/exe-%7E58_KB-2ea043?style=flat-square)
![admin](https://img.shields.io/badge/admin-not_required-d29922?style=flat-square)

## 目录

- [它到底干嘛](#它到底干嘛)
- [功能总览](#功能总览)
- [工作原理](#工作原理)
- [控制台与菜单](#控制台与菜单)
- [文件与数据位置](#文件与数据位置)
- [FAQ](#faq)
- [更新日志](#更新日志)
- [安全与隐私](#安全与隐私)
- [为什么断流会少](#为什么断流会少)
- [你见过的报错，到底怎么回事](#你见过的报错到底怎么回事)
- [这不是你一个人的问题](#这不是你一个人的问题)
- [安装](#安装)
- [懒得动手？让 AI 替你装](#懒得动手让-ai-替你装)
- [自己编译](#自己编译)
- [它如何和 ccodex-rotate 通信](#它如何和-ccodex-rotate-通信)
- [给仓库贡献者](#给仓库贡献者)
- [诚实边界](#诚实边界)
- [交流](#交流)

> **你每月给 OpenAI 打 $200，买到的却是：重连五次、429 转圈、overloaded 降智、502 坏网关、断流腰斩、"stream closed before response.completed"。**
>
> 钱付了，货没到。不是你账号的问题，是通往上游那条路烂了。

<p align="center">
  <img src="docs/meme-before-after.png" width="680" alt="凌晨三点被 429 淹没 vs 靠回去看轨道环">
</p>

**CodexOrbit** 是给 [ccodex-rotate](https://github.com/446599/CCODEX-ROTATE) 做的 Windows 托盘伴侣：把好出口找出来钉住、烂节点踢进冷却、`X-Codex-Turn-State` 采到自动注入。**让 Pro 会员花出去的钱，换回 Pro 会员该有的体验。**

<p align="center">
  <img src="docs/screenshot.png" width="300" alt="CodexOrbit status card">
</p>

## 它到底干嘛

零黑窗、零 CMD、零浏览器。后台静默跑，右下角一个小图标，**自带原生控制台**，网页面板彻底不需要了。

<p align="center">
  <img src="docs/console.png" width="430" alt="CodexOrbit console">
</p>

- **左键出状态卡**：当前节点、成功率、上次延迟、292 倒计时 + **最近 24 次请求的延迟曲线**
- **双击开控制台**：节点全列表（✓ 当前锁定 · 类型 · 实测延迟，单击即固定）、已采 292 凭据、添加订阅/节点、重启服务，一个原生窗口管全部
- **右键出操作菜单**：固定节点（✓ 当前锁定 · 带实测延迟）/ 凭据 292（已采 state 的模型·命中数·来源节点）/ **添加来源**（订阅链接、节点分享链接直接粘贴）/ 换节点 / 立即采 292 / 恢复自动（已自动时置灰）/ 重启服务 / **语言切换**（中文/English，记住选择）
- **掉线会冒泡**：服务挂掉→自动拉起、节点切换，都有气球通知，不用盯
- **有记忆**：`CodexOrbit.memory.json` 记住上次固定的节点和最后有效的 292 来源。重开后池空自动补采、固定自动恢复；服务起落/换节点/池变化/手动操作全写 `CodexOrbit.log`（菜单里可直接打开）
- **自启自拉**：启动即拉起 `orbit-core serve`（隐藏窗口）；进程意外死亡 4 秒内自动复活
- **图标会变色**：紫=正常 · 黄=错误偏多 · 红=离线 · 灰=启动中
- **退得干净**：退出 = 停服务 + 清孤儿内核 + `restore` 还原 `config.toml`，不留指向死代理的残局

## 功能总览

### 节点与路由

| 功能 | 说明 |
|---|---|
| 健康巡检 | 内核每 45s 探测全部节点；死节点进冷却，请求不会命中它 |
| 一键固定 | 托盘菜单或控制台单击节点即固定；上游可达性实测延迟随列表展示 |
| 自动选路 | `恢复自动` 解除固定交还 url-test；已是自动时该项置灰 |
| 切换节点 | 自动模式下强制轮换到下一个健康节点；每次切换写日志 |
| 可达上游标记 | 节点池区分"健康"与"实测可达上游"，能连上 ≠ 扛得住流 |

### 292 凭据池

| 功能 | 说明 |
|---|---|
| 自动采集 | 缺 state 自动补采；采到后按成功周期（30 分钟）自动续期 |
| 失败重试 | 采集失败 5 分钟后自动重试，不需要人工盯 |
| 凭据注入 | 采到的 `X-Codex-Turn-State` 自动注入请求；`inject_node_affinity` 绑死产出节点 |
| 池状态可见 | 控制台与状态卡实时显示在库条数、采集中标记、下次采集时间 |
| 凭据不落明文界面 | 凭据值永不显示在界面或日志，只显示模型、命中数、来源节点 |

### 守护与自愈

| 功能 | 说明 |
|---|---|
| 服务拉起 | 启动即拉起 `orbit-core serve`（隐藏窗口），不弹任何黑窗 |
| 4 秒自愈 | 内核进程意外死亡，巡检发现后先清孤儿 mihomo 再拉起 |
| 崩溃恢复 | 托盘自身退出后，开机自启快捷方式在下次登录时复活整套 |
| 失败引导 | 请求连续失败（403/429/5xx ×3）→ 气泡一键修复：换节点 + 重采 292 双管齐下；控制台/状态卡同步高亮对应按键 |
| 干净退出 | 退出 = 停服务 + 清孤儿内核 + `restore` 还原 `config.toml` |

### 记忆与日志

| 功能 | 说明 |
|---|---|
| 持久记忆 | `CodexOrbit.memory.json` 记录固定节点、最后有效 292 的模型与来源节点 |
| 重启续用 | 池空但记忆里有好凭据 → 自动补采；上次固定过 → 自动恢复固定 |
| 空池兜底 | 292 池空且引擎没在采 → 托盘每 150s 自动补采一脚，手动「采集」只是加速器 |
| 本地日志 | `CodexOrbit.log` 记录启动、拉起、掉线、换节点、池变化、手动操作；菜单可直接打开 |
| 请求记录 | 控制台实时滚动每条请求：状态码、**首字(TTFT)/总耗时**、命中节点、重试次数、292 注入标记、**实际上游给的模型**（与请求不一致时标出） |
| 防降智 | 上游偷换模型（如求 `gpt-6-astra` 实给 `gpt-5.6-luna`）时**直接拒绝该请求**（422），绝不吃降智回答；可在 `config.json` 设 `"strict_model": false` 放宽 |

## 工作原理

```text
┌──────────┐   HTTP    ┌─────────────┐   本地 API :17850   ┌─────────────┐
│ 托盘伴侣  │ ───────▶  │ orbit-core  │ ◀────────────────▶ │ CodexOrbit  │
│ (状态卡/  │  轮询 4s  │ (路由引擎)   │                     │  控制台      │
│  菜单)    │ ◀───────  │             │                     │             │
└──────────┘           └──────┬──────┘                     └─────────────┘
                              │ 内嵌
                       ┌──────▼──────┐    节点池 (vless/ss/trojan/hysteria2/tuic)
                       │   mihomo    │ ──────────────▶ 上游 https://chatgpt.com
                       │  混合:17890  │
                       │  控制:17891  │
                       └─────────────┘
```

- 托盘只发**本地回环 HTTP**，不直接碰节点配置；所有节点操作经由 `/api/*` 端点。
- 292 凭据池是**凭据指标**（在库条数、采集排期），节点池是**路由指标**（健康数、上游可达数），两者语义分开。
- `自动` 只恢复 url-test 选路；采集由独立调度器按成功 30 分钟 / 失败 5 分钟节奏自跑。

## 控制台与菜单

控制台六个按键：

| 按键 | 行为 |
|---|---|
| 切换 | 自动模式下强制轮换到下一个健康节点 |
| 采集 | 立即触发一次 292 采集（后台执行，不等排期） |
| 自动 | 解除固定恢复 url-test；已是自动时置灰 |
| 重启 | 重启 orbit-core 服务（先清孤儿 mihomo） |
| +订阅 / +节点 | 粘贴订阅链接或 `vless:// ss:// trojan:// hysteria2:// tuic://` 分享链接 |

控制台三块列表：节点池（单击固定）、292 凭据池（模型·长度·命中·来源节点）、请求记录（时间·方法·路径·状态码·首字/总耗时·重试·注入·节点），4 秒自动刷新。

托盘菜单在按键之外还提供：固定节点（带实测延迟）、292 凭据池明细（模型·命中数·来源节点）、添加来源、清空全部来源、语言切换、打开日志、退出并还原 Codex（带确认弹窗）。

## 文件与数据位置

| 路径 | 内容 |
|---|---|
| `CodexOrbit.exe` 旁 | `orbit-core.exe`（上游内核，发布包附带） |
| `~/.ccodex-rotate/config.json` | 订阅、节点、采集调度配置（本工具不改） |
| `~/.ccodex-rotate/mihomo/` | mihomo 运行时目录 |
| `CodexOrbit.memory.json` | 持久记忆：固定节点、最后有效 292 模型与来源节点 |
| `CodexOrbit.log` | 本地事件日志，超 1MB 自动截半 |

## FAQ

**点「自动」会触发采集吗？** 不会。`自动` 只调 `/api/reset` 解除固定恢复 url-test 选路。采集是独立调度：缺 state 补采、成功 30 分钟后续期、失败 5 分钟重试、滞留 1 小时作废。

**崩了会怎样？** 分三层：orbit-core 挂 → 4 秒内清孤儿 mihomo 再拉起；托盘自己挂 → 服务继续跑，下次登录自启快捷方式复活托盘；重启电脑 → 登录即起全套。

**292 凭据池会自动换吗？** 会。成功周期 1800s 换一批、失败 300s 重试、滞留 3600s 作废；请求缺 state 时 `auto_collect` 随时补采，不等排期。

**需要管理员权限吗？** 不需要。不写注册表、不装服务、不改系统代理。

**支持哪些节点协议？** 订阅链接，以及 `vless://` `ss://` `trojan://` `hysteria2://` `tuic://` 分享链接。

**退出后我的 Codex 配置会怎样？** 「退出并还原 Codex」会停服务、清孤儿内核、用 `restore` 还原 `config.toml`——不留指向已停代理的残局。

## 更新日志

### v1.1.0

- 控制台新增**请求记录**：每条请求的状态码、首字(TTFT)/总耗时、命中节点、重试次数、292 注入标记（TTFT 需要随包的新版 orbit-core）
- 发布 zip 捆绑 orbit-core，解压即用，不再需要自备内核
- 292 池空池兜底：托盘自动补采，不用手点；采集进度实时显示（x/y）
- 控制台 4 秒自动刷新（之前只在打开时拉一次，开着就一直是旧数据）
- 连续失败主动引导：弹气泡给一键修复，控制台/状态卡高亮该点的按键
- 缺引擎/启动失败弹气泡指引；空节点池/空凭据池给操作提示；退出前确认弹窗

### v1.0.0

首个公开发布：原生控制台、292 凭据池分离展示、持久记忆、本地日志、崩溃自愈、节点固定与自动选路、双语界面。

## 安全与隐私

- 全部通信走 `127.0.0.1` 本地回环；无遥测、无外部上报。
- 292 凭据值**从不显示**在界面或日志里，只展示模型、命中数、来源节点。
- 不写注册表、不装系统服务、不改系统代理设置；不需要管理员权限。
- 订阅与节点配置沿用 `~/.ccodex-rotate/config.json`，本工具不改写它。

## 为什么断流会少

`stream closed before response.completed` 的元凶是**请求落到了烂节点/烂会话上**：一旦 SSE 开始流，中途断就没法无感救回。所以 CodexOrbit 这套配置的思路是**前置拦截**：

| 机制 | 干什么 |
|---|---|
| mihomo 内核健康检查 | 每 45s 探一遍节点，死的进冷却，请求根本轮不到它 |
| 292 自动采集+注入 | 请求缺 state 自动采、采到自动注、到期自动续，上游把你当健康会话 |
| `inject_node_affinity` | 292 绑死产出它的节点，不跨节点乱注 |
| `collect_on_start` | 启动先扫一轮，不等第一次请求踩坑 |

你不用管"采集模式还是自动模式"：**采集永远后台自动跑，你唯一的动作是点图标。**

## 你见过的报错，到底怎么回事

不止 429。这些全是实测抓到的：症状五花八门，根子基本就两类：**节点烂** 或 **缺 292 被降权**：

| 你看到的报错 | 实际发生了什么 |
|---|---|
| `429` / `exceeded retry limit` | 共享出口被风控；且 Codex 源码 `retry_429=false`，**根本没替你重试** |
| `overloaded` / 突然降智变笨 | 缺 292 turn-state，上游把你当匿名流量降权 |
| `502 Bad Gateway: Unknown error` | 命中的节点已死，实测 WS 节点入口直接回 `403 Forbidden` |
| `failed to dial WebSocket: 403` | 节点被 CDN 入口拒了，**不是 OpenAI 拒你** |
| `stream closed before response.completed` | 流中途节点断/被掐，或代理总时限到点掐断 |
| 重连 ×5 | 死节点没被剔除，请求反复命中同一具尸体 |
| 转圈不回 / 首 token 超时 | 节点"能连上"但"扛不住流"，延迟测试量不出这个 |

**一句话：报错文案千奇百怪，病根全是路。** CodexOrbit 干的就是把烂路拦在请求到达之前。

## 这不是你一个人的问题

不用信我，信 openai/codex 官方 issue 区和中文社区的真实控诉。

**最狠的一锤在源码里**：Codex 把 `retry_429` 写死为 `false`（[openai/codex#30471](https://github.com/openai/codex/issues/30471)），`exceeded retry limit, last status: 429` 这句报错本身在说谎，**它根本没重试过**，第一个 429 就直接甩给你。指望客户端自愈是死路，只能在"请求落到哪条路"上做前置治理，这正是这套工具存在的理由。

| 付了钱、额度剩 61%，照样 429 | "重连 5 次"（官方 issue 原话） |
|---|---|
| <a href="https://github.com/openai/codex/issues/9135"><img src="docs/evidence/gh-9135-quota-left-429.png" width="430"></a> | <a href="https://github.com/openai/codex/issues/26050"><img src="docs/evidence/gh-26050-reconnect-5x.png" width="430"></a> |

| "pro*20 的号…那我真金白银买的是什么" | 全球 429 大停工，OpenAI 状态页都在闪红 |
|---|---|
| <a href="https://linux.do/t/topic/2349056"><img src="docs/evidence/dumb-down-complaints.png" width="430"></a> | <a href="https://linux.do/t/topic/2296878"><img src="docs/evidence/all-429-rage.png" width="430"></a> |

更多实证（写一半断流、降智风控分析、`retry_429` 源码追踪）见 [docs/evidence/](docs/evidence/)。

## 安装

**开始前你需要**：

- 至少一个节点来源：订阅链接，或 `vless:// ss:// trojan://` 等分享链接——进托盘「添加来源」或控制台 `+订阅`/`+节点` 加入
- 首次运行若弹 SmartScreen「已保护你的电脑」：点「更多信息」→「仍要运行」（未签名新应用都会这样）；托盘图标可能藏在任务栏「^」溢出区，拖出来即可

**兼容性**：Windows 10 / 11（理论 Win8+ 也行），只用系统自带 .NET Framework 4.x，**不装任何运行时**；ARM64 Windows 走内置仿真可跑；不需要管理员权限。发布 zip 解压即用（含托盘 ~58KB + 引擎 ~9.6MB），自带轨道图标。

1. 下载[最新 release](../../releases/latest) 里的 `CodexOrbit-*-windows-x64.zip`，解压到任意目录
2. 双击 `CodexOrbit.exe`（`orbit-core.exe` 已附带，两个文件保持在同一目录即可）
3. 开机自启：`Win+R` → `shell:startup` → 丢个 `CodexOrbit.exe` 快捷方式进去

> `orbit-core.exe` 由我们基于上游 ccodex-rotate 源码构建，附带自研补丁（TTFT 首字节耗时、严格防降智、会话锁模型等）——全部源码就在本仓库 [`engine/`](engine/) 目录，`go build ./cmd/ccodex-rotate` 即可复现。

配置沿用 `~/.ccodex-rotate/config.json`（本工具不改你的订阅和节点）。

## 懒得动手？让 AI 替你装

把下面这段原样发给你的 AI 助手（Codex / Windsurf / Cursor / Claude 都行）：

> 帮我部署 CodexOrbit，仓库地址 https://github.com/Reality-JH/CodexOrbit 。
> 按仓库 README 帮我完成安装：下载最新 release 的 windows zip 解压到合适目录，
> 运行 CodexOrbit.exe，确认托盘图标出现、控制台能打开后告诉我。

## 自己编译

不装任何东西，Windows 自带 .NET Framework 就够：

```cmd
build.cmd
```

产出 ~58KB 的 `CodexOrbit.exe`，零第三方依赖，源码就 `src/CodexOrbitApp.cs` 一个文件，随便审。自编译只产托盘端，引擎仍需从 release zip 或上游获取。

**界面语言**跟随系统显示语言自动切换；也可强制：`CodexOrbit.exe --lang=zh` 或 `--lang=en`。

## 它如何和 ccodex-rotate 通信

本地 HTTP API（`http://127.0.0.1:17850`）：

| 功能 | 端点 |
|---|---|
| 状态轮询（含延迟历史） | `GET /api/status` |
| 节点列表 | `GET /api/nodes` |
| 按名固定节点 | `POST /api/pin` |
| 换节点 | `POST /api/rotate` |
| 采集 292 | `POST /api/collect` |
| 恢复自动 | `POST /api/reset` |
| 添加订阅/节点 | `POST /api/sources/add` |
| 清空全部来源 | `POST /api/sources/clear` |
| 健康探针 | `GET /healthz` |

## 给仓库贡献者

- `CodexOrbit.exe --preview`：状态卡居中显示（截宣传图用）
- `CodexOrbit.exe --shot out.png`：无头渲染当前状态卡为 PNG
- `CodexOrbit.exe --lang=zh|en`：强制界面语言

## 诚实边界

它能显著改善连接质量，但**不增加账号额度、不保证 100% 根治**：上游 401/403/429 该等还是得等。它做的是：别让一个烂节点毁掉你付了钱的会话。

## 交流

有问题或想法，来群里聊：**QQ 群 758423201**

<p align="center">
  <img src="docs/qq-group.jpg" width="240" alt="QQ 群二维码">
</p>

## License

仅限个人非商用 · 详见 [LICENSE](LICENSE)
