using System.IO.Compression;
using System.Text;

namespace ChannelRecording;

/// <summary>把一组图片塞进一个最小 .docx（OpenXML = zip）：每张 slide 一段内嵌图片，横向 A4。零依赖。</summary>
internal static class DocxBuilder
{
    private const long EmuPerPx = 9525;           // 1px @96dpi = 9525 EMU
    // 横向 A4 去掉 720 twip 页边距后的可用尺寸（twip*635=EMU）：
    private const long MaxWidthEmu  = 9_770_000;  // (16838-1440)*635
    private const long MaxHeightEmu = 6_640_000;  // (11906-1440)*635

    public static void Build(IReadOnlyList<SlideImage> slides, string outPath)
    {
        if (slides.Count == 0) return;

        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        Write(zip, "[Content_Types].xml", ContentTypes());
        Write(zip, "_rels/.rels", RootRels());
        Write(zip, "word/_rels/document.xml.rels", DocumentRels(slides.Count));
        Write(zip, "word/document.xml", DocumentXml(slides));

        for (int i = 0; i < slides.Count; i++)
        {
            var entry = zip.CreateEntry($"word/media/image{i + 1}.jpg", CompressionLevel.NoCompression);
            using var es = entry.Open();
            using var src = File.OpenRead(slides[i].Path);
            src.CopyTo(es);
        }
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var s = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    private static string ContentTypes() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Default Extension="jpg" ContentType="image/jpeg"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
        </Types>
        """;

    private static string RootRels() =>
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
        </Relationships>
        """;

    private static string DocumentRels(int count)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.AppendLine("""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
        for (int i = 1; i <= count; i++)
            sb.AppendLine($"""  <Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image{i}.jpg"/>""");
        sb.AppendLine("</Relationships>");
        return sb.ToString();
    }

    private static string DocumentXml(IReadOnlyList<SlideImage> slides)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        sb.AppendLine("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">""");
        sb.AppendLine("  <w:body>");

        for (int i = 0; i < slides.Count; i++)
        {
            (long cx, long cy) = FitEmu(slides[i].Width, slides[i].Height);
            int id = i + 1;
            sb.AppendLine("    <w:p><w:r><w:drawing>");
            sb.AppendLine($"""      <wp:inline distT="0" distB="0" distL="0" distR="0">""");
            sb.AppendLine($"""        <wp:extent cx="{cx}" cy="{cy}"/>""");
            sb.AppendLine("""        <wp:effectExtent l="0" t="0" r="0" b="0"/>""");
            sb.AppendLine($"""        <wp:docPr id="{id}" name="slide{id}"/>""");
            sb.AppendLine("""        <wp:cNvGraphicFramePr><a:graphicFrameLocks noChangeAspect="1"/></wp:cNvGraphicFramePr>""");
            sb.AppendLine("""        <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">""");
            sb.AppendLine("""          <pic:pic>""");
            sb.AppendLine($"""            <pic:nvPicPr><pic:cNvPr id="{id}" name="slide{id}.jpg"/><pic:cNvPicPr/></pic:nvPicPr>""");
            sb.AppendLine($"""            <pic:blipFill><a:blip r:embed="rId{id}"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>""");
            sb.AppendLine($"""            <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="{cx}" cy="{cy}"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr>""");
            sb.AppendLine("""          </pic:pic>""");
            sb.AppendLine("""        </a:graphicData></a:graphic>""");
            sb.AppendLine("      </wp:inline>");
            sb.AppendLine("    </w:drawing></w:r></w:p>");
        }

        // 横向 A4
        sb.AppendLine("""    <w:sectPr><w:pgSz w:w="16838" w:h="11906" w:orient="landscape"/><w:pgMar w:top="720" w:right="720" w:bottom="720" w:left="720" w:header="0" w:footer="0" w:gutter="0"/></w:sectPr>""");
        sb.AppendLine("  </w:body>");
        sb.AppendLine("</w:document>");
        return sb.ToString();
    }

    private static (long cx, long cy) FitEmu(int wpx, int hpx)
    {
        long cx = (long)wpx * EmuPerPx, cy = (long)hpx * EmuPerPx;
        if (cx > MaxWidthEmu)  { cy = cy * MaxWidthEmu / cx;  cx = MaxWidthEmu; }   // 限宽
        if (cy > MaxHeightEmu) { cx = cx * MaxHeightEmu / cy; cy = MaxHeightEmu; }  // 再限高，保持比例
        return (cx, cy);
    }
}
