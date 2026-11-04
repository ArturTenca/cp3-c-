using System;

namespace ExpenseHub.Core.Domain;

/// <summary>Solicitação de reembolso com um único valor.</summary>
internal sealed class Expense
{
    public Guid Id { get; set; }

    public string OwnerId { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public DateOnly ExpenseDate { get; set; }

    public int CategoryId { get; set; }

    public ExpenseCategory? Category { get; set; }

    public ExpenseStatus Status { get; set; }

    public string? RejectionReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public int Version { get; set; }
}

/// <summary>Categoria de despesa.</summary>
internal sealed class ExpenseCategory
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>Registro imutável de uma ação sobre um reembolso.</summary>
internal sealed class ExpenseHistory
{
    public Guid Id { get; set; }

    public Guid ExpenseId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string ActorId { get; set; } = string.Empty;

    public DateTime OccurredAt { get; set; }

    public ExpenseStatus? FromStatus { get; set; }

    public ExpenseStatus ToStatus { get; set; }

    public string? Justification { get; set; }

    public string? Changes { get; set; }
}

/// <summary>Pagamento registrado pelo financeiro.</summary>
internal sealed class PaymentRecord
{
    public Guid Id { get; set; }

    public Guid ExpenseId { get; set; }

    public string PaidBy { get; set; } = string.Empty;

    public DateTime PaidAt { get; set; }

    public decimal Amount { get; set; }
}
