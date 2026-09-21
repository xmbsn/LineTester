# 实现计划：链路注册协议 v1（多机房自动互测）

> **计划基准（Spec）**：`docs/REGISTRY-PROTOCOL.md`（v1，**已批准**，SSOT）
> **日期**：2026-09-20 · **当前提交**：`6febcf9` · **执行路线**：inline（`executing-plans`）
> **用户确认需求**：无（授权、范围、验收均已在规格评审时确认）
>
> **进度**：✅ 切片 1 完成（T1–T7，commit `4603756`，自检 **474/474**，0 警告 0 错误）
> ✅ 切片 2 完成（T8–T11，自检 **550/550**，8 次连跑稳定）：端口分流、中心服务、客户端循环、三节点端到端
> → **下一步：切片 3 的 T12（RegistryPanel 设置页）**，然后 T13（中心节点管理 + 链路页联动）。
>
> 切片 2 的实现期修正与发现（均已回写规格）：
> 1. 帧格式改为 `LT1|TYPE|MAC|payload`——mac 若放进 payload 会"签名覆盖自身"，必然校验不过。
> 2. `PART` 必须带 `reqId`，否则客户端无法把分块关联到请求。
> 3. **删除必须持久**：只从清单删会被下一拍心跳加回来 → 中心增加持久移除名单（§9.1）。
> 4. `ACKP` 也要验签（否则伪造位图可让中心把清单发到任意地址）；`ERR` 客户端不强制验签（否则密钥错只会静默超时）。
> 5. 分块改为整批立即发出（原设计的逐块 8ms 延迟收益不足）。
> 6. **测试抓到真实并发缺陷**：`Start()` 的循环与调用方的"立即注册"并发时，同一 socket 上两个 receive
>    互相抢对方的回包 → 间歇性"中心无应答"。已加请求闸门串行化，并让循环任务不因异常终止。

**Aegis Visibility**：本计划存在的理由是这次要落一条**线上线协议 + 配置契约 + 持久化文件**，且必须与老版本双向兼容——没有计划直接写代码，最容易在"分块/幂等/兼容边界"这三处留下难以回滚的偏差。

---

## 1. 目标与已批准范围

把 `docs/REGISTRY-PROTOCOL.md` 的 G1~G7 落成可运行能力：每个客户端只配"自己 + 注册中心"，链路自动两两生成；中心删节点能传播到所有节点（含期间离线的）；中心不可达不影响测试；本地手工链路永不被同步动；老版本混跑不误判；回环地址绝不分发。

**非目标**（规格 §1.2，不实现）：NAT 打洞/中继、TLS/PKI、多中心高可用、显式链路矩阵、载荷加密。

## 2. 需求就绪检查（Requirement Ready Check）

| 项 | 内容 |
|---|---|
| 需求来源 | 用户原始需求 + 两轮设计问答（A 节点清单 / UDP 优先 / 回环不分发 / 含"参与网状"开关） |
| 目标与范围 | 规格 §1.1 G1~G7；非目标 §1.2 |
| 用户/场景 | 机房运维护；N 个机房全网格互测；只有一个入向端口映射；TCP 入向不通 |
| 验收标准 | 规格 §13 A1~A13 |
| 阻塞性未决 | 无（`HeartbeatSeconds/SyncSeconds` 现场取值有默认 60/300，可运行后再调） |
| **结论** | **ready** |

## 3. 基准引用（Baseline refs）

| 引用 | 用途 | 角色 |
|---|---|---|
| `docs/REGISTRY-PROTOCOL.md` | 协议契约 SSOT（帧格式/消息/状态机/验收） | 必需，本计划全程引用 |
| `LineTester.md` §4 关键设计约定 / §5 本机环境坑 / §6 排障口径 | 现有设计约定与构建方式 | 必需 |
| `README.md` | 使用说明（实现后需同步） | 必需（收尾任务） |
| `docs/aegis/BASELINE-GOVERNANCE.md` | 工作区基线治理 | 参考 |

## 4. 现状与改动边界（Files / Owners）

### 4.1 现状规模（实测）

| 文件 | 行数 | 说明 |
|---|---|---|
| `src/MainWindow.xaml` | 450 | 界面（4 个标签页） |
| `src/MainWindow.xaml.cs` | **713** | 界面逻辑 + 探测循环 + 链路编辑器 |
| `src/ProbeServer.cs` | 190 | 被动端（TCP/UDP 双栈 + 回显） |
| `src/ProbeClient.cs` | 261 | 主动端 |
| `src/Models.cs` | 247 | 配置与视图模型 |
| `tests/LineTester.Tests/Program.cs` | **1416** | 全部断言宿主（当前 **284** 项） |

