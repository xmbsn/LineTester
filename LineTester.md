# LineTester 项目备忘（会话总结 / 设计约定 / 踩坑记录）

> 用途：下次会话快速恢复上下文。本文件只覆盖 **LineTester 项目**（`E:\dsh\LineTester`）。
> **远端**：`https://github.com/xmbsn/LineTester`（**private**，分支 `main`，tip = `576d07c`）
> **最新会话 2026-09-20**：项目分析 → 按发现清单修复（3 个 P0 + 4 个 P1 + 一批 P2）→ 新增「清除」按钮、
> TCP/UDP 协议勾选、链路编辑器（含防误删）、界面重排、报文带对端源地址、失败原因分类、日志按天滚动 →
> 上传 GitHub（private）。自检从 **0 → 284 项断言**，全程 `0 警告 0 错误`。

---

## 0. 会话续接提示（下次新会话从这里开始）

> 读取 `E:\dsh\LineTester\LineTester.md`（本文件）与 `README.md`，然后：
> ① `dotnet build .\LineTester.sln -c Release` 确认可编译；
> ② `dotnet run --project .\tests\LineTester.Tests\LineTester.Tests.csproj -c Release` 跑自检（应 284/284 通过）；
> ③ 改任何代码前先看本文件 **§4 关键设计约定** 与 **§5 本机环境坑**（这两节能避免重复踩坑）。
> 注意：**测试工程不在 .sln 里**（本机 SDK 损坏，见 §5.2），必须按路径构建/运行。

**核心事实（一句话版）**

- .NET 8 WPF 桌面工具：**主动探测 + 被动应答一体**，两端互相探测对方的监听端口；输出实时统计与按天滚动的 CSV 日志。
- 所有运行参数在**程序目录**的 `appsettings.json`；界面只是可视化入口（含链路编辑器）。
- 当前 **9 个源文件 + 1 个测试工程**，零 NuGet 依赖。

---

## 1. 项目是什么

机房之间线路稳定性测试工具。每个机房跑一份程序，A 探测 B 的 `ListenPort`，B 原样回显；双向对称。

| 文件 | 职责 |
|---|---|
| `src/Models.cs` | `AppConfig` / `LinkConfig` / `ProbeResult` / `StatRow` / `LinkRow` |
| `src/ConfigManager.cs` | appsettings.json 读写（原子写+重试）、统一的链路校验规则 |
| `src/ProbeServer.cs` | 被动端：TCP/UDP 双栈监听、原样回显、按标记附加"对端源地址" |
| `src/ProbeClient.cs` | 主动端：探测、RTT/建连分离计时、回包校验、失败原因分类 |
| `src/Session.cs` | 统计会话（滑动窗口 / 连续丢包 / 源地址 / 失败原因 / epoch） |
| `src/Statistics.cs` | 百分位、四级状态判定（纯函数，可测） |
| `src/CsvLogWriter.cs` | 后台通道 + 按天分目录滚动写 CSV |
| `src/FailureKinds.cs` | 失败原因 token ↔ 中文文案 |
| `src/MainWindow.xaml(.cs)` | 界面、探测循环、节流刷新、链路编辑器 |
| `tests/LineTester.Tests/Program.cs` | 零依赖自检宿主（284 项断言，含真实回环网络与真实窗口端到端） |

---

## 2. 当前状态与验证方式

```powershell
# 编译（只含应用工程）
dotnet build .\LineTester.sln -c Release

# 自检：全部通过退出码 0，有失败退出码 1
dotnet run --project .\tests\LineTester.Tests\LineTester.Tests.csproj -c Release

# 渲染界面快照 + 数值化排版审查（不启动程序、不闪窗）
dotnet run --project .\tests\LineTester.Tests\LineTester.Tests.csproj -c Release -- --render-ui .\.ui-preview\ui.png

# 发布（win-x64 自包含单文件，appsettings.json 会自动带上）
.\build.bat
```

⚠️ `publish\LineTester.exe` 是**旧构建**；要用上当前改动必须重新发布。

---

## 3. 本会话（2026-09-20）改动清单

### 3.1 修复分析阶段发现的问题

