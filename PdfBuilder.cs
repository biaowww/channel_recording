using System.Text;

namespace ChannelRecording;

/// <summary>
/// 把一组 JPEG 直接打包成 PDF：每张 slide 一页，JPEG 以 /DCTDecode 原样内嵌(无重编码)。
/// 手写最小 PDF，零依赖。
/// </summary>
internal static class PdfBuilder
{
    public static void Build(IReadOnlyList<SlideImage> slides, string outPath)
    {
        if (slides.Count == 0) return;
        int n = slides.Count;
        int objCount = 2 + 3 * n;              // 1=Catalog 2=Pages，之后每页 page/content/image 三个对象
        var offsets = new long[objCount + 1];  // 1-based 偏移表

        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);

        void A(string s) { var b = Encoding.Latin1.GetBytes(s); fs.Write(b, 0, b.Length); }

        // 文件头（含二进制标记行，确保被当作二进制文件）
        A("%PDF-1.7\n");
        fs.Write(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A }, 0, 6);

        // 1: Catalog
        offsets[1] = fs.Position;
        A("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        // 2: Pages
        var kids = new StringBuilder();
        for (int i = 0; i < n; i++) kids.Append(3 + i * 3).Append(" 0 R ");
        offsets[2] = fs.Position;
        A($"2 0 obj\n<< /Type /Pages /Kids [{kids.ToString().Trim()}] /Count {n} >>\nendobj\n");

        for (int i = 0; i < n; i++)
        {
            int pageNum = 3 + i * 3, contentNum = pageNum + 1, imageNum = pageNum + 2;
            int w = slides[i].Width, h = slides[i].Height;
            byte[] jpeg = File.ReadAllBytes(slides[i].Path);

            // page：页面尺寸=图片像素(单位pt)，图片铺满
            offsets[pageNum] = fs.Position;
            A($"{pageNum} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] " +
              $"/Resources << /XObject << /Im0 {imageNum} 0 R >> >> /Contents {contentNum} 0 R >>\nendobj\n");

            // content：把图片缩放铺满整页
            string content = $"q\n{w} 0 0 {h} 0 0 cm\n/Im0 Do\nQ\n";
            int clen = Encoding.Latin1.GetByteCount(content);
            offsets[contentNum] = fs.Position;
            A($"{contentNum} 0 obj\n<< /Length {clen} >>\nstream\n");
            A(content);
            A("endstream\nendobj\n");

            // image：DCTDecode 原样内嵌 JPEG
            offsets[imageNum] = fs.Position;
            A($"{imageNum} 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} " +
              $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
            fs.Write(jpeg, 0, jpeg.Length);
            A("\nendstream\nendobj\n");
        }

        // xref
        long xref = fs.Position;
        A($"xref\n0 {objCount + 1}\n");
        A("0000000000 65535 f \n");
        for (int num = 1; num <= objCount; num++)
            A($"{offsets[num]:D10} 00000 n \n");

        A($"trailer\n<< /Size {objCount + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