### 4.2 新增（新 owner，避免继续堆到大文件里）

| 新文件 | 职责 |
|---|---|
| `src/Registry/RegistryProtocol.cs` | 帧编解码（`LT1\|TYPE\|payload\n`）、消息类型、DTO |
| `src/Registry/RegistryAuth.cs` | HMAC（对原始 payload 文本）、ts/nonce 防重放、常量时间比较 |
| `src/Registry/RegistryChunking.cs` | 分块切分/位图编解码/组装 |
| `src/Registry/RegistryAddressRules.cs` | 可分发地址判定与错误码 |
| `src/Registry/RegistryStore.cs` | 中心状态：rev 规则、TTL/online、`centerHidden`、原子写 `registry.json` |
| `src/Registry/RegistryCenterService.cs` | 处理 `REG`/`SYNC`：认证、限流、幂等、`selfCheck`、ACK（短路/分块）、传输上下文 |
| `src/Registry/RegistryClient.cs` | 心跳/同步循环、重传退避、分块接收、状态上报、`registry.log` |
| `src/Registry/RegistrySync.cs` | 快照 → `Links[]` 的纯函数应用（增/改/删/过滤/保留 LocalDisabled） |
| `src/Views/RegistryPanel.xaml(.cs)` | 「注册中心」页（UserControl），由 MainWindow 用一个 TabItem 引用 |
| `tests/LineTester.Tests/Program.Registry.cs` | 注册协议相关断言（`Program` 改 `partial`） |

### 4.3 改动（既有 owner）

| 文件 | 最小改动 |
|---|---|
| `src/Models.cs` | `AppConfig` 加 `Registrar` 段与 `RegistrarConfig`；`LinkConfig` 加 `Source`/`NodeId`/`LocalDisabled` |
| `src/ProbeServer.cs` | 收包处按 `LT1\|` 前缀分流（约 20 行）：命中交给 `RegistryCenterService`，否则走原回显路径 |
| `src/MainWindow.xaml` | 新增一个 TabItem「注册中心」引用 `RegistryPanel`；链路配置页加「来源」列 |
| `src/MainWindow.xaml.cs` | 仅接线：创建 `RegistryClient`、把同步结果并入 `_linkRows`、状态栏显示（目标 ≤ 80 行） |
| `src/ConfigManager.cs` | 校验：`Registrar` 字段范围（不引入新规则源） |
| `tests/LineTester.Tests/Program.cs` | 改 `partial class Program`；配置契约断言更新（有意的契约变更） |

### 4.4 Existence Check（新增面是否必要）

```text
Existence Check:
- Proposed new surface: src/Registry/* 9 个新文件 + RegistryPanel UserControl
- Existing owner / reuse candidate: MainWindow.xaml.cs / ProbeServer.cs / ConfigManager.cs
- Why existing surface is insufficient: MainWindow.xaml.cs 已 713 行（界面+循环+编辑器三职责），
  再塞入协议+分块+同步循环会变成 5 职责的巨型类；协议与分块是**纯逻辑**，独立成文件后可直接单测，
  放进 MainWindow 则无法在不构造窗口的情况下测试
- Creation proof: 规格 §15 的 4 个切片各自需要独立可验证的边界；分块/认证/地址规则都是纯函数
- Entropy / retirement impact: 不引入并行 owner（探测回显路径保持唯一 owner）；无 fallback 分支
- Decision: add-with-proof
```

### 4.5 Complexity Budget（计划期复杂度检查）

```text
Complexity Budget:
- Artifact class: 维护型源码 + 测试宿主
- Target files: src/MainWindow.xaml.cs(713) / tests/Program.cs(1416) / src/ProbeServer.cs(190)
- Current pressure: MainWindow.xaml.cs 与 Program.cs 均已接近"单文件承担过多职责"的临界
- Projected post-change pressure: 若就地新增 → MainWindow.xaml.cs ≈ 1100+、Program.cs ≈ 1900+（over-budget）
                         按 4.2/4.3 边界 → MainWindow.xaml.cs ≈ 780、Program.cs 不变、新增文件各自 <300 行
- Budget result: within-budget（仅在采用 4.2 边界时成立）
- Planned governance: 注册相关一律进 src/Registry/；测试进 Program.Registry.cs；
                      界面进 RegistryPanel（MainWindow 只加 TabItem 与接线）
Plan-Time Complexity Check:
- Better file boundary: 见 4.2（纯逻辑 / 网络服务 / 界面三者分离）
- Recommendation: add owner file
```