| 级别 | 问题 | 修法 |
|---|---|---|
| P0-1 | 配置路径与"程序目录"不一致：`dotnet run` 读的是 bin 下的空配置，仓库根目录那份完全不生效 | csproj 增加 `None Include=..\appsettings.json` 复制到输出/发布目录 |
| P0-2 | 文案声称"启动测试会重新读取配置"（实际只读界面）；运行中手改配置文件会被静默覆盖 | 文案订正 + **外部修改检测**三选一弹窗（重载/覆盖/取消）+ 解析失败时**不隐式覆盖** |
| P0-3 | 停止/重启时在途探测被当作**假丢包**写进日志与统计 | 取消返回 `null`（不计丢包）；`epoch` 会话号隔离"变更前发起、变更后返回"的结果 |
| P1-1 | 只监听 IPv4；域名解析到 IPv6 时全量超时 | 双栈监听（并**校验 DualMode 真生效**，否则退 IPv4）+ 客户端 IPv4 优先 + DNS 缓存 60s |
| P1-2 | RTT 含 DNS+握手，且整数毫秒把回环截断成 0 | RTT 改为**纯往返**（不含解析/建连），3 位小数；建连单独记 `setup_ms` |
| P1-3 | 每条结果全量重算百分位 + `Clear()` 重建表格（闪烁、丢选中） | 聚合移出界面；**500ms 节流**刷新；`StatRow` 就地更新 |
| P1-4 | UDP 回包不校验内容，串包会算成本次 RTT | TCP 前缀比对；UDP 不匹配则继续等（不误判成功） |
| P2 | 配置非原子写/中文被转义/日志在 UI 线程逐条开关文件/UDP 发送无超时 | 原子写+退避重试、`UnsafeRelaxedJsonEscaping`、后台队列写日志、全部收发带 ct |
| P2 | 状态"窗口内出现过一次丢包就永久报警"；异常表缺端口 | 四级状态 + `LossWarnPercent`/`ConsecutiveLossWarn` 可配；补端口列 |
| P2 | 无全局异常处理、无单实例、无 .gitignore | 全局异常+`logs\error.log`；按目录单实例；补 `.gitignore` |

### 3.2 新增功能

- **清除按钮**（DevTools 语义）：清空界面统计并立即重新开始；**运行中可反复点击**；不影响监听与日志。
- **TCP/UDP 协议勾选**（全局总开关）：探测循环**每轮重读**，取消勾选**立即生效**；与链路自身开关**取交集（只能收紧，不能放宽）**；链路配置页有「生效（实际发包）」列，摘要会提示"全局未勾选，已压住 …"。
- **链路编辑器**（「链路配置」页）：新增/复制/删除/撤销删除；逐行**实时校验**（与 `ConfigManager.ValidateLink` 同一套规则）；同目标同协议**重复检测**；非法行标红+原因；**校验不过拒绝保存/启动**并自动跳页；端口用**字符串**承载（否则非法输入被绑定吞掉、保存时静默写回旧值）。
  **防误删四重**：确认弹窗（默认"否"，带链路名与目标）/ 删最后一条时额外警告 / 单级撤销（含原位置）/ `Del` 键不能删行。
- **允许"两个协议都不勾选"**：表示该链路**不主动探测**（对端可能是纯服务端，或临时停测而不删配置）。摘要会提示"N 条不主动探测"。
- **对端看到的本端源地址**：报文 `序号|探测ID|v2`，被动端回 `…|v2|ip:端口\n`。跨公网即**出口公网 IP + NAT 后源端口**，逐条记入 CSV 的 `seen_from` 与三个表格；统计表显示该链路最近值。
- **失败原因分类**：`timeout` / `refused` / `icmp_unreachable` / `reset` / `closed` / `mismatch` / `host_not_found` / `unreachable` / `error`。界面「失败原因」列 + CSV `failure` 列。
- **日志按天分目录滚动**：`logs\YYYY-MM-DD\<链路>_<IP>_<端口>.csv`。
- **界面重排**：4 列×2 行参数网格、操作区左右分组（主操作/配置操作）、状态条（状态灯+状态+参数摘要+样本数+提示行）、表格统一外观（数值右对齐/隔行底纹/表头浅底）、说明收进**默认折叠**的 Expander、`MinWidth/MinHeight`。
- **修 `build.bat`**：原来是 PowerShell 反引号续行，在 CMD 里跑不通；已改成可用批处理。

