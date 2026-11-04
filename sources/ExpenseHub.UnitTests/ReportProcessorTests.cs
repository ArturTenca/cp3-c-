using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Core.Common;
using ExpenseHub.Core.Domain;
using ExpenseHub.Core.Reports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExpenseHub.UnitTests;

/// <summary>Worker: escopo do registro, idempotência, resultado persistido e falhas.</summary>
[TestClass]
public sealed class ReportProcessorTests
{
    private readonly List<ReportRequest> _reports = [];
    private readonly FixedTimeProvider _time = new();
    private FakeReportProcessingStore _store = null!;

    /// <summary>Prepara o store compartilhado.</summary>
    [TestInitialize]
    public void Setup()
    {
        _store = new FakeReportProcessingStore(_reports);
        _store.Expenses.AddRange(TestData.ApostilaExample());
    }

    /// <summary>Mine usa o solicitante registrado no servidor e não soma despesas de outro funcionário.</summary>
    [TestMethod]
    public async Task Process_Mine_UsesRequesterFromPersistedRecord()
    {
        ReportRequest report = AddPending(TestData.Alice, ReportScope.Mine);

        ProcessingOutcome outcome = await CreateProcessor().ProcessAsync(report.Id, CancellationToken.None);

        Assert.AreEqual(ProcessingOutcome.Completed, outcome);
        Assert.AreEqual(ReportStatus.Completed, report.Status);
        ReportResult result = ReportJson.DeserializeResult(report.ResultJson!);
        Assert.AreEqual(3, result.TotalCount);
        Assert.AreEqual(175.00m, result.TotalAmount);
        Assert.IsNotNull(report.StartedAt);
        Assert.IsNotNull(report.CompletedAt);
    }

    /// <summary>Mesma mensagem novamente: nenhum novo resultado, mesmo que os reembolsos tenham mudado.</summary>
    [TestMethod]
    public async Task Process_DuplicateAfterCompletion_HasNoNewEffect()
    {
        ReportRequest report = AddPending(TestData.Alice, ReportScope.Mine);
        ReportProcessor processor = CreateProcessor();
        await processor.ProcessAsync(report.Id, CancellationToken.None);
        string firstResult = report.ResultJson!;
        DateTime? firstCompletedAt = report.CompletedAt;

        _store.Expenses.Add(TestData.NewExpense(TestData.Alice, TestData.From, ExpenseStatus.Paid, 999m));
        _time.Now = _time.Now.AddHours(1);
        ProcessingOutcome second = await CreateProcessor().ProcessAsync(report.Id, CancellationToken.None);

        Assert.AreEqual(ProcessingOutcome.Duplicate, second);
        Assert.AreEqual(firstResult, report.ResultJson);
        Assert.AreEqual(firstCompletedAt, report.CompletedAt);
        Assert.AreEqual(1, _store.CompleteWrites);
        Assert.AreEqual(1, _store.LoadCalls);
    }

    /// <summary>Consulta após alterar um reembolso devolve o resultado persistido, sem recálculo.</summary>
    [TestMethod]
    public async Task Get_AfterExpenseChanges_ReturnsPersistedResult()
    {
        var repository = new FakeReportRequestRepository();
        var service = new ReportService(repository, new FakeReportPublisher(), _time, NullLogger<ReportService>.Instance);
        ServiceResult<ReportEnvelope> created = await service.CreateAsync(
            new CreateReportRequest { FromDate = TestData.From, ToDate = TestData.To, Scope = ReportScope.Mine },
            TestData.Employee(TestData.Alice),
            CancellationToken.None);
        var store = new FakeReportProcessingStore(repository.Reports);
        store.Expenses.AddRange(TestData.ApostilaExample());
        Guid id = Guid.Parse(created.Value!.Id);
        await new ReportProcessor(store, _time, NullLogger<ReportProcessor>.Instance, Options()).ProcessAsync(id, CancellationToken.None);

        store.Expenses[0].Status = ExpenseStatus.Paid;
        store.Expenses[0].Amount = 1_000m;
        ServiceResult<ReportEnvelope> fetched = await service.GetAsync(id, TestData.Employee(TestData.Alice), CancellationToken.None);

        Assert.AreEqual(ReportStatus.Completed, fetched.Value?.Status);
        Assert.AreEqual(175.00m, fetched.Value?.Result?.TotalAmount);
        Assert.AreEqual(2, TestData.Status(fetched.Value!.Result!, ExpenseStatus.Approved).Count);
        Assert.AreEqual(1, store.LoadCalls);
    }

