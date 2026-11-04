using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Core.Common;
using ExpenseHub.Core.Data;
using ExpenseHub.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExpenseHub.Core.Expenses;

/// <summary>Regras de reembolso (CP2): role, ownership e estado combinados no serviço.</summary>
internal sealed class ExpenseService
{
    private readonly ExpenseHubDbContext _db;
    private readonly TimeProvider _timeProvider;

    public ExpenseService(ExpenseHubDbContext db, TimeProvider timeProvider)
    {
        _db = db;
        _timeProvider = timeProvider;
    }

    public async Task<ServiceResult<ExpenseDto>> CreateAsync(ExpenseInput input, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(user);
        ServiceError? invalid = await ValidateAsync(input, cancellationToken);
        if (invalid is not null)
        {
            return ServiceResult<ExpenseDto>.Fail(ServiceStatus.BadRequest, invalid.Code, invalid.Message);
        }

        DateTime now = Now();
        var expense = new Expense
        {
            Id = Guid.NewGuid(),
            OwnerId = user.UserId,
            Description = input.Description!.Trim(),
            Amount = input.Amount!.Value,
            ExpenseDate = input.ExpenseDate!.Value,
            CategoryId = input.CategoryId!.Value,
            Status = ExpenseStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Expenses.Add(expense);
        _db.ExpenseHistory.Add(History(expense, "Created", user.UserId, null, ExpenseStatus.Draft, now));
        await _db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ExpenseDto>.Success(ExpenseDto.From(expense), ServiceStatus.Created);
    }

    public async Task<ServiceResult<ExpenseDto>> UpdateAsync(Guid id, ExpenseInput input, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(user);
        Expense? expense = await _db.Expenses.Where(Visible(user)).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (expense is null)
        {
            return NotFound();
        }

        if (expense.OwnerId != user.UserId)
        {
            return Forbidden("Somente o proprietário pode editar.");
        }

        if (expense.Status != ExpenseStatus.Draft)
        {
            return Conflict("Somente rascunhos podem ser editados.");
        }

        ServiceError? invalid = await ValidateAsync(input, cancellationToken);
        if (invalid is not null)
        {
            return ServiceResult<ExpenseDto>.Fail(ServiceStatus.BadRequest, invalid.Code, invalid.Message);
        }

        string description = input.Description!.Trim();
        decimal amount = input.Amount!.Value;
        DateOnly expenseDate = input.ExpenseDate!.Value;
        int categoryId = input.CategoryId!.Value;

        var changes = new List<string>();
        Track(changes, "description", expense.Description, description);
        Track(changes, "amount", expense.Amount.ToString(CultureInfo.InvariantCulture), amount.ToString(CultureInfo.InvariantCulture));
        Track(changes, "expenseDate", expense.ExpenseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), expenseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Track(changes, "categoryId", expense.CategoryId.ToString(CultureInfo.InvariantCulture), categoryId.ToString(CultureInfo.InvariantCulture));

        DateTime now = Now();
        expense.Description = description;
        expense.Amount = amount;
        expense.ExpenseDate = expenseDate;
        expense.CategoryId = categoryId;
        expense.UpdatedAt = now;
        expense.Version++;

        ExpenseHistory entry = History(expense, "Edited", user.UserId, ExpenseStatus.Draft, ExpenseStatus.Draft, now);
        entry.Changes = string.Join("; ", changes);
        _db.ExpenseHistory.Add(entry);
        return await SaveAsync(expense, cancellationToken);
    }

    public Task<ServiceResult<ExpenseDto>> SubmitAsync(Guid id, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return TransitionAsync(id, user, ExpenseStatus.Draft, ExpenseStatus.Submitted, "Submitted", ownerOnly: true, null, cancellationToken);
    }

    public Task<ServiceResult<ExpenseDto>> ApproveAsync(Guid id, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return TransitionAsync(id, user, ExpenseStatus.Submitted, ExpenseStatus.Approved, "Approved", ownerOnly: false, null, cancellationToken);
    }

    public Task<ServiceResult<ExpenseDto>> RejectAsync(Guid id, RejectInput input, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(user);
        string? justification = input.Justification?.Trim();
        if (justification is null || justification.Length is < 10 or > 500)
        {
            return Task.FromResult(ServiceResult<ExpenseDto>.Fail(ServiceStatus.BadRequest, "invalid_justification", "Justificativa deve ter entre 10 e 500 caracteres."));
        }

        return TransitionAsync(id, user, ExpenseStatus.Submitted, ExpenseStatus.Rejected, "Rejected", ownerOnly: false, justification, cancellationToken);
    }

    public Task<ServiceResult<ExpenseDto>> PayAsync(Guid id, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        return TransitionAsync(id, user, ExpenseStatus.Approved, ExpenseStatus.Paid, "Paid", ownerOnly: false, null, cancellationToken);
    }

    public async Task<IReadOnlyList<ExpenseDto>> ListAsync(UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        List<Expense> expenses = await _db.Expenses.AsNoTracking().Where(Visible(user)).ToListAsync(cancellationToken);
        return [.. expenses.OrderByDescending(e => e.CreatedAt).Select(ExpenseDto.From)];
    }

    public async Task<ServiceResult<ExpenseDto>> GetAsync(Guid id, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        Expense? expense = await _db.Expenses.AsNoTracking().Where(Visible(user)).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        return expense is null ? NotFound() : ServiceResult<ExpenseDto>.Success(ExpenseDto.From(expense));
    }

    public async Task<ServiceResult<IReadOnlyList<ExpenseHistoryDto>>> GetHistoryAsync(Guid id, UserContext user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        bool visible = await _db.Expenses.Where(Visible(user)).AnyAsync(e => e.Id == id, cancellationToken);
        if (!visible)
        {
            return ServiceResult<IReadOnlyList<ExpenseHistoryDto>>.Fail(ServiceStatus.NotFound, "expense_not_found", "Reembolso não encontrado.");
        }

        List<ExpenseHistory> entries = await _db.ExpenseHistory.AsNoTracking().Where(h => h.ExpenseId == id).ToListAsync(cancellationToken);
        IReadOnlyList<ExpenseHistoryDto> result =
        [
            .. entries.OrderBy(h => h.OccurredAt)
                .Select(h => new ExpenseHistoryDto(h.Id, h.Action, h.ActorId, h.OccurredAt, h.FromStatus, h.ToStatus, h.Justification, h.Changes)),
        ];
        return ServiceResult<IReadOnlyList<ExpenseHistoryDto>>.Success(result);
    }

    /// <summary>Filtro de leitura aplicado no banco, antes de materializar dados.</summary>
    internal static Expression<Func<Expense, bool>> Visible(UserContext user)
    {
        string userId = user.UserId;
        bool isEmployee = user.IsInRole(Roles.Employee);
        bool isApprover = user.IsInRole(Roles.Approver);
        bool isFinance = user.IsInRole(Roles.Finance);
        bool isAuditor = user.IsInRole(Roles.Auditor);
        return e => isAuditor
            || (isEmployee && e.OwnerId == userId)
            || (isApprover && e.Status == ExpenseStatus.Submitted)
            || (isFinance && (e.Status == ExpenseStatus.Approved || e.Status == ExpenseStatus.Paid));
    }

    private async Task<ServiceResult<ExpenseDto>> TransitionAsync(
        Guid id,
        UserContext user,
        ExpenseStatus expected,
        ExpenseStatus next,
        string action,
        bool ownerOnly,
        string? justification,
        CancellationToken cancellationToken)
    {
        Expense? expense = await _db.Expenses.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (expense is null)
        {
            return NotFound();
        }

        bool isOwner = expense.OwnerId == user.UserId;
        bool visible = await _db.Expenses.Where(Visible(user)).AnyAsync(e => e.Id == id, cancellationToken);
        bool processedByThisRole = !ownerOnly && expense.Status != expected && IsLaterThan(expense.Status, expected);

        if (ownerOnly && !isOwner)
        {
            return NotFound();
        }

        if (!ownerOnly && isOwner)
        {
            return Forbidden("Não é permitido aprovar, reprovar ou pagar o próprio reembolso.");
        }

        if (!visible && !processedByThisRole)
        {
            return NotFound();
        }

        if (expense.Status != expected)
        {
            return Conflict($"Transição inválida a partir de {expense.Status}.");
        }

        DateTime now = Now();
        expense.Status = next;
        expense.UpdatedAt = now;
        expense.Version++;
        if (next == ExpenseStatus.Rejected)
        {
            expense.RejectionReason = justification;
        }

        ExpenseHistory entry = History(expense, action, user.UserId, expected, next, now);
        entry.Justification = justification;
        _db.ExpenseHistory.Add(entry);

        if (next == ExpenseStatus.Paid)
        {
            _db.PaymentRecords.Add(new PaymentRecord { Id = Guid.NewGuid(), ExpenseId = expense.Id, PaidBy = user.UserId, PaidAt = now, Amount = expense.Amount });
        }

        return await SaveAsync(expense, cancellationToken);
    }

    private static bool IsLaterThan(ExpenseStatus actual, ExpenseStatus expected) => expected switch
    {
        ExpenseStatus.Submitted => actual is ExpenseStatus.Approved or ExpenseStatus.Rejected or ExpenseStatus.Paid,
        ExpenseStatus.Approved => actual is ExpenseStatus.Paid,
        _ => false,
    };

    private static ExpenseHistory History(Expense expense, string action, string actorId, ExpenseStatus? from, ExpenseStatus to, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        ExpenseId = expense.Id,
        Action = action,
        ActorId = actorId,
        OccurredAt = now,
        FromStatus = from,
        ToStatus = to,
    };

    private static void Track(List<string> changes, string field, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            changes.Add($"{field}: '{before}' -> '{after}'");
        }
    }