## 5. 兼容边界（不可回退的承诺）

| 边界 | 承诺 | 由什么验证 |
|---|---|---|
| 端口复用 | 现有探测包（数字或 GUID 开头）**永远**走原回显路径，行为一字不改 | 老式探测包进新服务端仍成功；`v2` 源地址附加仍工作 |
| 老客户端 | 收到 `LT1\|` 会原样回显 → 新客户端判 `unsupported`，**不重试风暴、不产生丢包记录** | 兼容用例（规格 §12 逐行） |
| 配置 | `Links[]` 新字段全部有缺省值 → 旧 `appsettings.json` 直接可用 | 契约用例 + 用旧配置启动 |
| 日志 | 文件名与列布局不变（新协议日志另起 `registry.log`） | 现有 CSV 断言保持通过 |
| 关闭即回退 | `Registrar.Enabled=false`（默认）→ 行为与现在**完全一致** | 默认配置下的全量自检 |

## 6. Change Necessity

为什么不能只改配置/文档：G1~G7 需要**新的通信与状态**（端口复用判定、注册/同步、分块、中心持久化），现有代码没有任何等价能力；`appsettings.json` 也没有"注册中心"这个概念。最小代码边界 = §4.2 的 `src/Registry/*`（纯逻辑与网络）+ §4.3 的四处接线。**不做**：不改探测协议语义、不改统计与日志口径、不引入第二个监听端口。

## 7. Ripple Signal Triage

| 信号 | 是否触发 | 处置 |
|---|---|---|
| 公共契约（线协议） | ✅ | Canonical owner = `docs/REGISTRY-PROTOCOL.md`；实现必须逐条对齐；变更需先改规格 |
| Schema / 持久化 | ✅ | `appsettings.json` 加段/字段（全部有默认值）；新增 `registry.json`（reuse `ConfigManager` 的原子写做法） |
| 生产者/消费者 | ✅ | 生产者=客户端注册；消费者=中心快照 → 各客户端 `Links`；两侧同一份 DTO，禁止各写一套解析 |
| 兼容分支 | ✅ | 保留老回显路径（**保留**而非新增 fallback：它就是既有的唯一路径） |
| 退役 | ⚠️ 无 | 无需删除旧路径；`LineTester.md` §7 的"待实现"条目在收尾时更新 |

## 8. TDD Route

```text
TDD Route:
- mode: off（宿主注入的 Aegis TDD 模式为 off）
- decision: skipped（无 explicit strict 授权）
- authority: AEGIS_DSH_ROUTING_BOOTSTRAP「Aegis TDD mode: off」
- test posture: 每个切片都要新增断言（沿用现有 284 项零依赖自检宿主）作为**回归与验收证据**，
                但不写"先失败的测试再让通过"的 RED/GREEN 步骤
- reason: 无用户/项目 strict 授权；跳过的是流程仪式，不是测试本身
- verification: 每个任务给出可执行命令与期望结果（§10）；分片完成后跑全量自检，末片跑端到端与兼容矩阵
```

## 9. 任务分解（4 个切片 / 14 个任务）

> 每个任务的"证据"= 可复制执行的命令 + 期望结果。全量自检命令：
> `dotnet run --project .\tests\LineTester.Tests\LineTester.Tests.csproj -c Release`

### 切片 1 — 协议与中心逻辑（纯逻辑，无网络）

