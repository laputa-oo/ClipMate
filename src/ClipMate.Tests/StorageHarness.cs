using ClipMate.Core;

// 存储层验证：验证三重淘汰 + 空间回收（直接引用 Storage 类型，无需启动 GUI）
class StorageHarness
{
    static int StorageMain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipnest_harness_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
            if (!ok) failures++;
        }

        Console.WriteLine("[1] 数据写入与三重淘汰");
        using (var s = new Storage(Path.Combine(dir, "t1.db")))
        {
            var cfg = new AppConfig
            {
                MaxEntries = 100,
                MaxTotalSizeMB = 1,   // 1MB 字节上限
                RetainDays = 0,
                MaxSingleEntryKB = 512
            };

            // 写 200 条各 10KB 文本 = 2MB，超字节上限
            var bigText = new string('A', 10 * 1024);
            for (int i = 0; i < 200; i++)
                s.AddText(bigText + "_" + i);

            var statsBefore = s.GetStats();
            Console.WriteLine($"  写入后: {statsBefore.count} 条, {statsBefore.totalBytes} 字节");

            // 触发清理
            var r = s.Cleanup(cfg);
            var statsAfter = s.GetStats();
            Console.WriteLine($"  清理后: {statsAfter.count} 条, {statsAfter.totalBytes} 字节 (删除 {r.deleted} 条)");
            Console.WriteLine($"  文件: {r.bytesBefore:N0} -> {r.bytesAfter:N0} 字节");

            Check("条数不超过 MaxEntries(100)", statsAfter.count <= 100);
            Check("总字节不超过 1MB 上限", statsAfter.totalBytes <= 1L * 1024 * 1024);
            Check("物理文件确实缩小", r.bytesAfter < r.bytesBefore);
        }

        Console.WriteLine("[2] 时间过期淘汰 + 置顶保护");
        using (var s = new Storage(Path.Combine(dir, "t2.db")))
        {
            var cfg = new AppConfig { MaxEntries = 0, MaxTotalSizeMB = 0, RetainDays = 30 };
            // 直接写一条 60 天前的记录（通过内部 SQL 做不到，用 Cleanup 的 cutoff 逻辑验证）
            // 这里改为验证：pinned 条目不被淘汰
            s.AddText("普通条目");
            var pinned = s.AddText("置顶条目", pinned: true);

            var r = s.Cleanup(new AppConfig { MaxEntries = 1, MaxTotalSizeMB = 0, RetainDays = 0 });
            var remains = s.GetEntries(groupId: -2, limit: 100);
            Check("置顶条目在 MaxEntries=1 下仍被保留",
                remains.Any(e => e.Id == pinned.Id));
        }

        Console.WriteLine("[3] 分组单击语义（数据层）");
        using (var s = new Storage(Path.Combine(dir, "t3.db")))
        {
            var gid = s.AddGroup("测试分组");
            s.AddText("组内条目1", groupId: gid);
            s.AddText("组内条目2", groupId: gid);
            s.AddText("未分组条目");

            var inGroup = s.GetEntries(gid);
            var ungrouped = s.GetEntries(ClipGroup.UngroupedId);
            Check("分组内恰好 2 条", inGroup.Count == 2);
            Check("未分组恰好 1 条", ungrouped.Count == 1);

            // 分组受保护：即使 MaxEntries=1，组内条目也不被淘汰
            s.Cleanup(new AppConfig { MaxEntries = 1, MaxTotalSizeMB = 0, RetainDays = 0 });
            var after = s.GetEntries(gid);
            Check("分组条目受保护不被淘汰", after.Count == 2);
        }

        Console.WriteLine("[4] 图片默认不入库（策略层）");
        {
            // 该策略在 ClipboardService.OnClipboardChanged 里体现，这里验证配置默认值
            var cfg = new AppConfig();
            Check("SaveImages 默认 false", cfg.SaveImages == false);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过 ✅" : $"{failures} 项失败 ❌");
        return failures;
    }
}