    private static ServiceResult<ExpenseDto> NotFound() =>
        ServiceResult<ExpenseDto>.Fail(ServiceStatus.NotFound, "expense_not_found", "Reembolso não encontrado.");

    private static ServiceResult<ExpenseDto> Forbidden(string message) =>
        ServiceResult<ExpenseDto>.Fail(ServiceStatus.Forbidden, "forbidden", message);

    private static ServiceResult<ExpenseDto> Conflict(string message) =>
        ServiceResult<ExpenseDto>.Fail(ServiceStatus.Conflict, "invalid_transition", message);

    private async Task<ServiceError?> ValidateAsync(ExpenseInput input, CancellationToken cancellationToken)
    {
        if (input.ExpenseDate is DateOnly date && date > DateOnly.FromDateTime(Now()))
        {
            return new ServiceError("future_date", "A data da despesa não pode ser futura.");
        }

        int categoryId = input.CategoryId ?? 0;
        if (!await _db.ExpenseCategories.AnyAsync(c => c.Id == categoryId, cancellationToken))
        {
            return new ServiceError("invalid_category", "Categoria inexistente.");
        }

        return null;
    }

    private async Task<ServiceResult<ExpenseDto>> SaveAsync(Expense expense, CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return ServiceResult<ExpenseDto>.Success(ExpenseDto.From(expense));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("O reembolso foi alterado por outra operação.");
        }
    }

    private DateTime Now() => _timeProvider.GetUtcNow().UtcDateTime;
}
