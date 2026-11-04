using System;
using System.Collections.Generic;
using System.Linq;
using ExpenseHub.Core.Domain;
using ExpenseHub.Core.Reports;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExpenseHub.UnitTests;

/// <summary>Cálculo, período inclusivo, escopo e conjunto vazio.</summary>
[TestClass]
public sealed class ReportCalculationTests
{
    private static readonly DateTime GeneratedAt = new(2026, 10, 1, 12, 0, 2, DateTimeKind.Utc);

    /// <summary>Exemplo Mine da apostila: 3 reembolsos e R$ 175,00.</summary>
    [TestMethod]
    public void MineScope_ApostilaExample_CountsAndSumsByStatus()
    {
        ReportResult result = Run(ReportScope.Mine, TestData.Alice, TestData.ApostilaExample());

        Assert.AreEqual(3, result.TotalCount);
        Assert.AreEqual(175.00m, result.TotalAmount);
        Assert.AreEqual(new StatusSummary(ExpenseStatus.Approved, 2, 150.00m), TestData.Status(result, ExpenseStatus.Approved));
        Assert.AreEqual(new StatusSummary(ExpenseStatus.Paid, 1, 25.00m), TestData.Status(result, ExpenseStatus.Paid));
        Assert.AreEqual(0, TestData.Status(result, ExpenseStatus.Draft).Count);
        Assert.AreEqual(GeneratedAt, result.GeneratedAt);
    }

    /// <summary>Exemplo Global da apostila: 4 reembolsos e R$ 375,00, excluindo o registro fora do período.</summary>
    [TestMethod]
    public void GlobalScope_ApostilaExample_IncludesOtherEmployeesButNotOutOfPeriod()
    {
        ReportResult result = Run(ReportScope.Global, TestData.Carol, TestData.ApostilaExample());

        Assert.AreEqual(4, result.TotalCount);
        Assert.AreEqual(375.00m, result.TotalAmount);
        Assert.AreEqual(new StatusSummary(ExpenseStatus.Approved, 3, 350.00m), TestData.Status(result, ExpenseStatus.Approved));
    }

    /// <summary>Os limites fromDate e toDate entram; o dia anterior e o posterior não.</summary>
    [TestMethod]
    public void PeriodBounds_AreInclusive()
    {
        List<Expense> expenses =
        [
            TestData.NewExpense(TestData.Alice, TestData.From.AddDays(-1), ExpenseStatus.Draft, 1.00m),
            TestData.NewExpense(TestData.Alice, TestData.From, ExpenseStatus.Submitted, 10.00m),
            TestData.NewExpense(TestData.Alice, TestData.To, ExpenseStatus.Rejected, 20.00m),
            TestData.NewExpense(TestData.Alice, TestData.To.AddDays(1), ExpenseStatus.Draft, 2.00m),
        ];

        ReportResult result = Run(ReportScope.Mine, TestData.Alice, expenses);

        Assert.AreEqual(2, result.TotalCount);
        Assert.AreEqual(30.00m, result.TotalAmount);
        Assert.AreEqual(0, TestData.Status(result, ExpenseStatus.Draft).Count);
    }

    /// <summary>Sem ocorrências: os cinco estados presentes com zero e totais zero.</summary>
    [TestMethod]
    public void EmptySet_ReturnsAllFiveStatusesWithZero()
    {
        ReportResult result = Run(ReportScope.Mine, TestData.Bob, TestData.ApostilaExample().Where(e => e.OwnerId == TestData.Alice).ToList());

        Assert.AreEqual(0, result.TotalCount);
        Assert.AreEqual(0m, result.TotalAmount);
        CollectionAssert.AreEquivalent(Enum.GetValues<ExpenseStatus>(), result.ByStatus.Select(s => s.Status).ToArray());
        Assert.IsTrue(result.ByStatus.All(s => s.Count == 0 && s.Amount == 0m));
    }

    /// <summary>Somas decimais não acumulam erro de ponto flutuante.</summary>
    [TestMethod]
    public void DecimalSum_HasNoFloatingPointError()
    {
        List<Expense> expenses = [.. Enumerable.Range(0, 10).Select(_ => TestData.NewExpense(TestData.Alice, TestData.From, ExpenseStatus.Paid, 0.10m))];

        ReportResult result = Run(ReportScope.Mine, TestData.Alice, expenses);

        Assert.AreEqual(1.00m, result.TotalAmount);
    }

    private static ReportResult Run(ReportScope scope, string requester, List<Expense> expenses)
    {
        ExpenseAmount[] amounts =
        [
            .. ReportFilter.Apply(expenses.AsQueryable(), scope, requester, TestData.From, TestData.To)
                .Select(e => new ExpenseAmount(e.Status, e.Amount)),
        ];
        return ReportCalculator.Calculate(amounts, GeneratedAt);
    }
}