    /// <summary>Solicitação deixada em Processing (queda antes do ack) é retomada na reentrega.</summary>
    [TestMethod]
    public async Task Process_StuckProcessing_IsResumed()
    {
        ReportRequest report = AddPending(TestData.Alice, ReportScope.Mine);
        report.Status = ReportStatus.Processing;
        report.StartedAt = _time.Now.UtcDateTime.AddMinutes(-5);
        report.Attempts = 1;

        ProcessingOutcome outcome = await CreateProcessor().ProcessAsync(report.Id, CancellationToken.None);

        Assert.AreEqual(ProcessingOutcome.Completed, outcome);
        Assert.AreEqual(_time.Now.UtcDateTime.AddMinutes(-5), report.StartedAt);
        Assert.AreEqual(2, report.Attempts);
    }

    /// <summary>Falha ao salvar o resultado é transitória: não conclui e não pode ser confirmada.</summary>
    [TestMethod]
    public async Task Process_PersistFailure_IsTransientAndNotCompleted()
    {
        ReportRequest report = AddPending(TestData.Alice, ReportScope.Mine);
        _store.ThrowOnComplete = true;

        ProcessingOutcome outcome = await CreateProcessor().ProcessAsync(report.Id, CancellationToken.None);

        Assert.AreEqual(ProcessingOutcome.Transient, outcome);
        Assert.AreNotEqual(ReportStatus.Completed, report.Status);
        Assert.IsNull(report.ResultJson);
    }

    /// <summary>Após o limite de tentativas a falha é definitiva (sem reencaminhamento infinito).</summary>
    [TestMethod]
    public async Task Process_MaxAttemptsExceeded_MarksFailedDefinitively()
    {
        ReportRequest report = AddPending(TestData.Alice, ReportScope.Mine);
        report.Status = ReportStatus.Processing;
        report.Attempts = 3;

        ProcessingOutcome outcome = await CreateProcessor().ProcessAsync(report.Id, CancellationToken.None);
        ProcessingOutcome again = await CreateProcessor().ProcessAsync(report.Id, CancellationToken.None);

        Assert.AreEqual(ProcessingOutcome.Failed, outcome);
        Assert.AreEqual(ProcessingOutcome.Duplicate, again);
        Assert.AreEqual(ReportStatus.Failed, report.Status);
        Assert.AreEqual("max_attempts_exceeded", report.ErrorCode);
        Assert.IsNull(report.ResultJson);
        Assert.IsNotNull(report.CompletedAt);
    }

    /// <summary>Registro persistido inválido é falha de negócio definitiva.</summary>
    [TestMethod]
    public async Task Process_InvalidPersistedPeriod_MarksFailed()
    {
        ReportRequest report = AddPending(TestData.Alice, ReportScope.Mine);
        report.FromDate = TestData.To;
        report.ToDate = TestData.From;

        ProcessingOutcome outcome = await CreateProcessor().ProcessAsync(report.Id, CancellationToken.None);

        Assert.AreEqual(ProcessingOutcome.Failed, outcome);
        Assert.AreEqual("invalid_request", report.ErrorCode);
        Assert.AreEqual(0, _store.LoadCalls);
    }

    /// <summary>Mensagem com id inexistente é descartada sem efeito.</summary>
    [TestMethod]
    public async Task Process_UnknownId_ReturnsNotFound()
    {
        ProcessingOutcome outcome = await CreateProcessor().ProcessAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.AreEqual(ProcessingOutcome.NotFound, outcome);
        Assert.AreEqual(0, _store.CompleteWrites);
    }

    private static IOptions<ReportProcessingOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new ReportProcessingOptions { MaxAttempts = 3 });

    private ReportRequest AddPending(string requester, ReportScope scope)
    {
        var report = new ReportRequest
        {
            Id = Guid.NewGuid(),
            RequesterId = requester,
            Scope = scope,
            FromDate = TestData.From,
            ToDate = TestData.To,
            Status = ReportStatus.Pending,
            CreatedAt = _time.Now.UtcDateTime,
        };
        _reports.Add(report);
        return report;
    }

    private ReportProcessor CreateProcessor() =>
        new(_store, _time, NullLogger<ReportProcessor>.Instance, Options());
}
