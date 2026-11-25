using System.IO;
using System.IO.Compression;
using System.Text;

namespace ProductionStationTool.App.Utilities;

/// <summary>成功写入并回读一致的 SN 记录。</summary>
public sealed record SnRecord(string Sn, DateTime Time);

/// <summary>
/// 将成功 SN 导出为 Excel（.xlsx，Open XML 最小工作簿，无第三方依赖）。
/// 列：SN码 | 日期（yyyy-MM-dd HH:mm:ss）。
/// </summary>
public static class SnExcelExporter
{
    public static void Export(IReadOnlyList<SnRecord> records, string filePath)
    {
        if (records.Count == 0)
            throw new InvalidOperationException("没有可导出的成功 SN 记录");

        if (File.Exists(filePath))
            File.Delete(filePath);

        using var zip = ZipFile.Open(filePath, ZipArchiveMode.Create);

        WriteEntry(zip, "[Content_Types].xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
            </Types>
            """);

        WriteEntry(zip, "_rels/.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);

        WriteEntry(zip, "xl/workbook.xml",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="SN记录" sheetId="1" r:id="rId1"/>
              </sheets>
            </workbook>
            """);

        WriteEntry(zip, "xl/_rels/workbook.xml.rels",
            """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);

        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
            """);
        AppendRow(sb, "SN码", "日期");
        foreach (var r in records)
            AppendRow(sb, r.Sn, r.Time.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.Append("""
              </sheetData>
            </worksheet>
            """);
        WriteEntry(zip, "xl/worksheets/sheet1.xml", sb.ToString());
    }

    private static void AppendRow(StringBuilder sb, string col1, string col2)
    {
        sb.Append("<row>");
        AppendCell(sb, col1);
        AppendCell(sb, col2);
        sb.Append("</row>");
    }

    private static void AppendCell(StringBuilder sb, string value)
    {
        sb.Append("<c t=\"inlineStr\"><is><t>")
          .Append(System.Security.SecurityElement.Escape(value) ?? "")
          .Append("</t></is></c>");
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }
}
