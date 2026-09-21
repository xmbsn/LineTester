using System.IO;
using System.Text;
using LineTester.Registry;

namespace LineTester.Tests;

/// <summary>
/// 链路注册协议的自检（规格 docs/REGISTRY-PROTOCOL.md）。
/// 与 <c>Program</c> 同一 partial 类，共用断言宿主。
/// </summary>
internal static partial class Program
{
    // ---------------- 帧编解码 ----------------

    private static void TestRegistryProtocol()
    {
        Section("注册协议：帧编解码");

        var frame = RegistryProtocol.Encode(RegistryProtocol.TypeRegister, "{\"a\":1}");
        Eq("帧格式为 LT1|TYPE|payload\\n", "LT1|REG|{\"a\":1}\n", Encoding.UTF8.GetString(frame));
        Check("协议帧被识别", RegistryProtocol.LooksLikeProtocolMessage(frame, frame.Length));
        Check("帧可解析", RegistryProtocol.TryDecode(frame, frame.Length, out var type, out var payload));
        Eq("类型解析正确", "REG", type);
        Eq("payload 原样保留（HMAC 依赖原文，不能重新序列化）", "{\"a\":1}", payload);

        var probe = Encoding.UTF8.GetBytes("42|3f2a9c1d4b5e6f708192a3b4c5d6e7f8|v2");
        Check("探测包（数字开头）不被误判为协议消息",
            !RegistryProtocol.LooksLikeProtocolMessage(probe, probe.Length));
        var legacyProbe = Encoding.UTF8.GetBytes("3f2a9c1d4b5e6f708192a3b4c5d6e7f8");
        Check("老版本探测包（GUID 开头）不被误判为协议消息",
            !RegistryProtocol.LooksLikeProtocolMessage(legacyProbe, legacyProbe.Length));

        Check("缺类型分隔符的帧被拒绝",
            !RegistryProtocol.TryDecode(Encoding.UTF8.GetBytes("LT1|REG\n"), 8, out _, out _));
        Check("空类型的帧被拒绝",
            !RegistryProtocol.TryDecode(Encoding.UTF8.GetBytes("LT1||{}\n"), 8, out _, out _));
        Check("空输入被拒绝", !RegistryProtocol.TryDecode(Array.Empty<byte>(), 0, out _, out _));

        var tricky = "{\"name\":\"a|b\\nc\",\"n\":1}";
        var trickyFrame = RegistryProtocol.Encode(RegistryProtocol.TypeRegister, tricky);
        Check("payload 含 | 与转义换行时往返正确",
            RegistryProtocol.TryDecode(trickyFrame, trickyFrame.Length, out var trickyType, out var trickyPayload)
            && trickyType == "REG" && trickyPayload == tricky,
            trickyPayload);

        var message = new RegisterMessage
        {
            ReqId = "a1b2c3d4",
            NodeId = "n1",
            Name = "机房A",
            Ip = "203.0.113.10",
            Port = 50001,
            Tcp = true,
            Udp = false,
            Distributable = true,
            Instance = "i1",
            Rev = 7,
            Ts = 1789000000,
            Nonce = "9f8e7d6c",
            Mac = "deadbeef"
        };

        var json = RegistryProtocol.Serialize(message);
        Check("序列化为紧凑单行（不含裸换行）", !json.Contains('\n'), json);
        Check("序列化使用 camelCase（reqId/nodeId/distributable/reannounceAfter 风格）",
            json.Contains("\"reqId\"") && json.Contains("\"nodeId\"") && json.Contains("\"distributable\""), json);

        var back = RegistryProtocol.Deserialize<RegisterMessage>(json);
        Eq("DTO 往返保留节点名", "机房A", back?.Name);
        Eq("DTO 往返保留 rev", 7UL, back!.Rev);
        Eq("DTO 往返保留端口", 50001, back.Port);
        Check("非法 JSON 返回 null 而不抛异常", RegistryProtocol.Deserialize<RegisterMessage>("not json") is null);

        var ack = new AckMessage
        {
            ReqId = "a1",
            Rev = 3,
            Changed = true,
            ReannounceAfter = 180,
            SelfCheck = new SelfCheckInfo { SeenIp = "198.51.100.7", SeenPort = 41234 },
            Nodes = new List<NodeSnapshot> { new() { NodeId = "b", Name = "机房B", Ip = "203.0.113.20", Port = 50002, Tcp = true, Udp = true } }
        };
        var ackJson = RegistryProtocol.Serialize(ack);
        Check("ACK 序列化含 reannounceAfter（camelCase）", ackJson.Contains("\"reannounceAfter\""), ackJson);
        Check("ACK 序列化含 selfCheck 与节点快照", ackJson.Contains("\"selfCheck\"") && ackJson.Contains("\"nodes\""), ackJson);
        var ackBack = RegistryProtocol.Deserialize<AckMessage>(ackJson);
        Eq("ACK 往返保留节点数", 1, ackBack?.Nodes?.Count ?? -1);
        Eq("ACK 往返保留 selfCheck 端口", 41234, ackBack?.SelfCheck?.SeenPort ?? -1);
    }

