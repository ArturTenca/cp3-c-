using System;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Core.Common;
using ExpenseHub.Core.Domain;
using ExpenseHub.Core.Reports;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExpenseHub.UnitTests;

/// <summary>Solicitação, autorização por escopo, isolamento e publicação confirmada.</summary>
[TestClass]
public sealed class ReportServiceTests
{
    private readonly FakeReportRequestRepository _repository = new();
    private readonly FakeReportPublisher _publisher = new();
    private readonly FixedTimeProvider _time = new();

    /// <summary>fromDate posterior a toDate é rejeitado sem registro e sem publicação.</summary>
    [TestMethod]
    public async Task Create_FromAfterTo_IsRejectedWithoutPersistOrPublish()
    {
        ServiceResult<ReportEnvelope> result = await CreateService().CreateAsync(
            Request(TestData.To, TestData.From, ReportScope.Mine), TestData.Employee(TestData.Alice), CancellationToken.None);

        Assert.AreEqual(ServiceStatus.BadRequest, result.Status);
        Assert.AreEqual("invalid_period", result.Error?.Code);
        Assert.IsEmpty(_repository.Reports);
        Assert.IsEmpty(_publisher.Published);
    }

    /// <summary>Employee solicitando Global é negado sem criar processamento.</summary>
    [TestMethod]
    public async Task Create_EmployeeRequestsGlobal_IsForbiddenWithoutWork()
    {
        ServiceResult<ReportEnvelope> result = await CreateService().CreateAsync(
            Request(TestData.From, TestData.To, ReportScope.Global), TestData.Employee(TestData.Alice), CancellationToken.None);

        Assert.AreEqual(ServiceStatus.Forbidden, result.Status);
        Assert.IsEmpty(_repository.Reports);
        Assert.IsEmpty(_publisher.Published);
    }

    /// <summary>Auditor sem Employee não solicita Mine; Admin não ganha privilégio implícito.</summary>
    [TestMethod]
    [DataRow(Roles.Auditor, "Mine")]
    [DataRow(Roles.Admin, "Mine")]
    [DataRow(Roles.Admin, "Global")]
    [DataRow(Roles.Finance, "Global")]
    [DataRow(Roles.Approver, "Mine")]
    public async Task Create_RoleWithoutScopePermission_IsForbidden(string role, string scopeName)
    {
        ReportScope scope = Enum.Parse<ReportScope>(scopeName);
        ServiceResult<ReportEnvelope> result = await CreateService().CreateAsync(
            Request(TestData.From, TestData.To, scope), new UserContext("u1", [role]), CancellationToken.None);

        Assert.AreEqual(ServiceStatus.Forbidden, result.Status);
        Assert.IsEmpty(_publisher.Published);
    }

    /// <summary>Aceitação: solicitante vem da identidade, estado Pending e a mensagem carrega o mesmo id.</summary>
    [TestMethod]
    public async Task Create_Valid_PersistsPendingAndPublishesSameId()
    {
        ServiceResult<ReportEnvelope> result = await CreateService().CreateAsync(
            Request(TestData.From, TestData.To, ReportScope.Mine), TestData.Employee(TestData.Alice), CancellationToken.None);

        Assert.AreEqual(ServiceStatus.Accepted, result.Status);
        ReportRequest saved = _repository.Reports[0];
        Assert.AreEqual(TestData.Alice, saved.RequesterId);
        Assert.AreEqual(ReportStatus.Pending, saved.Status);
        Assert.AreEqual(_time.Now.UtcDateTime, saved.CreatedAt);
        Assert.HasCount(1, _publisher.Published);
        Assert.AreEqual(saved.Id, _publisher.Published[0].ReportId);
        Assert.AreEqual(saved.Id.ToString(), result.Value?.Id);
        Assert.IsNull(result.Value?.Result);
    }

    /// <summary>Employee + Auditor pode solicitar os dois escopos.</summary>
    [TestMethod]
    public async Task Create_EmployeeAndAuditor_CanRequestBothScopes()
    {
        var user = new UserContext(TestData.Carol, [Roles.Employee, Roles.Auditor]);
        ReportService service = CreateService();

        ServiceResult<ReportEnvelope> mine = await service.CreateAsync(Request(TestData.From, TestData.To, ReportScope.Mine), user, CancellationToken.None);
        ServiceResult<ReportEnvelope> global = await service.CreateAsync(Request(TestData.From, TestData.To, ReportScope.Global), user, CancellationToken.None);

        Assert.AreEqual(ServiceStatus.Accepted, mine.Status);
        Assert.AreEqual(ServiceStatus.Accepted, global.Status);
    }