| # | 任务 | 文件 | 最小改动 | 兼容 | 证据 |
|---|---|---|---|---|---|
| T1 | 配置 schema | `src/Models.cs`、`src/ConfigManager.cs` | 加 `Registrar`（14 字段，§4.1）+ `Links` 三字段（§4.2），全部带缺省；校验范围 | 旧配置零改动可用 | 契约断言更新后全量自检通过；用旧 `appsettings.json` 启动不报错 |
| T2 | 帧编解码 | `src/Registry/RegistryProtocol.cs` | `LT1\|<TYPE>\|<json>\n` 编解码；非法帧/超长返回失败；`LT1\|` 前缀判定 | 无 | 用例：正常帧往返、缺 `\n`、类型未知、payload 非 JSON、空帧 |
| T3 | 认证与防重放 | `src/Registry/RegistryAuth.cs` | `HMAC(type\|rawPayload)`；`\|Δts\|≤300`；nonce LRU 256；常量时间比较 | 无 | 用例：正确 mac 通过、改一字节失败、ts 越界失败、重复 nonce 失败 |
| T4 | 分块与位图 | `src/Registry/RegistryChunking.cs` | 768B 原始块 → base64；位图（LSB=块 0）编解码；缺块清单计算；块数上限 64 | 无 | 用例：n=1/3/7 往返、位图解析、缺块重传清单、超 64 块拒绝 |
| T5 | 地址规则 | `src/Registry/RegistryAddressRules.cs` | 回环/未指定/link-local → 不可分发 + 错误码；私网与公网允许 | 无 | 用例：`127.0.0.1`/`::1`/`0.0.0.0`/`169.254.1.1` 被拒；`10.1.2.3`/`203.0.113.9` 通过 |
| T6 | 中心存储与 rev 规则 | `src/Registry/RegistryStore.cs` | upsert/TTL-离线/`centerHidden`/分发判定；**只有实质变化才 rev++**；原子写 `registry.json` | 无 | 用例：纯心跳不涨 rev；改端口涨 rev；TTL 后 online=false 且涨 rev；文件往返保留 rev |
| T7 | 同步应用（纯函数） | `src/Registry/RegistrySync.cs` | 快照 → 目标 `Links`：过滤自己、过滤不可用地址、保留 `LocalDisabled`、不动 `manual`、删除消失项 | 不触碰 manual | 用例：§8.3 的 7 步逐条（含"离线错过删除"场景） |

**切片完成证据**：全量自检通过（预计 284 → 约 340）；`0 警告 0 错误`。

### 切片 2 — 接入网络与同步循环

| # | 任务 | 文件 | 最小改动 | 兼容 | 证据 |
|---|---|---|---|---|---|
| T8 | 端口分流 | `src/ProbeServer.cs` | 收包处按前缀分流（TCP/UDP 各一处，约 20 行），命中交给注入的 handler | **保留**原回显路径 | 老式探测包进新服务端仍成功（既有用例）；`LT1\|` 不再被回显 |
| T9 | 中心服务 | `src/Registry/RegistryCenterService.cs` | 处理 `REG`/`SYNC`：认证→按类型限流→幂等(reqId)→`selfCheck`→ACK（`changed=false` 短路 / 快照分块）；传输上下文（xferId、并发 32、60s 超时）；非中心回 `ERR unsupported` | 无 | 用例：注册→ACK；重复 reqId 幂等（rev 不重复涨）；限流；各错误码；分块 ACK 与缺块重传 |
| T10 | 客户端循环 | `src/Registry/RegistryClient.cs` | 心跳/同步两个计时器、重传退避、分块接收与位图确认、失败状态上报、`registry.log`（不含密钥/mac） | `Enabled=false` 完全不启动 | 用例：正常同步、丢包重传成功、中心不可达时进入退避且探测不受影响 |
| T11 | 端到端接线 | `src/MainWindow.xaml.cs` | 创建 `RegistryClient`；同步结果并入 `_linkRows` 并原子保存；状态栏文案 | 不改变现有循环语义 | 端到端用例：**3 节点同进程**（不同端口）→ 各自动生成另外 2 条链路；中心删除 B → A/C 的 B 链路消失；**A 离线再上线也消失** |

### 切片 3 — 界面

| # | 任务 | 文件 | 最小改动 | 兼容 | 证据 |
|---|---|---|---|---|---|
| T12 | 设置页 | `src/Views/RegistryPanel.xaml(.cs)` + `MainWindow.xaml` 一个 TabItem | 本节点/注册中心/状态/操作四区（规格 §11.1）；`selfCheck` 比对提示；`AdvertiseTcp/Udp` 带提示 | 新增页，不影响既有页 | 界面断言：字段往返保存、比对提示文案；渲染检查通过 |
| T13 | 中心管理与链路页联动 | `RegistryPanel` + `MainWindow.xaml(.cs)` | 节点表（在线/分发/认证状态 + 删除确认 + `centerHidden` 勾选）；链路配置页「来源」列、注册链路只读 + 删除提示、`LocalDisabled` 显示 | 手工链路行为不变 | 界面断言：勾掉「分发」后其他节点链路消失且不被心跳顶回；注册行只读 |