    // ---------------- 认证与防重放 ----------------

    private static void TestRegistryAuth()
    {
        Section("注册协议：认证与防重放");

        const string secret = "preshared-secret";
        const string type = "REG";
        const string payload = "{\"reqId\":\"a1\",\"nodeId\":\"n1\"}";

        var mac = RegistryAuth.ComputeMac(secret, type, payload);
        Eq("mac 为 64 位十六进制", 64, mac.Length);
        Check("正确 mac 通过校验", RegistryAuth.VerifyMac(secret, type, payload, mac));
        Check("换密钥后失败", !RegistryAuth.VerifyMac("other-secret", type, payload, mac));
        Check("payload 多一个字符即失败", !RegistryAuth.VerifyMac(secret, type, payload + " ", mac));
        Check("类型不同即失败", !RegistryAuth.VerifyMac(secret, "SYNC", payload, mac));
        Check("空 mac 失败", !RegistryAuth.VerifyMac(secret, type, payload, ""));
        Check("mac 大小写不敏感", RegistryAuth.VerifyMac(secret, type, payload, mac.ToUpperInvariant()));
        Check("同一输入 mac 稳定", RegistryAuth.ComputeMac(secret, type, payload) == mac);

        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var nowSeconds = RegistryAuth.ToUnixSeconds(now);
        Check("当前时间戳通过", RegistryAuth.IsTimestampFresh(nowSeconds, now));
        Check("偏差 299 秒通过", RegistryAuth.IsTimestampFresh(nowSeconds - 299, now));
        Check("旧时间戳 301 秒被拒绝", !RegistryAuth.IsTimestampFresh(nowSeconds - 301, now));
        Check("未来时间戳 301 秒被拒绝", !RegistryAuth.IsTimestampFresh(nowSeconds + 301, now));

        var cache = new NonceCache(4);
        Check("首次 nonce 通过", cache.TryAdd("n1", "aaaa"));
        Check("重复 nonce 被拒绝（防重放）", !cache.TryAdd("n1", "aaaa"));
        Check("不同节点的同一 nonce 通过", cache.TryAdd("n2", "aaaa"));
        Check("第三个通过", cache.TryAdd("n3", "bbbb"));
        Check("第四个通过", cache.TryAdd("n4", "cccc"));
        Eq("缓存容量上限", 4, cache.Count);
        Check("第五个通过并淘汰最旧", cache.TryAdd("n5", "dddd"));
        Eq("淘汰后仍为 4 条", 4, cache.Count);
        Check("被淘汰的 nonce 可以再次使用", cache.TryAdd("n1", "aaaa"));

        var requestId = RegistryAuth.NewRequestId();
        Eq("请求号为 8 位十六进制", 8, requestId.Length);
        Check("请求号是十六进制", requestId.All(Uri.IsHexDigit), requestId);
        Check("两次请求号不同", RegistryAuth.NewRequestId() != RegistryAuth.NewRequestId());
        Eq("nonce 为 8 位十六进制", 8, RegistryAuth.NewNonce().Length);
    }

    // ---------------- 分块与位图 ----------------