### 3.3 上传 GitHub

- `git init -b main` → 首个提交 `576d07c`（20 文件 / 233 KB；`publish/` `bin/` `obj/` `logs/` `.ui-preview/` 均被 gitignore 排除）
- 创建 **private** 仓库 `xmbsn/LineTester` 并推送 `main`；本地 `main` 跟踪 `origin/main`。
- 推送方式见 §5.1（schannel 坏 + 代理 + openssl）。

---

## 4. 关键设计约定（改代码前必读）

### 4.1 配置

- 顶层键**固定 10 个**（有契约测试守着，多一个派生属性就会失败）：`ListenPort` `IntervalMs` `TimeoutMs` `WarningRttMs` `StatWindowMinutes` `LossWarnPercent` `ConsecutiveLossWarn` `EnableTcp` `EnableUdp` `Links`
- `Links[]` 固定 5 个键：`Name` `Host` `Port` `Tcp` `Udp`
- **只读派生属性绝不能加到配置类上**（`System.Text.Json` 会把它序列化进文件，现场看不懂）——曾经踩过：`LinkConfig.Key`/`Target` 被写进 `publish\appsettings.json`。
- 缺键 = 用代码默认值（旧配置直接兼容）；**未知键静默忽略**（拼错不报错，会静默用默认值）。
- 保存会**重写整个文件**：手写注释与自定义键会丢失（注释与尾逗号在**读取**时是允许的）。

### 4.2 探测与统计口径

- **RTT = 发出首字节 → 收齐回包**，不含 DNS 与 TCP 建连（所以 TCP/UDP 两行可比）；建连/解析单独记 `setup_ms`。
- **序号**在发送时分配、运行期**不清零**（同一 CSV 内唯一，便于与抓包对齐）；**丢包也占一个号**，所以跳号只代表"结果未被记录"（停止/清除）。
- **取消 ≠ 丢包**：`ProbeAsync` 返回 `null` 表示调用方取消，绝不写日志。
- **epoch**：清除/启动会 +1；`Accept(epoch)` 决定结果是否入账——这是"清除后能立刻重新开始统计、且不混入旧数据"的机制。**探测循环每轮重读 epoch**（固定成启动值会导致清除后永远统计不上，曾踩过）。
- 状态四级：中断（连续丢包≥阈值）> 有丢包（窗口丢包率≥阈值）> 高延迟（平均 RTT≥阈值）> 正常。
- **失败原因不是装饰**：`refused` 说明**路径通、端口没监听**；`timeout` 说明包在**到达监听者之前**就被丢了。这是排查端口映射的核心区别。

### 4.3 版本兼容（两端可以不同时升级）

被动端**只有**看到请求末尾的 `v2` 标记才追加源地址；否则严格**原样回显**——因为老主动端是**逐字节比对**回包的，多一个字节就会把整条线路判成丢包。

| 主动端 | 被动端 | 结果 |
|---|---|---|
| 新 | 新 | 记录源地址 |
| 新 | 旧 | 探测正常，`seen_from` 为空 |
| 旧 | 新 | 探测正常（不带 v2 → 原样回显） |
| 旧 | 旧 | 与升级前一致 |

附加信息读取有 **120ms 宽限**（`ProbeClient.TrailerGraceMs`），且发生在 **RTT 计时停止之后**，不影响时延数值。

### 4.4 界面

- 表格统一在 `Window.Resources` 里的隐式 `DataGrid`/`DataGridColumnHeader`/`DataGridRow` 样式；**隔行底纹用行样式触发器**实现（用 `AlternatingRowBackground` 会盖掉"非法行标红"）。
- 按钮样式：`BaseButton`（键控）+ **隐式 `Style TargetType="Button"`** + `PrimaryButton`。曾经因为只定义了键控样式，导致除主按钮外全部退回 WPF 默认外观。
- 状态栏四段：`StatusDot` / `StatusText` / `StatusParams` / `StatusSamples` + 独立的 `StatusNote`（出错时变红）。
- 链路改动是**配置类**变更，需点「启动测试」生效（页脚会提示"运行中的测试仍在使用启动时的链路"）；**协议勾选是唯一即时生效的开关**。

