using System;
using System.Globalization;
using System.IO;
using ClosedXML.Excel;

namespace ExpenseHub.Core.Reports;

/// <summary>Monta a planilha somente a partir do resultado persistido.</summary>
internal static class ReportExcelBuilder
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string SheetName = "Relatorio";
    public const int HeaderRow = 8;
    public const int FirstStatusRow = 9;

    public static string FileName(Guid reportId) => $"report-{reportId}.xlsx";

    public static byte[] Build(ReportRequest report, ReportResult result)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(result);

        using var workbook = new XLWorkbook();
        IXLWorksheet sheet = workbook.Worksheets.Add(SheetName);

        sheet.Cell(1, 1).Value = "Relatório de reembolsos — ExpenseHub";
        sheet.Cell(2, 1).Value = "Identificador";
        sheet.Cell(2, 2).Value = report.Id.ToString();
        sheet.Cell(3, 1).Value = "Escopo";
        sheet.Cell(3, 2).Value = report.Scope.ToString();
        sheet.Cell(4, 1).Value = "Período inicial";
        sheet.Cell(4, 2).Value = report.FromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sheet.Cell(5, 1).Value = "Período final";
        sheet.Cell(5, 2).Value = report.ToDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sheet.Cell(6, 1).Value = "Gerado em (UTC)";
        sheet.Cell(6, 2).Value = result.GeneratedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        sheet.Cell(HeaderRow, 1).Value = "Estado";
        sheet.Cell(HeaderRow, 2).Value = "Quantidade";
        sheet.Cell(HeaderRow, 3).Value = "Valor";
        sheet.Range(HeaderRow, 1, HeaderRow, 3).Style.Font.Bold = true;

        int row = FirstStatusRow;
        foreach (StatusSummary summary in result.ByStatus)
        {
            sheet.Cell(row, 1).Value = summary.Status.ToString();
            sheet.Cell(row, 2).Value = summary.Count;
            sheet.Cell(row, 3).Value = summary.Amount;
            row++;
        }

        sheet.Cell(row, 1).Value = "Total";
        sheet.Cell(row, 2).Value = result.TotalCount;
        sheet.Cell(row, 3).Value = result.TotalAmount;
        sheet.Range(row, 1, row, 3).Style.Font.Bold = true;
        sheet.Range(FirstStatusRow, 3, row, 3).Style.NumberFormat.Format = "#,##0.00";

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