    private static void TestRegistryChunking()
    {
        Section("注册协议：分块与位图");

        const string small = "{\"a\":1}";
        var single = RegistryChunking.Split(small);
        Eq("小 payload 只分 1 块", 1, single.Count);
        Check("单块可拼回", RegistryChunking.TryJoin(single, out var singleJoined) && singleJoined == small, singleJoined);
        Eq("单块内容等于原文的 base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(small)), single[0]);
        Eq("块数预估与切分一致", 1, RegistryChunking.ChunkCount(small));

        var big = "{\"nodes\":[" + string.Join(",",
            Enumerable.Range(0, 200).Select(i => $"{{\"i\":{i},\"name\":\"节点{i}\"}}")) + "]}";
        var chunks = RegistryChunking.Split(big);
        Check("大 payload 需要多块", chunks.Count > 1, chunks.Count.ToString());
        Check("每块 base64 长度不超过 1024", chunks.All(c => c.Length <= 1024));
        Check("块数不超过上限", chunks.Count <= RegistryChunking.MaxChunks, chunks.Count.ToString());
        Check("多块可拼回原文（含中文，字节边界切分安全）",
            RegistryChunking.TryJoin(chunks, out var bigJoined) && bigJoined == big,
            $"块数={chunks.Count}");
        Eq("块数预估与切分一致（大 payload）", chunks.Count, RegistryChunking.ChunkCount(big));

        var bitmap = RegistryChunking.EmptyBitmap(chunks.Count);
        Check("空位图不完整", !RegistryChunking.IsComplete(bitmap, chunks.Count));
        Eq("缺失块数等于块数", chunks.Count, RegistryChunking.MissingChunks(bitmap, chunks.Count).Count);
        bitmap = RegistryChunking.SetBit(bitmap, 0);
        Check("块 0 已收", RegistryChunking.HasBit(bitmap, 0));
        Check("块 1 未收", !RegistryChunking.HasBit(bitmap, 1));
        for (var i = 1; i < chunks.Count; i++) bitmap = RegistryChunking.SetBit(bitmap, i);
        Check("全部置位后完整", RegistryChunking.IsComplete(bitmap, chunks.Count));

        var partialBitmap = RegistryChunking.SetBit(RegistryChunking.EmptyBitmap(4), 0);
        partialBitmap = RegistryChunking.SetBit(partialBitmap, 3);
        Eq("缺块清单正确（用于重传）", "1,2", string.Join(",", RegistryChunking.MissingChunks(partialBitmap, 4)));

        var nine = RegistryChunking.EmptyBitmap(9);
        Eq("9 块的位图长 3 个十六进制字符", 3, nine.Length);
        nine = RegistryChunking.SetBit(nine, 8);
        Check("跨字符边界的第 8 块被置位", RegistryChunking.HasBit(nine, 8));
        Check("第 8 块置位不影响第 7 块", !RegistryChunking.HasBit(nine, 7));

        Check("有缺块时拼接失败", !RegistryChunking.TryJoin(new List<string?> { chunks[0], null }, out _));
        Check("非法 base64 拼接失败", !RegistryChunking.TryJoin(new List<string?> { "!!!not-base64" }, out _));
        Check("非法位图字符不误判为已收", !RegistryChunking.HasBit("zz", 0));
    }

    // ---------------- 可分发地址规则 ----------------

    private static void TestRegistryAddressRules()
    {
        Section("注册协议：可分发地址规则");

        foreach (var loopback in new[] { "127.0.0.1", "127.1.2.3", "::1", "::ffff:127.0.0.1", "localhost" })
        {
            Check($"{loopback} 不可分发（否则其他节点会测到自己的回环）",
                !RegistryAddressRules.IsDistributable(loopback));
            RegistryAddressRules.IsDistributable(loopback, out var code, out _);
            Eq($"{loopback} 的错误码为 loopback", RegistryErrorCodes.Loopback, code);
        }

        foreach (var unspecified in new[] { "0.0.0.0", "::", "", "  " })
        {
            Check($"“{unspecified}”不可分发", !RegistryAddressRules.IsDistributable(unspecified));
        }

        RegistryAddressRules.IsDistributable("169.254.1.1", out var linkLocalCode, out _);
        Check("169.254.x.x 不可分发", linkLocalCode == RegistryErrorCodes.LinkLocal);
        Check("fe80:: 不可分发", !RegistryAddressRules.IsDistributable("fe80::1"));

        Check("私网 10.x 可分发（机房之间可能走内网/VPN）", RegistryAddressRules.IsDistributable("10.1.2.3"));
        Check("私网 172.16.x 可分发", RegistryAddressRules.IsDistributable("172.16.5.5"));
        Check("私网 192.168.x 可分发", RegistryAddressRules.IsDistributable("192.168.1.10"));
        Check("公网 IPv4 可分发", RegistryAddressRules.IsDistributable("203.0.113.10"));
        Check("公网 IPv6 可分发", RegistryAddressRules.IsDistributable("2001:db8::1"));
        Check("域名放行（本机无法判定其指向，由运维负责）", RegistryAddressRules.IsDistributable("peer.example.com"));
        Check("带空格的地址被 trim 后判定", RegistryAddressRules.IsDistributable("  203.0.113.10  "));
        Check("可分发时不返回错误码", RegistryAddressRules.IsDistributable("203.0.113.10", out var noCode, out _) && noCode.Length == 0);
    }

    // ---------------- 中心清单与 revision ----------------

    private static void TestRegistryStore()
    {
        Section("注册协议：中心清单与 revision");

        var dir = Path.Combine(AppContext.BaseDirectory, ".testtmp", "reg_" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "registry.json");
        var now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var clock = now;

        RegisterMessage Msg(string nodeId, string name, string ip, int port, string instance,
            bool distributable = true, bool tcp = true, bool udp = true) => new()
            {
                NodeId = nodeId,
                Name = name,
                Ip = ip,
                Port = port,
                Tcp = tcp,
                Udp = udp,
                Distributable = distributable,
                Instance = instance
            };

        try
        {
            var store = new RegistryStore(path, 60, () => clock);
            Eq("初始 revision 为 0", 0UL, store.Revision);
            Eq("初始清单为空", 0, store.Nodes.Count);

            var first = store.Upsert(Msg("n1", "机房A", "203.0.113.10", 50001, "i1"), true);
            Check("首次注册被接受", first.Accepted);
            Check("首次注册使 rev 变化", first.Changed);
            Eq("rev 变为 1", 1UL, store.Revision);
            Eq("清单里有一个节点", 1, store.Nodes.Count);
            Check("节点标记为已认证", store.Nodes[0].Authenticated);
            Check("节点默认在线", store.Nodes[0].Online);

            clock = now.AddSeconds(60);
            var heartbeat = store.Upsert(Msg("n1", "机房A", "203.0.113.10", 50001, "i1"), true);
            Check("纯心跳不涨 rev（否则每个心跳都要全量重同步）", !heartbeat.Changed);
            Eq("纯心跳后 rev 仍为 1", 1UL, store.Revision);

            clock = now.AddSeconds(120);
            var revBeforePort = store.Revision;
            var portChanged = store.Upsert(Msg("n1", "机房A", "203.0.113.10", 50002, "i1"), true);
            Check("端口变化使 rev 变化", portChanged.Changed);
            Eq("端口变化 rev+1", revBeforePort + 1, store.Revision);
            Eq("端口已更新", 50002, store.Nodes[0].Port);

            clock = now.AddSeconds(180);
            var nameChanged = store.Upsert(Msg("n1", "机房A-改名", "203.0.113.10", 50002, "i1"), true);
            Check("改名使 rev 变化", nameChanged.Changed);
            Eq("改名已生效", "机房A-改名", store.Nodes[0].Name);

            var rejected = store.Upsert(Msg("n2", "本机", "127.0.0.1", 50001, "i2"), true);
            Check("回环地址注册被拒绝", !rejected.Accepted);
            Eq("拒绝错误码为 loopback", RegistryErrorCodes.Loopback, rejected.ErrorCode);
            Check("拒绝原因可读", rejected.ErrorMessage.Contains("回环"), rejected.ErrorMessage);
            Eq("被拒节点不进清单", 1, store.Nodes.Count);

            Eq("快照含 1 个节点", 1, store.BuildSnapshot().Count);
            Eq("快照保留节点名", "机房A-改名", store.BuildSnapshot()[0].Name);

            var revBeforeHide = store.Revision;
            Check("中心隐藏生效", store.SetCenterHidden("n1", true));
            Eq("隐藏使 rev+1（影响快照内容）", revBeforeHide + 1, store.Revision);
            Eq("隐藏后快照为空", 0, store.BuildSnapshot().Count);
            Eq("隐藏不删除节点（中心界面仍可见）", 1, store.Nodes.Count);
            Check("重复设置同一值返回 false", !store.SetCenterHidden("n1", true));
            Check("取消隐藏", store.SetCenterHidden("n1", false));
            Eq("取消隐藏后快照恢复", 1, store.BuildSnapshot().Count);

            clock = now.AddSeconds(240);
            var revBeforeSelfHide = store.Revision;
            store.Upsert(Msg("n1", "机房A-改名", "203.0.113.10", 50002, "i1", distributable: false), true);
            Eq("节点自报不可分发使 rev+1", revBeforeSelfHide + 1, store.Revision);
            Eq("自报不可分发时快照为空", 0, store.BuildSnapshot().Count);

            // 恢复为可分发病，再验证 TTL 行为（离线只标记不删）
            clock = now.AddSeconds(300);
            store.Upsert(Msg("n1", "机房A-改名", "203.0.113.10", 50002, "i1"), true);
            Eq("恢复可分发病后快照恢复", 1, store.BuildSnapshot().Count);

            var lastSeen = now.AddSeconds(300);
            clock = lastSeen.AddSeconds(3 * 60 + 1);
            Check("超 TTL 刷新在线状态", store.RefreshOnlineState());
            Eq("离线节点仍在清单里", 1, store.Nodes.Count);
            Check("节点被标记离线", !store.Nodes[0].Online);
            Eq("离线节点仍进快照（不删链路）", 1, store.BuildSnapshot().Count);
            Check("已离线时再次刷新不产生变化", !store.RefreshOnlineState());

            clock = lastSeen.AddSeconds(3 * 60 + 2);
            var revBeforeBack = store.Revision;
            var backOnline = store.Upsert(Msg("n1", "机房A-改名", "203.0.113.10", 50002, "i1"), true);
            Check("心跳回来即在线（rev 变化）", backOnline.Changed);
            Eq("在线恢复使 rev+1", revBeforeBack + 1, store.Revision);
            Check("在线状态已恢复", store.Nodes[0].Online);

            var revBeforeReopen = store.Revision;
            var reopened = new RegistryStore(path, 60, () => clock);
            Eq("重开后 revision 保留（保持单调）", revBeforeReopen, reopened.Revision);
            Eq("重开后节点保留", 1, reopened.Nodes.Count);
            Eq("重开后节点名保留", "机房A-改名", reopened.Nodes[0].Name);
            Check("原子写不留 .tmp 残留", !File.Exists(path + ".tmp"));

            var revBeforeRemove = reopened.Revision;
            Check("删除节点成功", reopened.RemoveNode("n1"));
            Eq("删除使 rev+1", revBeforeRemove + 1, reopened.Revision);
            Eq("删除后清单为空", 0, reopened.Nodes.Count);
            Check("删除不存在的节点返回 false", !reopened.RemoveNode("nope"));
            Eq("删除后快照为空", 0, reopened.BuildSnapshot().Count);

            // 同一 nodeId 被两台机器同时使用（配置被复制）的疑似识别
            var dup = new RegistryStore(Path.Combine(dir, "dup.json"), 60, () => clock);
            dup.Upsert(Msg("x", "重复", "203.0.113.9", 60001, "i1"), true);
            dup.Upsert(Msg("x", "重复", "203.0.113.9", 60001, "i2"), true);
            Check("干净重启（进程标识换成新值）不算重复", !dup.Nodes[0].DuplicateSuspected);
            dup.Upsert(Msg("x", "重复", "203.0.113.9", 60001, "i1"), true);
            Check("进程标识来回跳 → 疑似 nodeId 重复", dup.Nodes[0].DuplicateSuspected);

            // 损坏的 registry.json 不应让中心起不来
            File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json", new UTF8Encoding(false));
            var broken = new RegistryStore(Path.Combine(dir, "broken.json"), 60, () => clock);
            Eq("清单损坏时从空清单启动（节点会在一个心跳内重新注册）", 0, broken.Nodes.Count);
            Eq("清单损坏时 rev 从 0 开始", 0UL, broken.Revision);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------- 快照应用 ----------------

    private static void TestRegistrySync()
    {
        Section("注册协议：快照应用（增/改/删/保留）");

        const string localNodeId = "self";

        var existing = new List<LinkConfig>
        {
            new() { Name = "手工链路", Host = "10.9.9.9", Port = 9999 },
            new() { Name = "机房B", Host = "203.0.113.20", Port = 50002, Tcp = true, Udp = true, Source = LinkSources.Registry, NodeId = "b" },
            new() { Name = "机房C", Host = "203.0.113.30", Port = 50003, Tcp = false, Udp = false, Source = LinkSources.Registry, NodeId = "c", LocalDisabled = true },
            new() { Name = "已消失的机房E", Host = "203.0.113.50", Port = 50005, Tcp = true, Udp = true, Source = LinkSources.Registry, NodeId = "e" }
        };

        var snapshot = new List<NodeSnapshot>
        {
            new() { NodeId = "self", Name = "本机", Ip = "203.0.113.10", Port = 50001 },
            new() { NodeId = "b", Name = "机房B", Ip = "203.0.113.20", Port = 50002, Tcp = true, Udp = true },
            new() { NodeId = "c", Name = "机房C", Ip = "203.0.113.30", Port = 50003, Tcp = true, Udp = true },
            new() { NodeId = "d", Name = "机房D", Ip = "203.0.113.40", Port = 50004, Tcp = true, Udp = false },
            new() { NodeId = "loop", Name = "回环节点", Ip = "127.0.0.1", Port = 50006, Tcp = true, Udp = true }
        };

        var outcome = RegistrySync.Apply(snapshot, localNodeId, existing);
        Eq("结果条数 = 1 条手工 + 3 条目标注册链路", 4, outcome.Links.Count);
        Eq("新增 1 条", 1, outcome.Added);
        Eq("更新 0 条", 0, outcome.Updated);
        Eq("删除 1 条（快照里已不存在的注册链路 E）", 1, outcome.Removed);
        Eq("跳过 1 个不可用地址（回环）", 1, outcome.SkippedUnusable);
        Check("有变化", outcome.HasChanges);

        Eq("手工链路在原位置原样保留", "10.9.9.9", outcome.Links[0].Host);
        Eq("手工链路来源仍是本地", LinkSources.Manual, outcome.Links[0].Source);
        Check("自己不进自己的链路", outcome.Links.All(l => l.NodeId != localNodeId));
        Check("回环节点没有被生成链路（客户端侧双保险）", outcome.Links.All(l => l.Host != "127.0.0.1"));
        Check("消失的注册链路被删除", outcome.Links.All(l => l.NodeId != "e"));

        var linkC = outcome.Links.First(l => l.NodeId == "c");
        Check("本地停测意图被同步保留（TCP/UDP 都被压住）", !linkC.Tcp && !linkC.Udp);
        Check("本地停测标记保留", linkC.LocalDisabled);
        Eq("本地停测链路名仍来自中心", "机房C", linkC.Name);

        var linkD = outcome.Links.First(l => l.NodeId == "d");
        Check("新增链路按对端宣告的协议生成（对端只宣告 TCP）", linkD.Tcp && !linkD.Udp);
        Eq("新增链路来源为注册中心", LinkSources.Registry, linkD.Source);
        Eq("新增链路记录了来源节点 id", "d", linkD.NodeId);

        // 中心把节点的可被探测协议改了 → 记为更新
        var updated = RegistrySync.Apply(
            new List<NodeSnapshot> { new() { NodeId = "b", Name = "机房B", Ip = "203.0.113.20", Port = 50002, Tcp = false, Udp = true } },
            localNodeId, outcome.Links);
        Eq("协议变化记为更新", 1, updated.Updated);
        Eq("快照里没有的注册链路被删除（C、D）", 2, updated.Removed);
        var linkB = updated.Links.First(l => l.NodeId == "b");
        Check("更新后 TCP 关闭、UDP 保留", !linkB.Tcp && linkB.Udp);
        Check("手工链路不受影响", updated.Links.Any(l => l.Source == LinkSources.Manual));

        // 空快照（中心清单被清空）→ 注册链路全删，手工链路不动
        var cleared = RegistrySync.Apply(Array.Empty<NodeSnapshot>(), localNodeId, updated.Links);
        Eq("空快照删除全部注册链路", 1, cleared.Removed);
        Eq("只剩手工链路", 1, cleared.Links.Count);
        Eq("剩的确实是手工链路", "手工链路", cleared.Links[0].Name);

        // 中心节点改为不可分发的地址（回环）→ 等价于从快照消失
        var loopOnly = RegistrySync.Apply(
            new List<NodeSnapshot> { new() { NodeId = "loop", Name = "回环节点", Ip = "::1", Port = 50006 } },
            localNodeId, cleared.Links);
        Eq("回环地址节点不生成链路", 0, loopOnly.Added);
        Eq("仍只有手工链路", 1, loopOnly.Links.Count);
        Check("无可分发目标时不算有变化", !loopOnly.HasChanges);
    }
}
