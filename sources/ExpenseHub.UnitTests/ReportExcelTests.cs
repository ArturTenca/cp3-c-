using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using ExpenseHub.Core.Common;
using ExpenseHub.Core.Domain;
using ExpenseHub.Core.Reports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExpenseHub.UnitTests;

/// <summary>Contrato do download Excel: planilha válida, fiel ao resultado salvo, 404/409.</summary>
[TestClass]
public sealed class ReportExcelTests
{
    private readonly FakeReportRequestRepository _repository = new();
    private readonly FixedTimeProvider _time = new();

    /// <summary>Relatório concluído gera .xlsx real com células numéricas iguais ao resultado salvo.</summary>
    [TestMethod]
    public async Task Excel_Completed_IsValidWorkbookMatchingStoredResult()
    {
        ReportRequest report = AddCompleted(TestData.Alice, ReportCalculator.Calculate(
        [
            new ExpenseAmount(ExpenseStatus.Approved, 100.00m),
            new ExpenseAmount(ExpenseStatus.Approved, 50.00m),
            new ExpenseAmount(ExpenseStatus.Paid, 25.00m),
        ],
        _time.Now.UtcDateTime));

        ServiceResult<byte[]> result = await CreateService().ExportExcelAsync(report.Id, TestData.Employee(TestData.Alice), CancellationToken.None);

        Assert.AreEqual(ServiceStatus.Ok, result.Status);
        Assert.IsTrue(result.Value![0] == (byte)'P' && result.Value[1] == (byte)'K', "xlsx é um pacote ZIP, não texto renomeado");
        using var stream = new MemoryStream(result.Value);
        using var workbook = new XLWorkbook(stream);
        IXLWorksheet sheet = workbook.Worksheet(ReportExcelBuilder.SheetName);

        Assert.AreEqual(report.Id.ToString(), sheet.Cell(2, 2).GetString());
        Assert.AreEqual("Mine", sheet.Cell(3, 2).GetString());
        Assert.AreEqual("2026-09-01", sheet.Cell(4, 2).GetString());
        Assert.AreEqual("2026-09-30", sheet.Cell(5, 2).GetString());
        Assert.AreEqual("Estado", sheet.Cell(ReportExcelBuilder.HeaderRow, 1).GetString());
        Assert.AreEqual("Quantidade", sheet.Cell(ReportExcelBuilder.HeaderRow, 2).GetString());
        Assert.AreEqual("Valor", sheet.Cell(ReportExcelBuilder.HeaderRow, 3).GetString());

        int approvedRow = ReportExcelBuilder.FirstStatusRow + (int)ExpenseStatus.Approved;
        Assert.AreEqual("Approved", sheet.Cell(approvedRow, 1).GetString());
        Assert.AreEqual(XLDataType.Number, sheet.Cell(approvedRow, 2).DataType);
        Assert.AreEqual(2, sheet.Cell(approvedRow, 2).GetValue<int>());
        Assert.AreEqual(150.00m, sheet.Cell(approvedRow, 3).GetValue<decimal>());

        int totalRow = ReportExcelBuilder.FirstStatusRow + 5;
        Assert.AreEqual("Total", sheet.Cell(totalRow, 1).GetString());
        Assert.AreEqual(3, sheet.Cell(totalRow, 2).GetValue<int>());
        Assert.AreEqual(175.00m, sheet.Cell(totalRow, 3).GetValue<decimal>());
    }

    /// <summary>Resultado vazio: os cinco estados aparecem com zero numérico.</summary>
    [TestMethod]
    public async Task Excel_EmptyResult_HasFiveZeroRows()
    {
        ReportRequest report = AddCompleted(TestData.Alice, ReportCalculator.Calculate([], _time.Now.UtcDateTime));

        ServiceResult<byte[]> result = await CreateService().ExportExcelAsync(report.Id, TestData.Employee(TestData.Alice), CancellationToken.None);

        using var stream = new MemoryStream(result.Value!);
        using var workbook = new XLWorkbook(stream);
        IXLWorksheet sheet = workbook.Worksheet(ReportExcelBuilder.SheetName);
        for (int i = 0; i < 5; i++)
        {
            int row = ReportExcelBuilder.FirstStatusRow + i;
            Assert.AreEqual(((ExpenseStatus)i).ToString(), sheet.Cell(row, 1).GetString());
            Assert.AreEqual(0, sheet.Cell(row, 2).GetValue<int>());
            Assert.AreEqual(0m, sheet.Cell(row, 3).GetValue<decimal>());
        }
    }

    /// <summary>Solicitação própria Pending, Processing ou Failed: 409 sem arquivo.</summary>
    [TestMethod]
    [DataRow("Pending")]
    [DataRow("Processing")]
    [DataRow("Failed")]
    public async Task Excel_OwnNotCompleted_IsConflict(string statusName)
    {
        ReportRequest report = AddCompleted(TestData.Alice, ReportCalculator.Calculate([], _time.Now.UtcDateTime));
        report.Status = Enum.Parse<ReportStatus>(statusName);

        ServiceResult<byte[]> result = await CreateService().ExportExcelAsync(report.Id, TestData.Employee(TestData.Alice), CancellationToken.None);

        Assert.AreEqual(ServiceStatus.Conflict, result.Status);
        Assert.IsNull(result.Value);
    }

    /// <summary>Excel alheio ou inexistente: 404 sem dados, inclusive para Auditor.</summary>
    [TestMethod]
    public async Task Excel_OtherUserOrMissing_IsNotFound()
    {
        ReportRequest report = AddCompleted(TestData.Alice, ReportCalculator.Calculate([], _time.Now.UtcDateTime));
        ReportService service = CreateService();

        ServiceResult<byte[]> byBob = await service.ExportExcelAsync(report.Id, TestData.Employee(TestData.Bob), CancellationToken.None);
        ServiceResult<byte[]> byAuditor = await service.ExportExcelAsync(report.Id, TestData.Auditor(TestData.Carol), CancellationToken.None);
        ServiceResult<byte[]> missing = await service.ExportExcelAsync(Guid.NewGuid(), TestData.Employee(TestData.Alice), CancellationToken.None);

        Assert.AreEqual(ServiceStatus.NotFound, byBob.Status);
        Assert.IsNull(byBob.Value);
        Assert.AreEqual(ServiceStatus.NotFound, byAuditor.Status);
        Assert.AreEqual(ServiceStatus.NotFound, missing.Status);
    }

    private ReportRequest AddCompleted(string requester, ReportResult result)
    {
        var report = new ReportRequest
        {
            Id = Guid.NewGuid(),
            RequesterId = requester,
            Scope = ReportScope.Mine,
            FromDate = TestData.From,
            ToDate = TestData.To,
            Status = ReportStatus.Completed,
            CreatedAt = _time.Now.UtcDateTime,
            CompletedAt = _time.Now.UtcDateTime,
            ResultJson = ReportJson.SerializeResult(result),
        };
        _repository.Reports.Add(report);
        return report;
    }

    private ReportService CreateService() =>
        new(_repository, new FakeReportPublisher(), _time, NullLogger<ReportService>.Instance);
}