### 4.5 测试工程（重要）

- 零依赖自检宿主（本机 NuGet 不可达，无法用 xunit）：`Check/Eq/Near` 断言 + 退出码 0/1。
- 覆盖：纯逻辑（百分位/状态/会话/配置/CSV）+ **真实回环网络**（TCP/UDP/IPv6、取消、伪装回包、新旧版本互操作）+ **真实窗口端到端**（STA 线程 + 消息泵驱动 Start/Clear/Stop/协议勾选/链路编辑器）。
- **测试缝**（都为测试真实关卡而非内部小函数）：
  - `ConfigManager.OverridePath` —— 覆盖配置文件路径；
  - `MainWindow._alert` / `_confirmDelete` —— 用反射替换掉模态框。
- 异步写入器有 `WaitForIdleAsync()`："待写计数归零"发生在 Flush **之后**，所以返回 true 即数据已落盘（测试因此确定性，不靠 sleep）。

---

## 5. 本机环境坑（务必先看）

### 5.1 GitHub 访问（schannel 坏 + 代理 + openssl）

```powershell
# 诊断：schannel 报 SEC_E_NO_CREDENTIALS(0x8009030e)，连 baidu.com 都会失败
# 正解：切 openssl 后端 + 走本机代理
git -c http.sslBackend=openssl -c http.proxy=http://127.0.0.1:17895 -c https.proxy=http://127.0.0.1:17895 push origin main
```

- 用户名 `xmbsn` / 邮箱 `404216104@qq.com`；**凭据管理器里有有效 PAT**（`git:https://github.com`，`repo` scope），普通终端里 `wincred` 供凭据即可。
- **代理端口**：当前 `127.0.0.1:17895`（系统代理与 git 全局配置都是它；`clash-windows-amd64` 在跑）。
  ⚠️ 端口会变，变了就跑 `E:\dsh\git-vpn.ps1` 自动探测并写回 git 全局配置。
  ⚠️ `sda` 技能里写的 `33210` 已失效（那是旧记录）。
