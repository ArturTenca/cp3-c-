using System;
using System.Collections.Generic;
using System.Linq;
using ExpenseHub.Core.Common;
using ExpenseHub.Core.Domain;

namespace ExpenseHub.Core.Reports;

/// <summary>Cálculo puro do resumo por estado, sem banco.</summary>
internal static class ReportCalculator
{
    public static ReportResult Calculate(IEnumerable<ExpenseAmount> expenses, DateTime generatedAt)
    {
        ArgumentNullException.ThrowIfNull(expenses);

        ExpenseStatus[] statuses = Enum.GetValues<ExpenseStatus>();
        var counts = statuses.ToDictionary(s => s, _ => 0);
        var amounts = statuses.ToDictionary(s => s, _ => 0m);

        foreach (ExpenseAmount expense in expenses)
        {
            counts[expense.Status]++;
            amounts[expense.Status] += expense.Amount;
        }

        List<StatusSummary> byStatus = [.. statuses.Select(s => new StatusSummary(s, counts[s], amounts[s]))];
        return new ReportResult(generatedAt, byStatus.Sum(s => s.Count), byStatus.Sum(s => s.Amount), byStatus);
    }
}

/// <summary>Filtro de período e escopo aplicado no banco.</summary>
internal static class ReportFilter
{
    public static IQueryable<Expense> Apply(IQueryable<Expense> source, ReportScope scope, string requesterId, DateOnly fromDate, DateOnly toDate)
    {
        IQueryable<Expense> inPeriod = source.Where(e => e.ExpenseDate >= fromDate && e.ExpenseDate <= toDate);
        return scope switch
        {
            ReportScope.Mine => inPeriod.Where(e => e.OwnerId == requesterId),
            ReportScope.Global => inPeriod,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Escopo desconhecido."),
        };
    }
}

/// <summary>Matriz de quem pode solicitar cada escopo.</summary>
internal static class ReportAccess
{
    public static bool CanRequest(ReportScope scope, UserContext user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return scope switch
        {
            ReportScope.Mine => user.IsInRole(Roles.Employee),
            ReportScope.Global => user.IsInRole(Roles.Auditor),
            _ => false,
        };
    }
}
