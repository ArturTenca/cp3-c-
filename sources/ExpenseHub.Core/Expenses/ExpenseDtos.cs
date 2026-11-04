using System;
using System.ComponentModel.DataAnnotations;
using ExpenseHub.Core.Domain;

namespace ExpenseHub.Core.Expenses;

/// <summary>Criação ou edição de rascunho. Proprietário, estado, ator e horários nunca vêm do cliente.</summary>
internal sealed class ExpenseInput
{
    [Required]
    [StringLength(500, MinimumLength = 10)]
    public string? Description { get; init; }

    [Required]
    [Range(typeof(decimal), "0.01", "2147483647")]
    public decimal? Amount { get; init; }

    [Required]
    public DateOnly? ExpenseDate { get; init; }

    [Required]
    [Range(1, int.MaxValue)]
    public int? CategoryId { get; init; }
}

/// <summary>Justificativa obrigatória da reprovação.</summary>
internal sealed class RejectInput
{
    [Required]
    [StringLength(500, MinimumLength = 10)]
    public string? Justification { get; init; }
}

/// <summary>Representação pública de um reembolso.</summary>
internal sealed record ExpenseDto(
    Guid Id,
    string OwnerId,
    string Description,
    decimal Amount,
    DateOnly ExpenseDate,
    int CategoryId,
    ExpenseStatus Status,
    string? RejectionReason,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static ExpenseDto From(Expense e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new ExpenseDto(e.Id, e.OwnerId, e.Description, e.Amount, e.ExpenseDate, e.CategoryId, e.Status, e.RejectionReason, e.CreatedAt, e.UpdatedAt);
    }
}

/// <summary>Item de histórico.</summary>
internal sealed record ExpenseHistoryDto(
    Guid Id,
    string Action,
    string ActorId,
    DateTime OccurredAt,
    ExpenseStatus? FromStatus,
    ExpenseStatus ToStatus,
    string? Justification,
    string? Changes);