### 切片 4 — 兼容与收尾

| # | 任务 | 文件 | 最小改动 | 兼容 | 证据 |
|---|---|---|---|---|---|
| T14 | 兼容矩阵与收尾 | `tests/.../Program.Registry.cs`、`README.md`、`LineTester.md`、`docs/REGISTRY-PROTOCOL.md` | 规格 §12 四行逐条验证；规格状态改"已实现"；README 加使用说明；`LineTester.md` §7 勾掉该项 | 全部保留 | 全量自检 + 兼容用例通过；`git status` 干净并推送 |

## 10. 验证汇总

| 层 | 命令 | 期望 |
|---|---|---|
| 编译 | `dotnet build .\LineTester.sln -c Release` | `0 警告 0 错误` |
| 自检 | `dotnet run --project .\tests\LineTester.Tests\LineTester.Tests.csproj -c Release` | `失败 0`（预计 284 → 约 370） |
| 界面渲染 | `… -- --render-ui .\.ui-preview\ui.png` | 5 张 PNG + 排版审查 PASS |
| 端到端 | 同进程 3 节点用例（在自检内） | A2/A3/A4/A9 全部成立 |
| 现场 | 两个机房真机连通后手工验收 | A1、A2、A7 |

## 11. 风险

| 风险 | 处置 |
|---|---|
| UDP 分块在真实链路被限速/丢包 | 块间 8ms + 位图重传 + 60s 超时；单来源限流按类型区分（§5.5）。若现场仍不稳，退路是缩小快照（只分发"自 since 以来的变化"），**规格需先改** |
| `PublicHost` 填错导致全部测不通 | `selfCheck` 交叉提示 + 中心节点表可见地址；验收 A1 现场确认 |
| 中心单点 | 规格 G4：不可达不影响测试；切换中心只需改各节点 `CenterHost` |
| 兼容被破坏（最严重） | 切片 2 的 T8 是**纯新增分支**，老路径不改一行；T14 逐行验证兼容矩阵 |
| 测试宿主继续膨胀 | `Program` 改 `partial` + 新文件 `Program.Registry.cs`，不往 1416 行里加 |

## 12. Repair / Retirement Track

- **Repair Track**：不适用（新功能，无缺陷修复）。
- **Retirement Track**：本计划**不删除**任何既有路径。
  - 老探测回显路径：**保留**（兼容边界，老客户端依赖它逐字节比对）→ 无退役计划。
  - `LineTester.md` §7「链路注册协议」待办条目：在 T14 更新为已完成（文档口径随实现同步）。

## 13. 计划自检（Plan self-review）

| 检查项 | 结果 |
|---|---|
| 覆盖已批准范围（G1~G7 / A1~A13） | ✅ 每个 G 都有对应任务与验收（G1→T12/A1，G2→T7/T11/A1，G3→T7/T11/A2/A3，G4→T10/A4，G5→T7/A5，G6→T8/T14/A9，G7→T5/T9/A7） |
| 占位符 | ✅ 无 TBD/待补；唯一未定项（心跳/同步现场取值）有默认值且不改变方案分类 |
| owner/类型一致 | ✅ DTO 与解析只有一份（`RegistryProtocol`）；中心与客户端共用 |
| 最小改动边界 | ✅ 既有文件只接线；`MainWindow.xaml.cs` 目标 ≤80 行新增 |
| 兼容边界 | ✅ 第 5 节明示四类承诺 + 各自验证方式 |
| 验证可执行 | ✅ 每条命令可复制，期望结果可判定 |
| 条件触发器 | ✅ Requirement Ready / Existence / Complexity / Ripple 均已按触发处理 |
| 退役处理 | ✅ 第 12 节明示"不删除"及理由 |

## 14. 执行交接（Execution Handoff）

```text
Execution Route:
- route: inline（executing-plans）
- evidence: 14 个任务高度共享同一批文件（ProbeServer / MainWindow / Models / 测试宿主），
            且切片间有严格顺序依赖（schema → 协议 → 网络 → 界面 → 兼容），并行化无收益
- fallback: 若某切片需要独立核查，可对该切片单独用 subagent（非必需）
- User confirmation required: no（授权与验收已在规格评审确认；无不可逆外部动作）
```

**下一步**：按 T1 → T14 顺序执行；每个切片结束跑全量自检并提交一次（切片是提交单元）。
