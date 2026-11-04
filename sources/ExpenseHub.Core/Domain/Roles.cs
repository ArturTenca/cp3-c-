using System.Collections.Generic;

namespace ExpenseHub.Core.Domain;

/// <summary>Roles conhecidas pela aplicação.</summary>
internal static class Roles
{
    public const string Admin = "Admin";
    public const string Employee = "Employee";
    public const string Approver = "Approver";
    public const string Finance = "Finance";
    public const string Auditor = "Auditor";

    public static IReadOnlyList<string> All { get; } = [Admin, Employee, Approver, Finance, Auditor];
}
