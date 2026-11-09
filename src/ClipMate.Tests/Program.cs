// 自测总入口：依次跑存储层、热键、图片三组用例
int failed = 0;

Console.WriteLine("==================== 存储层 ====================");
failed += StorageHarness.Run();

Console.WriteLine();
Console.WriteLine("==================== 热键 ====================");
failed += HotkeyHarness.Run();

Console.WriteLine();
Console.WriteLine("==================== 图片 ====================");
failed += ImageHarness.Run();

Console.WriteLine();
Console.WriteLine(failed == 0
    ? "全部测试通过 ✅"
    : $"共 {failed} 项失败 ❌");
return failed == 0 ? 0 : 1;
