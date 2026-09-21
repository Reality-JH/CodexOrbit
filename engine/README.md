# orbit-core — CodexOrbit 内置路由引擎

CodexOrbit 自带的本地 Codex 反向代理引擎。**不修改系统代理，不影响你本地的 Clash。** 纯 Go 实现，零第三方依赖。

## 架构（一句话版）

```text
Codex  →  orbit-core(本地)  →  代理节点池  →  chatgpt.com
                │
                ├─ 转发通道(17890)：你发的消息走这里
                └─ 采集通道(17892)：后台去找 292/332 凭据走这里
```

- 你发消息时，引擎经「**转发出口**」把请求送到 Codex 后端；
- 后台同时用「**采集出口**」逐个节点去请求，拿到 `X-Codex-Turn-State`（个人约 292 字符 / Team 约 332 字符）；
- 采到后缓存，并在后续请求里**自动注入**，让回答更稳定；
- 两个通道**完全独立**，互不影响。

## 自研特性（本仓库独有）

- **请求级 TTFT**：每条请求记录首字节耗时与总耗时，供托盘控制台展示
- **实际模型嗅探**：从 SSE 流 `response.created` 事件读出上游真正返回的模型
- **严格防降智**（`strict_model`，默认开）：实得模型 ≠ 请求模型时返回 `422 model_mismatch`，不吃降智回答
- **会话锁模型**（`session_model_lock`，默认开）：同一会话内模型漂移自动改回锚定模型；子 agent 的独立会话互不影响
- **凭据亲和不劫持手动固定**：`inject_node_affinity` 下凭据亲和只在自动选路时切换出口，用户手动固定永远优先（`PinAff`）
- **运行时开关**：`/api/injection`、`/api/strict-model` 即时生效，不用重启
- **降级自动探测**：`probe_interval_seconds` 可调；托盘在降级期间自动退避探测，恢复即通知
- **原生静默**：启动不弹浏览器、不写系统代理，一切走本地回环

## 构建

```cmd
cd engine
go build -o orbit-core.exe ./cmd/orbit-core
```

需要 Go 1.26+。`go.sum` 不存在是正常的——零第三方依赖。

## 命令

```text
orbit-core init                    创建默认配置
orbit-core sub add <url...>        添加订阅链接
orbit-core node add <link...>      添加节点分享链接
orbit-core serve                   启动（托盘伴侣会自动拉起，一般不用手动跑）
orbit-core collect                 立即采集一次凭据
orbit-core status / nodes          查看状态 / 节点
orbit-core fetch-core              下载 mihomo 内核（包内已内置，一般不需要）
orbit-core restore                 还原 Codex 配置
orbit-core path                    显示配置与数据目录位置
```

## 配置

`%USERPROFILE%\.codexorbit\config.json`（老版本数据目录自动迁移）。常用字段：

| 字段 | 默认 | 说明 |
|---|---|---|
| `strict_model` | `true` | 实得模型≠请求模型时拒绝（422） |
| `session_model_lock` | `true` | 同一会话内锁定首个模型 |
| `inject_state` | `true` | 注入已采集的 292 凭据 |
| `inject_node_affinity` | `false` | 自动模式下凭据走产出它的节点 |
| `force_model` | `""` | 留空关闭；设置后改写所有请求模型（混用子 agent 勿开） |
| `probe_interval_seconds` | `0` | 降级探测间隔；0 = 自动退避（120s→600s） |
| `probe_timeout_seconds` | `6` | 采集探测单节点超时 |
| `timeout_seconds` | `120` | 上游请求总超时 |
| `max_retries` | `5` | 请求内换节点重试上限 |
| `state_ttl_seconds` | `3600` | 凭据有效期 |
| `collect_success_interval_seconds` | `1800` | 采集成功后的续期节奏 |
| `collect_retry_interval_seconds` | `300` | 采集失败后的重试节奏 |

## 本地 API（托盘伴侣走这套）

`GET /api/status` · `GET /api/nodes` · `POST /api/pin` · `POST /api/rotate` · `POST /api/reset` · `POST /api/collect` · `POST /api/injection` · `POST /api/strict-model` · `POST /api/sources/add|clear` · `GET /healthz`

网页面板仍在 `http://127.0.0.1:17850/panel`（不会自动打开）；日常使用用 CodexOrbit 原生控制台即可。

## 隐私

- 凭据值从不显示在界面或日志里。
- 全部通信走 `127.0.0.1` 本地回环 + 你配置的节点出口；无遥测、无外部上报。
- 本引擎不含、也不分发任何代理订阅——节点来源由用户自行提供。

第三方组件声明见 [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)。