    /// <summary>Sem confirmação do broker: 503 com id persistido, registro mantido em Pending.</summary>
    [TestMethod]
    public async Task Create_PublishNotConfirmed_ReturnsUnavailableWithPersistedId()
    {
        _publisher.Confirm = false;

        ServiceResult<ReportEnvelope> result = await CreateService().CreateAsync(
            Request(TestData.From, TestData.To, ReportScope.Mine), TestData.Employee(TestData.Alice), CancellationToken.None);

        Assert.AreEqual(ServiceStatus.Unavailable, result.Status);
        Assert.AreEqual("publish_unconfirmed", result.Error?.Code);
        Assert.HasCount(1, _repository.Reports);
        Assert.AreEqual(_repository.Reports[0].Id.ToString(), result.Value?.Id);
        Assert.AreEqual(ReportStatus.Pending, _repository.Reports[0].Status);
    }

    /// <summary>Banco indisponível antes de registrar: erro explícito, sem id e sem publicação.</summary>
    [TestMethod]
    public async Task Create_DatabaseUnavailable_ReturnsErrorWithoutIdOrPublish()
    {
        _repository.ThrowOnAdd = true;

        ServiceResult<ReportEnvelope> result = await CreateService().CreateAsync(
            Request(TestData.From, TestData.To, ReportScope.Mine), TestData.Employee(TestData.Alice), CancellationToken.None);

        Assert.AreEqual(ServiceStatus.Unavailable, result.Status);
        Assert.IsNull(result.Value);
        Assert.IsEmpty(_publisher.Published);
    }

    /// <summary>Detalhe de solicitação alheia retorna 404, mesmo para Auditor.</summary>
    [TestMethod]
    public async Task Get_OtherUsersReport_IsNotFoundEvenForAuditor()
    {
        ReportService service = CreateService();
        ServiceResult<ReportEnvelope> created = await service.CreateAsync(
            Request(TestData.From, TestData.To, ReportScope.Mine), TestData.Employee(TestData.Alice), CancellationToken.None);
        Guid id = Guid.Parse(created.Value!.Id);

        ServiceResult<ReportEnvelope> byBob = await service.GetAsync(id, TestData.Employee(TestData.Bob), CancellationToken.None);
        ServiceResult<ReportEnvelope> byAuditor = await service.GetAsync(id, TestData.Auditor(TestData.Carol), CancellationToken.None);
        ServiceResult<ReportEnvelope> byOwner = await service.GetAsync(id, TestData.Employee(TestData.Alice), CancellationToken.None);

        Assert.AreEqual(ServiceStatus.NotFound, byBob.Status);
        Assert.IsNull(byBob.Value);
        Assert.AreEqual(ServiceStatus.NotFound, byAuditor.Status);
        Assert.AreEqual(ServiceStatus.Ok, byOwner.Status);
    }

    /// <summary>A listagem contém somente solicitações próprias.</summary>
    [TestMethod]
    public async Task List_ReturnsOnlyOwnRequests()
    {
        ReportService service = CreateService();
        await service.CreateAsync(Request(TestData.From, TestData.To, ReportScope.Mine), TestData.Employee(TestData.Alice), CancellationToken.None);
        await service.CreateAsync(Request(TestData.From, TestData.To, ReportScope.Mine), TestData.Employee(TestData.Bob), CancellationToken.None);

        var list = await service.ListAsync(TestData.Employee(TestData.Bob), CancellationToken.None);

        Assert.HasCount(1, list);
        Assert.AreEqual(_repository.Reports[1].Id.ToString(), list[0].Id);
    }

    private static CreateReportRequest Request(DateOnly from, DateOnly to, ReportScope scope) =>
        new() { FromDate = from, ToDate = to, Scope = scope };

    private ReportService CreateService() =>
        new(_repository, _publisher, _time, NullLogger<ReportService>.Instance);
}
