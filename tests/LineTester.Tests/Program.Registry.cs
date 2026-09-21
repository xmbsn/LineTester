using System.IO;
using System.Net;
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

        var frame = RegistryProtocol.Encode(RegistryProtocol.TypeRegister, "abc123", "{\"a\":1}");
        Eq("帧格式为 LT1|TYPE|MAC|payload\\n", "LT1|REG|abc123|{\"a\":1}\n", Encoding.UTF8.GetString(frame));
        Check("协议帧被识别", RegistryProtocol.LooksLikeProtocolMessage(frame, frame.Length));
        Check("帧可解析", RegistryProtocol.TryDecode(frame, frame.Length, out var type, out var mac, out var payload));
        Eq("类型解析正确", "REG", type);
        Eq("mac 是独立一段（不能放进 payload，否则签名覆盖自身、永远校验不过）", "abc123", mac);
        Eq("payload 原样保留（HMAC 依赖原文，不能重新序列化）", "{\"a\":1}", payload);

        const string secret = "preshared";
        var signed = RegistryProtocol.EncodeSigned(secret, RegistryProtocol.TypeSync, "{\"n\":1}");
        Check("签名帧可解析",
            RegistryProtocol.TryDecode(signed, signed.Length, out var signedType, out var signedMac, out var signedPayload));
        Check("签名帧验签通过", RegistryProtocol.VerifyFrame(secret, signedType, signedMac, signedPayload));
        Check("换密钥后验签失败", !RegistryProtocol.VerifyFrame("other", signedType, signedMac, signedPayload));
        Check("改 payload 后验签失败", !RegistryProtocol.VerifyFrame(secret, signedType, signedMac, signedPayload + "x"));
        Check("改类型后验签失败", !RegistryProtocol.VerifyFrame(secret, "ACK", signedMac, signedPayload));

        var probe = Encoding.UTF8.GetBytes("42|3f2a9c1d4b5e6f708192a3b4c5d6e7f8|v2");
        Check("探测包（数字开头）不被误判为协议消息",
            !RegistryProtocol.LooksLikeProtocolMessage(probe, probe.Length));
        var legacyProbe = Encoding.UTF8.GetBytes("3f2a9c1d4b5e6f708192a3b4c5d6e7f8");
        Check("老版本探测包（GUID 开头）不被误判为协议消息",
            !RegistryProtocol.LooksLikeProtocolMessage(legacyProbe, legacyProbe.Length));

        Check("缺 mac 分隔符的帧被拒绝",
            !RegistryProtocol.TryDecode(Encoding.UTF8.GetBytes("LT1|REG\n"), 8, out _, out _, out _));
        Check("缺 payload 分隔符的帧被拒绝",
            !RegistryProtocol.TryDecode(Encoding.UTF8.GetBytes("LT1|REG|mac\n"), 12, out _, out _, out _));
        Check("空类型的帧被拒绝",
            !RegistryProtocol.TryDecode(Encoding.UTF8.GetBytes("LT1||m|{}\n"), 10, out _, out _, out _));
        Check("空输入被拒绝", !RegistryProtocol.TryDecode(Array.Empty<byte>(), 0, out _, out _, out _));

        var tricky = "{\"name\":\"a|b\\nc\",\"n\":1}";
        var trickyFrame = RegistryProtocol.Encode(RegistryProtocol.TypeRegister, "m", tricky);
        Check("payload 含 | 与转义换行时往返正确",
            RegistryProtocol.TryDecode(trickyFrame, trickyFrame.Length, out var trickyType, out _, out var trickyPayload)
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
            Check("被删除的节点进入移除名单（心跳不会把它加回来）", reopened.IsRemoved("n1"));
            Check("未知 nodeId 也会被记入名单（可预先封禁）", reopened.RemoveNode("nope"));
            Check("重复移除同一 nodeId 返回 false", !reopened.RemoveNode("nope"));
            Eq("删除后快照为空", 0, reopened.BuildSnapshot().Count);

            // 被移除的节点再次注册必须被拒绝（否则"中心删除"会被下一拍心跳撤销）
            var blocked = reopened.Upsert(Msg("n1", "机房A-改名", "203.0.113.10", 50002, "i1"), true);
            Check("已被移除的节点注册被拒绝", !blocked.Accepted);
            Eq("拒绝错误码为 removed", RegistryErrorCodes.Removed, blocked.ErrorCode);
            Eq("拒绝后清单仍为空", 0, reopened.Nodes.Count);

            // 显式恢复后才允许重新注册
            Check("恢复后从名单移除", reopened.RestoreNode("n1") && !reopened.IsRemoved("n1"));
            Check("恢复不存在的 nodeId 返回 false", !reopened.RestoreNode("never-removed"));
            Check("恢复后可以重新注册", reopened.Upsert(Msg("n1", "机房A-改名", "203.0.113.10", 50002, "i1"), true).Accepted);
            Eq("恢复后清单恢复", 1, reopened.Nodes.Count);

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

    // ---------------- 中心服务（认证/幂等/限流/分块） ----------------

    private static void TestRegistryCenter()
    {
        Section("注册协议：中心服务（认证/幂等/限流/分块）");

        var dir = Path.Combine(AppContext.BaseDirectory, ".testtmp", "ctr_" + Guid.NewGuid().ToString("N"));
        const string secret = "preshared";
        var clock = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        try
        {
            var store = new RegistryStore(Path.Combine(dir, "registry.json"), 60, () => clock);
            var center = new RegistryCenterService(store, secret, 60, () => clock);
            var remote = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 41234);

            byte[] Frame(string nodeId, string ip, int port, ulong rev, string reqId, string nonce,
                string frameSecret = secret, long? ts = null, bool tcp = true, bool udp = true)
            {
                var payload = RegistryProtocol.Serialize(new RegisterMessage
                {
                    ReqId = reqId,
                    NodeId = nodeId,
                    Name = "节点" + nodeId,
                    Ip = ip,
                    Port = port,
                    Tcp = tcp,
                    Udp = udp,
                    Distributable = true,
                    Instance = "i-" + nodeId,
                    Rev = rev,
                    Ts = ts ?? RegistryAuth.ToUnixSeconds(clock),
                    Nonce = nonce
                });

                return RegistryProtocol.EncodeSigned(frameSecret, RegistryProtocol.TypeRegister, payload);
            }

            IReadOnlyList<byte[]> Send(byte[] frame)
                => center.HandleAsync(frame, frame.Length, remote, CancellationToken.None).GetAwaiter().GetResult();

            (AckMessage? Ack, ErrorMessage? Error) Parse(IReadOnlyList<byte[]> frames)
            {
                AckMessage? ack = null;
                ErrorMessage? error = null;

                foreach (var frame in frames)
                {
                    if (!RegistryProtocol.TryDecode(frame, frame.Length, out var type, out _, out var payload)) continue;
                    if (type == RegistryProtocol.TypeAck) ack = RegistryProtocol.Deserialize<AckMessage>(payload);
                    else if (type == RegistryProtocol.TypeError) error = RegistryProtocol.Deserialize<ErrorMessage>(payload);
                }

                return (ack, error);
            }

            var first = Parse(Send(Frame("a", "203.0.113.11", 50001, 0, "r1", "n1")));
            Check("首次注册返回 ACK", first.Ack is not null, first.Error?.Code);
            Check("首次注册判定有变化", first.Ack!.Changed);
            Eq("ACK 带回中心 revision", 1UL, first.Ack.Rev);
            Eq("ACK 带完整快照", 1, first.Ack.Nodes!.Count);
            Check("ACK 回显中心看到的源地址（供与手填地址交叉核对）",
                first.Ack.SelfCheck is { SeenIp: "198.51.100.7", SeenPort: 41234 });

            var repeat = Parse(Send(Frame("a", "203.0.113.11", 50001, 0, "r1", "n1")));
            Eq("重传同一 reqId 不重复递增 revision", 1UL, store.Revision);
            Check("重传返回缓存应答（幂等）", repeat.Ack is not null && repeat.Ack.Rev == 1);

            var badMac = Parse(Send(Frame("a", "203.0.113.11", 50001, 1, "r2", "n2", frameSecret: "wrong-secret")));
            Check("签名不匹配时返回 ERR", badMac.Error is not null);
            Eq("错误码为 auth", RegistryErrorCodes.Auth, badMac.Error!.Code);
            Check("签名不匹配时不返回 ACK", badMac.Ack is null);

            var stale = Parse(Send(Frame("a", "203.0.113.11", 50001, 1, "r3", "n3",
                ts: RegistryAuth.ToUnixSeconds(clock) - 400)));
            Eq("时间戳偏差过大 → auth", RegistryErrorCodes.Auth, stale.Error!.Code);

            var replay = Parse(Send(Frame("a", "203.0.113.11", 50001, 1, "r4", "n1")));
            Eq("重复 nonce → auth", RegistryErrorCodes.Auth, replay.Error!.Code);

            var loopback = Parse(Send(Frame("b", "127.0.0.1", 50001, 0, "r5", "n5")));
            Eq("回环地址 → loopback", RegistryErrorCodes.Loopback, loopback.Error!.Code);
            Eq("回环地址不进清单", 1, store.Nodes.Count);

            var unchanged = Parse(Send(Frame("a", "203.0.113.11", 50001, 1, "r6", "n6")));
            Check("rev 一致时 changed=false（稳态只回 1 个包）", unchanged.Ack is { Changed: false });
            Check("短路应答不带快照", unchanged.Ack!.Nodes is null);

            ErrorMessage? throttled = null;
            for (var i = 0; i < 30; i++)
            {
                var rate = Parse(Send(Frame("a", "203.0.113.11", 50001, 1, $"rate{i}", $"rn{i}")));
                if (rate.Error?.Code != RegistryErrorCodes.Throttled) continue;

                throttled = rate.Error;
                break;
            }

            Check("超过请求限流阈值 → throttled", throttled is not null, throttled?.Code);
            Check("限流应答带 retryAfter", throttled is { RetryAfter: > 0 });

            // ---- 分块：塞满节点让快照超过单包上限 ----
            clock = clock.AddSeconds(60);
            for (var i = 0; i < 14; i++)
            {
                Send(Frame($"bulk{i}", $"203.0.113.{100 + i}", 51000 + i, 0, $"rb{i}", $"nb{i}"));
            }

            Eq("批量注册后清单变大", 15, store.Nodes.Count);

            clock = clock.AddSeconds(60);
            var chunked = Send(Frame("a", "203.0.113.11", 50001, 1, "rc1", "nc1"));

            var parts = new List<PartMessage>();
            foreach (var frame in chunked)
            {
                if (!RegistryProtocol.TryDecode(frame, frame.Length, out var type, out _, out var payload)) continue;
                if (type == RegistryProtocol.TypePart) parts.Add(RegistryProtocol.Deserialize<PartMessage>(payload)!);
            }

            Check("大快照走分块传输", parts.Count > 1, $"块数={parts.Count}");
            Check("每个分块都带 reqId（客户端据此关联本次请求）",
                parts.All(p => p.ReqId == "rc1"), parts.FirstOrDefault()?.ReqId);
            Eq("分块序号从 0 连续", string.Join(",", parts.Select(p => p.I).OrderBy(i => i)),
                string.Join(",", Enumerable.Range(0, parts.Count)));
            Eq("分块声明总数一致", parts.Count, parts[0].N);
            Check("分块内容可拼回合法 JSON",
                RegistryChunking.TryJoin(parts.OrderBy(p => p.I).Select(p => (string?)p.Data).ToList(), out var assembled)
                && RegistryProtocol.Deserialize<AckMessage>(assembled) is not null);

            // 只声明收到第 0 块 → 应只补发其余块
            var transferId = parts[0].XferId;
            var partialBitmap = RegistryChunking.SetBit(RegistryChunking.EmptyBitmap(parts.Count), 0);
            var partialPayload = RegistryProtocol.Serialize(new PartAckMessage { XferId = transferId, Bitmap = partialBitmap });
            var partialFrame = RegistryProtocol.EncodeSigned(secret, RegistryProtocol.TypePartAck, partialPayload);
            var resend = center.HandleAsync(partialFrame, partialFrame.Length, remote, CancellationToken.None)
                .GetAwaiter().GetResult();

            var resent = new List<int>();
            foreach (var frame in resend)
            {
                if (!RegistryProtocol.TryDecode(frame, frame.Length, out var type, out _, out var payload)) continue;
                if (type == RegistryProtocol.TypePart) resent.Add(RegistryProtocol.Deserialize<PartMessage>(payload)!.I);
            }

            Eq("只补发缺失块", parts.Count - 1, resent.Count);
            Check("不含已确认的第 0 块", !resent.Contains(0), string.Join(",", resent));

            // 未验签的位图必须被忽略（否则可被用来让中心把清单发到任意地址）
            var forgedFrame = RegistryProtocol.EncodeSigned("wrong-secret", RegistryProtocol.TypePartAck, partialPayload);
            var forged = center.HandleAsync(forgedFrame, forgedFrame.Length, remote, CancellationToken.None)
                .GetAwaiter().GetResult();
            Eq("未验签的位图被忽略", 0, forged.Count);

            // 全部确认 → 不再补发
            var fullBitmap = RegistryChunking.EmptyBitmap(parts.Count);
            for (var i = 0; i < parts.Count; i++) fullBitmap = RegistryChunking.SetBit(fullBitmap, i);
            var fullPayload = RegistryProtocol.Serialize(new PartAckMessage { XferId = transferId, Bitmap = fullBitmap });
            var fullFrame = RegistryProtocol.EncodeSigned(secret, RegistryProtocol.TypePartAck, fullPayload);
            var completed = center.HandleAsync(fullFrame, fullFrame.Length, remote, CancellationToken.None)
                .GetAwaiter().GetResult();
            Eq("全部确认后不再补发", 0, completed.Count);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---------------- 三节点端到端 ----------------

    private static async Task TestRegistryEndToEndAsync()
    {
        Section("注册协议：三节点端到端（自动生成链路 / 删除传播 / 兼容）");

        var root = Path.Combine(AppContext.BaseDirectory, ".testtmp", "e2e_" + Guid.NewGuid().ToString("N"));
        var logDir = Path.Combine(root, "logs");
        const string secret = "e2e-secret";

        var centerPort = FindBindablePort();
        var legacyPort = FindBindablePort();

        var store = new RegistryStore(Path.Combine(root, "registry.json"), 60);
        var centerService = new RegistryCenterService(store, secret, 60);

        using var centerServer = new ProbeServer(centerPort);
        var handled = 0;
        Exception? handlerError = null;
        centerServer.ProtocolHandler = (request, length, remote, ct) =>
        {
            Interlocked.Increment(ref handled);
            try
            {
                return centerService.HandleAsync(request, length, remote, ct);
            }
            catch (Exception ex)
            {
                // 被动端的 catch 会吞掉处理器异常（不影响探测），这里显式留证
                handlerError ??= ex;
                throw;
            }
        };
        centerServer.Start();

        // 未启用注册协议的节点（等价旧版本）：没有 protocol handler → 协议消息被原样回显
        using var legacyServer = new ProbeServer(legacyPort);
        legacyServer.Start();

        using var nodeA = new RegistryTestNode("a", "203.0.113.11", 50001, centerPort, secret, logDir);
        using var nodeB = new RegistryTestNode("b", "203.0.113.12", 50002, centerPort, secret, logDir);

        try
        {
            Check("A 注册成功", await nodeA.Client.RegisterNowAsync(), nodeA.Client.Status.LastError);
            Check("中心处理器没有抛异常", handlerError is null, handlerError?.ToString());
            Check("协议消息被端口分流到中心服务", handled > 0,
                $"centerPort={centerPort} 收到 {handled} 帧；中心 rev={store.Revision}，清单 {store.Nodes.Count} 个节点");
            Check("B 注册成功", await nodeB.Client.RegisterNowAsync(), nodeB.Client.Status.LastError);
            Eq("中心清单里有 2 个节点", 2, store.Nodes.Count);

            Check("A 再次注册以拿到含 B 的最新快照", await nodeA.Client.RegisterNowAsync());

            var links = nodeA.Snapshot();
            Eq("A 自动生成 1 条链路（对端 B）", 1, links.Count);
            Eq("链路地址来自 B 的注册值", "203.0.113.12", links[0].Host);
            Eq("链路端口来自 B 的注册值", 50002, links[0].Port);
            Eq("链路来源标记为注册中心", LinkSources.Registry, links[0].Source);
            Eq("链路记录了来源节点 id", "b", links[0].NodeId);
            Check("A 不会把自己加成链路", links.All(l => l.NodeId != "a"));
            Check("A 记录了中心 revision", nodeA.Client.Status.Revision > 0, nodeA.Client.Status.Revision.ToString());
            Check("记录了中心看到的本端源地址（与手填地址交叉核对）",
                nodeA.Client.Status.SeenFrom?.StartsWith("127.0.0.1:") == true, nodeA.Client.Status.SeenFrom);

            // ---- 删除传播：中心移除 B，且**心跳不会把它加回来** ----
            Check("中心移除节点 B", store.RemoveNode("b"));
            Check("B 的再次注册被拒绝", !await nodeB.Client.RegisterNowAsync());
            Check("B 收到 removed 错误",
                nodeB.Client.Status.LastError?.Contains(RegistryErrorCodes.Removed) == true,
                nodeB.Client.Status.LastError);
            Eq("中心清单仍为 1 个节点（心跳没能复活它）", 1, store.Nodes.Count);

            Check("A 同步成功", await nodeA.Client.RegisterNowAsync());
            Eq("删除传播到 A：B 的链路消失", 0, nodeA.Snapshot().Count);

            // ---- 恢复后重新出现 ----
            Check("中心恢复节点 B", store.RestoreNode("b"));
            Check("B 恢复后注册成功", await nodeB.Client.RegisterNowAsync(), nodeB.Client.Status.LastError);
            Check("A 同步成功", await nodeA.Client.RegisterNowAsync());
            Eq("恢复后 A 重新生成 B 的链路", 1, nodeA.Snapshot().Count);

            // ---- 兼容：对端不是注册中心（旧版本纯回显） ----
            using var legacyNode = new RegistryTestNode("legacy", "203.0.113.99", 50009, legacyPort, secret, logDir);
            Check("打到非注册中心地址时注册失败", !await legacyNode.Client.RegisterNowAsync());
            Check("明确报出“不是注册中心”而不是只报超时",
                legacyNode.Client.Status.LastError?.Contains("不是注册中心") == true,
                legacyNode.Client.Status.LastError);

            // ---- 兼容：装了协议处理器也不影响探测回显 ----
            var probeClient = new ProbeClient();
            var probeLink = new LinkConfig { Name = "端点探测", Host = "127.0.0.1", Port = centerPort };
            var tcpProbe = await probeClient.ProbeAsync(probeLink, "TCP", 1000, CancellationToken.None);
            var udpProbe = await probeClient.ProbeAsync(probeLink, "UDP", 1000, CancellationToken.None);
            Check("装了协议处理器后 TCP 探测仍成功", tcpProbe is { Lost: false }, tcpProbe?.Info);
            Check("装了协议处理器后 UDP 探测仍成功", udpProbe is { Lost: false }, udpProbe?.Info);
            Check("探测结果仍带对端看到的源地址",
                tcpProbe?.SeenFrom.StartsWith("127.0.0.1:") == true, tcpProbe?.SeenFrom);

            // ---- 协议日志 ----
            var logFiles = Directory.Exists(logDir)
                ? Directory.GetFiles(logDir, "registry.log", SearchOption.AllDirectories)
                : Array.Empty<string>();
            Check("写入按天目录下的 registry.log", logFiles.Length > 0, logDir);

            if (logFiles.Length > 0)
            {
                var content = File.ReadAllText(logFiles[0]);
                Check("协议日志不含密钥", !content.Contains(secret), content.Split('\n').FirstOrDefault());
                Check("协议日志记录了快照事件", content.Contains("清单快照"), content.Split('\n').FirstOrDefault());
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>端到端用的测试节点：一个客户端 + 一份本地链路清单（订阅快照并套用）。</summary>
    private sealed class RegistryTestNode : IDisposable
    {
        private readonly object _gate = new();
        private List<LinkConfig> _links = new();

        public RegistryTestNode(string nodeId, string publicHost, int publicPort, int centerPort,
            string secret, string logDirectory)
        {
            Client = new RegistryClient(new RegistryClientOptions
            {
                NodeId = nodeId,
                NodeName = "节点" + nodeId,
                PublicHost = publicHost,
                PublicPort = publicPort,
                CenterHost = "127.0.0.1",
                CenterPort = centerPort,
                Secret = secret,
                HeartbeatSeconds = 15,
                SyncSeconds = 15,
                RetryMs = 200,
                MaxRetries = 2,
                LogDirectory = logDirectory
            });

            Client.SnapshotReceived += nodes =>
            {
                lock (_gate)
                {
                    _links = RegistrySync.Apply(nodes, nodeId, _links).Links;
                }
            };

            Client.Start();
        }

        public RegistryClient Client { get; }

        public List<LinkConfig> Snapshot()
        {
            lock (_gate)
            {
                return new List<LinkConfig>(_links);
            }
        }

        public void Dispose() => Client.Dispose();
    }
}
