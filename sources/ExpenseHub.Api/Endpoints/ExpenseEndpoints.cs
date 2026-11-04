using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Api.Infrastructure;
using ExpenseHub.Core.Domain;
using ExpenseHub.Core.Expenses;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ExpenseHub.Api.Endpoints;

/// <summary>Rotas de reembolso do CP2.</summary>
internal static class ExpenseEndpoints
{
    public static IEndpointRouteBuilder MapExpenseEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/expenses").WithTags("Expenses");
        string[] readers = [Roles.Employee, Roles.Approver, Roles.Finance, Roles.Auditor];

        group.MapPost(string.Empty, CreateAsync).RequireAuthorization(p => p.RequireRole(Roles.Employee));
        group.MapPut("/{id:guid}", UpdateAsync).RequireAuthorization(p => p.RequireRole(Roles.Employee));
        group.MapGet(string.Empty, ListAsync).RequireAuthorization(p => p.RequireRole(readers));
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(p => p.RequireRole(readers));
        group.MapGet("/{id:guid}/history", HistoryAsync).RequireAuthorization(p => p.RequireRole(readers));
        group.MapPost("/{id:guid}/submit", SubmitAsync).RequireAuthorization(p => p.RequireRole(Roles.Employee));
        group.MapPost("/{id:guid}/approve", ApproveAsync).RequireAuthorization(p => p.RequireRole(Roles.Approver));
        group.MapPost("/{id:guid}/reject", RejectAsync).RequireAuthorization(p => p.RequireRole(Roles.Approver));
        group.MapPost("/{id:guid}/pay", PayAsync).RequireAuthorization(p => p.RequireRole(Roles.Finance));
        return app;
    }

    private static async Task<IResult> CreateAsync(ExpenseInput? input, ClaimsPrincipal user, ExpenseService service, CancellationToken cancellationToken)
    {
        IResult? invalid = HttpHelpers.Validate(input);
        if (invalid is not null)
        {
            return invalid;
        }

        var result = await service.CreateAsync(input!, user.ToUserContext(), cancellationToken);
        return result.ToHttp(dto => Results.Created($"/api/expenses/{dto.Id}", dto));
    }

    private static async Task<IResult> UpdateAsync(Guid id, ExpenseInput? input, ClaimsPrincipal user, ExpenseService service, CancellationToken cancellationToken)
    {
        IResult? invalid = HttpHelpers.Validate(input);
        if (invalid is not null)
        {
            return invalid;
        }

        var result = await service.UpdateAsync(id, input!, user.ToUserContext(), cancellationToken);
        return result.ToHttp(value => Results.Ok(value));
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, ExpenseService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(user.ToUserContext(), cancellationToken));

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, ExpenseService service, CancellationToken cancellationToken) =>
        (await service.GetAsync(id, user.ToUserContext(), cancellationToken)).ToHttp(value => Results.Ok(value));

    private static async Task<IResult> HistoryAsync(Guid id, ClaimsPrincipal user, ExpenseService service, CancellationToken cancellationToken) =>
        (await service.GetHistoryAsync(id, user.ToUserContext(), cancellationToken)).ToHttp(value => Results.Ok(value));

    private static async Task<IResult> SubmitAsync(Guid id, ClaimsPrincipal user, ExpenseService service, CancellationToken cancellationToken) =>
        (await service.SubmitAsync(id, user.ToUserContext(), cancellationToken)).ToHttp(value => Results.Ok(value));

    private static async Task<IResult> ApproveAsync(Guid id, ClaimsPrincipal user, ExpenseService service, CancellationToken cancellationToken) =>
        (await service.ApproveAsync(id, user.ToUserContext(), cancellationToken)).ToHttp(value => Results.Ok(value));

    private static async Task<IResult> RejectAsync(Guid id, RejectInput? input, ClaimsPrincipal user, ExpenseService service, CancellationToken cancellationToken)
    {
        IResult? invalid = HttpHelpers.Validate(input);
        if (invalid is not null)
        {
            return invalid;
        }

        return (await service.RejectAsync(id, input!, user.ToUserContext(), cancellationToken)).ToHttp(value => Results.Ok(value));
    }

    private static async Task<IResult> PayAsync(Guid id, ClaimsPrincipal user, ExpenseService service, CancellationToken cancellationToken) =>
        (await service.PayAsync(id, user.ToUserContext(), cancellationToken)).ToHttp(value => Results.Ok(value));
}
