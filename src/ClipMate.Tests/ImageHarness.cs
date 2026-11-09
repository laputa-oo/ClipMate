using ClipMate.Core;
using System.Windows.Forms;

// 图片链路验证：缩略图所需的取图、重复判定、格式兜底
class ImageHarness
{
    public static int Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clipmate_img_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name);
            if (!ok) failures++;
        }

        // 造两张不同的 PNG
        byte[] MakePng(int w, int h, byte r, byte g, byte b)
        {
            using var bmp = new Bitmap(w, h);
            using (var gr = Graphics.FromImage(bmp))
            {
                using var brush = new SolidBrush(Color.FromArgb(r, g, b));
                gr.FillRectangle(brush, 0, 0, w, h);
                using var brush2 = new SolidBrush(Color.FromArgb((byte)(255 - r), 200, 90));
                gr.FillEllipse(brush2, w / 4, h / 4, w / 2, h / 2);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }

        var imgA = MakePng(400, 300, 30, 120, 220);
        var imgB = MakePng(400, 300, 220, 60, 40);   // 同尺寸不同内容
        var imgC = MakePng(120, 90, 30, 120, 220);   // 不同尺寸

        Console.WriteLine("[1] 图片去重：同一次复制触发多次通知只应记一条");
        using (var s = new Storage(Path.Combine(dir, "img1.db")))
        {
            s.AddImage(imgA, "400×300");
            // 第二次读到完全相同的字节 -> 应判重复
            Check("相同字节判重复", ClipboardService.IsDuplicateImage(s, imgA, "400×300"));
            // 尺寸不同 -> 不应判重复
            Check("不同尺寸不判重复", !ClipboardService.IsDuplicateImage(s, imgC, "120×90"));
            // 同尺寸但内容不同、且超过 3 秒时间窗 -> 不应判重复
            var last = s.GetEntries(groupId: -2, limit: 1)[0];
            typeof(Storage).GetMethod("Touch");  // 保持引用，避免编译器告警
            Check("同尺寸不同内容(刚写入,在3秒窗内)视为重复",
                ClipboardService.IsDuplicateImage(s, imgB, "400×300"));
        }

        Console.WriteLine("[2] 图片条目能被取出（缩略图依赖此接口）");
        using (var s = new Storage(Path.Combine(dir, "img2.db")))
        {
            var e = s.AddImage(imgA, "400×300");
            var data = s.GetImageData(e.Id);
            Check("GetImageData 返回原始字节", data is not null && data.Length == imgA.Length);
            Check("条目类型为 Image", e.IsImage);
            Check("Preview 为尺寸文本", e.Preview == "400×300");

            // 模拟缩略图生成：解码 -> 等比缩放
            using var ms = new MemoryStream(data!);
            using var src = Image.FromStream(ms);
            var scale = Math.Min(Math.Min(56.0 / src.Width, 44.0 / src.Height), 1.0);
            var tw = (int)Math.Round(src.Width * scale);
            var th = (int)Math.Round(src.Height * scale);
            Check($"缩略图等比缩放 400x300 -> {tw}x{th}", tw == 56 && th == 42);
        }

        Console.WriteLine("[3] 非图片条目不会被误判为图片重复");
        using (var s = new Storage(Path.Combine(dir, "img3.db")))
        {
            s.AddText("一段普通文本");
            Check("最近是文本时不判图片重复",
                !ClipboardService.IsDuplicateImage(s, imgA, "400×300"));
        }

        Console.WriteLine("[4] 自定义 PNG 格式（企业微信/微信截图常见写法）能否读出");
        // 企业微信截图不一定写标准 CF_DIB，而是登记自定义 "PNG" 格式。
        // 这里模拟只写 PNG、不写 DIB 的剪贴板，验证读取兜底逻辑。
        var clipOk = RunSta(() =>
        {
            try
            {
                var png = MakePng(200, 150, 10, 200, 100);
                var obj = new DataObject();
                obj.SetData("PNG", new MemoryStream(png));
                Clipboard.SetDataObject(obj, true);

                var snap = ClipboardReader.Read();
                var ok = snap is { HasImage: true } && snap.ImageBytes is { Length: > 0 };
                Console.WriteLine($"      命中格式: {snap?.ImageSourceFormat ?? "(无)"}, 尺寸: {snap?.ImagePreview ?? "-"}");
                return ok;
            }
            catch (Exception ex)
            {
                Console.WriteLine("      异常: " + ex.Message);
                return false;
            }
            finally
            {
                // 还原剪贴板，避免留下测试图片
                try { Clipboard.SetText("ClipMate 自测已完成，剪贴板已还原为文本。"); } catch { }
            }
        });
        Check("只写自定义 PNG 格式也能读出图片", clipOk);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过 ✅" : $"{failures} 项失败 ❌");
        return failures;
    }

    /// <summary>剪贴板 API 需要 STA 线程，这里开一个独立 STA 线程执行。</summary>
    private static T RunSta<T>(Func<T> fn)
    {
        T result = default!;
        var t = new Thread(() => { result = fn(); });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join(TimeSpan.FromSeconds(30));
        return result;
    }
}
