namespace ExpenseHub.Core.Domain;

/// <summary>Estados possíveis de um reembolso.</summary>
internal enum ExpenseStatus
{
    /// <summary>Rascunho editável pelo proprietário.</summary>
    Draft = 0,

    /// <summary>Enviado para aprovação.</summary>
    Submitted = 1,

    /// <summary>Aprovado e aguardando pagamento.</summary>
    Approved = 2,

    /// <summary>Reprovado (final).</summary>
    Rejected = 3,

    /// <summary>Pago (final).</summary>
    Paid = 4,
}
