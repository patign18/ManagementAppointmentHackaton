using System.IO.Compression;
using System.Xml.Linq;
using ManagementAppointment.Models;

namespace ManagementAppointment.Services;

public static class ReportExport
{
    public static byte[] Excel(IEnumerable<Appointment> appointments, OfficeClock clock)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Write(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
            Write(zip, "_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Write(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Citas\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Write(zip, "xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var data = new XElement(ns + "sheetData"); var rowNumber = 0;
            void Row(string[] values) { rowNumber++; data.Add(new XElement(ns + "row", new XAttribute("r", rowNumber), values.Select((value, i) => new XElement(ns + "c", new XAttribute("r", $"{(char)('A' + i)}{rowNumber}"), new XAttribute("t", "inlineStr"), new XElement(ns + "is", new XElement(ns + "t", value)))))); }
            Row(["Cita", "Fecha", "Inicio", "Fin", "Paciente", "Médico", "Especialidad", "Estado"]);
            foreach (var item in appointments) Row([item.Id.ToString(), clock.Local(item.StartUtc).ToString("yyyy-MM-dd"), clock.Local(item.StartUtc).ToString("h.mm tt"), clock.Local(item.EndUtc).ToString("h.mm tt"), item.Patient.FullName, item.Doctor.FullName, item.Doctor.Specialty?.Name ?? "", StatusText.Label(item.Status)]);
            Write(zip, "xl/worksheets/sheet1.xml", new XElement(ns + "worksheet", data).ToString(SaveOptions.DisableFormatting));
        }
        return output.ToArray();
    }
    private static void Write(ZipArchive zip, string path, string content) { using var writer = new StreamWriter(zip.CreateEntry(path).Open()); writer.Write(content); }
}