- **沙箱内** git 凭据助手子进程会因 signal pipe 失败 → 用显式 Basic 头绕过（token 只放内存/环境变量，**不要写进文件或命令文本**）：
  ```powershell
  $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("xmbsn:$pass"))
  git -c http.sslBackend=openssl -c http.proxy=http://127.0.0.1:17895 -c https.proxy=http://127.0.0.1:17895 `
      -c credential.helper= -c "http.extraHeader=Authorization: Basic $b64" push -u origin main
  ```
- 相关手册：`C:\Users\xd.huang\.dsh\skills\sda\SKILL.md`（**未注册进会话 catalog，必须直接读文件**）。

### 5.2 .NET SDK 损坏（影响构建方式）

- `C:\Program Files\dotnet\sdk\8.0.425\Sdks\` 下**缺** `Microsoft.NET.SDK.Workload*Locator` 目录 → **任何多项目解决方案的 restore 都会失败**（`MSB4276`，且报"0 错误"却 `exit 1`）。
- `dotnet --info`（列工作负载）与 `dotnet new` 也会崩。
- **对策**：`LineTester.sln` 只放应用工程；测试工程按**路径**构建/运行。SDK 修好后可 `dotnet sln add` 加回。

### 5.3 其它

- **NuGet 不可达** → 不用任何需要还原的测试框架。
- **沙箱限制**：`Get-NetTCPConnection` 返回空（不代表没监听）；对子进程做管道捕获在部分场景会 EPERM；**控制台是 GBK**，打印 emoji/中文可能抛 `UnicodeEncodeError` → 用 `PYTHONIOENCODING=utf-8` 或只输出 ASCII。
- 视觉：本机**没有可用的视觉模型**，渲染出的 PNG 我无法自己查看；用 `--render-ui` 输出到 `.ui-preview\` 由人眼确认，程序侧只做数值化排版审查。

---

## 6. 排障口径（失败原因 → 现场动作）

| `failure` | 界面文案 | 含义 | 下一步 |
|---|---|---|---|
| `timeout` | 无响应（超时） | 包在**到达监听者之前**被丢弃 | 查端口映射 / 机房 ACL / 安全组 / 主机防火墙 / 运营商封端口 |
| `refused` | 连接被拒绝（端口无监听） | 收到 RST → **路径是通的** | 查对端程序是否在监听、内网映射端口是否填对 |
| `icmp_unreachable` | UDP 端口不可达（收到 ICMP） | 包**已到达对端主机** | 该 UDP 端口没有程序监听 |
| `reset` | 连接被重置 | 被 RST 中断 | 对端程序异常退出 / 中间设备重置 |
| `closed` | 对端提前关闭连接 | 建立后被关闭 | 该端口上的服务不是本工具 |
| `mismatch` | 回包内容不匹配 | 回包不是本工具回的 | 该端口跑着别的服务 |

**"UDP 通、TCP 不通"的检查顺序**：① 被动端 `Get-NetTCPConnection -State Listen` 与 `Get-NetUDPEndpoint` 是否**都在**；
② 防火墙是否**两个协议都放行**（最常见原因：只放了 UDP）；③ 路由器 TCP 静态映射的外部端口是否与配置的 `Port` 一致
（**每条链路只有一个 Port，TCP/UDP 共用**——若映射端口不同，拆成两条链路分别只勾一个协议）；④ 主动端直接测端口（快失败=被拒绝=映射通；慢失败=被丢）；⑤ 被动端 `pktmon` 抓包看 SYN 有没有到；⑥ 换高位端口排除运营商封禁。
⚠️ 要稳定区分 `refused` 与 `timeout`，`TimeoutMs` 建议 **3000~5000**（Windows 上 RST 可能 ~2 秒才返回）。

---

## 7. 待办 / 可选下一步

- [ ] **链路注册协议（多机房自动互测）**：设计已批准，规格见 `docs/REGISTRY-PROTOCOL.md`（UDP 优先 + 分块重传、回环地址绝不分发、快照语义删除传播、`Registrar` 配置段）。
      **下一步：按 §15 的 4 个切片写实现计划并开工**（先落实现计划，再动代码）。
- [ ] **重新发布**（`.\build.bat`），让机房用上当前改动；建议两端都升级才能看到 `seen_from`（只升一端也不会坏）。
- [ ] 可选：`LogRetentionDays`（保留 N 天自动清理，默认 0 = 不删；涉及删数据，未默认开启）。
- [ ] 可选：`Links` 支持**TCP/UDP 分开的公网端口**（当前靠"拆两条链路"变通）。
- [ ] 可选：被动端统计"本机收到多少探测包"（现在被动端不记录入站探测，只能抓包看）。
- [ ] 可选：RTT 趋势图、告警通知（声音/托盘/邮件）、`push.bat` 一键推送。
- [ ] 可选：更新 `sda` 技能里两处过期信息（代理端口 `33210`→`17895`；"旧 PAT 已撤销"已不成立）。

---

## 8. 本次会话的验证证据

- `dotnet build .\LineTester.sln -c Release` → **0 警告 0 错误**（多次）
- 自检 **284/284 通过**，连续多轮稳定（`exit=0`）
- 渲染 + 排版审查通过：非法行确实标红、校验列给出原因、静态区域无文本裁剪/越界
- 真实 CSV 证据（回环）：`…,seq,probe_id,seen_from,failure` 列齐全；`seen_from` 为客户端临时源端口；RTT 亚毫秒（`0.150` / `0.412`）
- 测试过程中被断言抓出并修掉的**真实缺陷**：① 清除后统计永远无法恢复（epoch 被固定）；② `File.Move` 覆盖目标偶发 `UnauthorizedAccessException`（索引器/杀软瞬时占用）；③ 界面按钮样式只定义了键控样式导致四个按钮退回默认外观；④ 权限：链路名可来自界面输入，已加"恶意名字不越出日志根目录"断言
- 被断言纠正的**错误预期**：Windows 上"被拒绝"约 2 秒才返回（不是瞬时）；对端有未读数据时关闭连接会回 RST（不是干净的 FIN）
