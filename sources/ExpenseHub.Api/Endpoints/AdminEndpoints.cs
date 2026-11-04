using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Api.Infrastructure;
using ExpenseHub.Core.Common;
using ExpenseHub.Core.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace ExpenseHub.Api.Endpoints;

/// <summary>Administração de usuários e roles (somente Admin).</summary>
internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/admin/users")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin))
            .WithTags("Admin");
        group.MapGet(string.Empty, ListUsersAsync);
        group.MapPut("/{id}/roles", SetRolesAsync);
        return app;
    }

    private static async Task<IResult> ListUsersAsync(UserManager<IdentityUser> userManager, CancellationToken cancellationToken)
    {
        List<IdentityUser> users = await userManager.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(cancellationToken);
        var result = new List<UserDto>(users.Count);
        foreach (IdentityUser user in users)
        {
            IList<string> roles = await userManager.GetRolesAsync(user);
            result.Add(new UserDto(user.Id, user.Email, [.. roles.Order(StringComparer.Ordinal)]));
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> SetRolesAsync(string id, SetRolesInput? input, ClaimsPrincipal principal, UserManager<IdentityUser> userManager)
    {
        IResult? invalid = HttpHelpers.Validate(input);
        if (invalid is not null)
        {
            return invalid;
        }

        string[] requested = [.. input!.Roles!.Distinct(StringComparer.Ordinal)];
        string[] unknown = [.. requested.Where(r => !Roles.All.Contains(r, StringComparer.Ordinal))];
        if (unknown.Length > 0)
        {
            return new ServiceError("unknown_role", "Roles desconhecidas: " + string.Join(", ", unknown)).ToProblem(ServiceStatus.BadRequest);
        }

        IdentityUser? user = await userManager.FindByIdAsync(id);
        if (user is null)
        {
            return new ServiceError("user_not_found", "Usuário não encontrado.").ToProblem(ServiceStatus.NotFound);
        }

        string selfId = principal.ToUserContext().UserId;
        if (user.Id == selfId && !requested.Contains(Roles.Admin, StringComparer.Ordinal))
        {
            return new ServiceError("cannot_remove_own_admin", "Não é permitido remover a própria role Admin.").ToProblem(ServiceStatus.Conflict);
        }

        IList<string> current = await userManager.GetRolesAsync(user);
        IdentityResult removed = await userManager.RemoveFromRolesAsync(user, current.Except(requested, StringComparer.Ordinal));
        IdentityResult added = await userManager.AddToRolesAsync(user, requested.Except(current, StringComparer.Ordinal));
        if (!removed.Succeeded || !added.Succeeded)
        {
            return new ServiceError("role_update_failed", "Não foi possível atualizar as roles.").ToProblem(ServiceStatus.BadRequest);
        }

        await userManager.UpdateSecurityStampAsync(user);
        return Results.Ok(new UserDto(user.Id, user.Email, [.. requested.Order(StringComparer.Ordinal)]));
    }

    private sealed record UserDto(string Id, string? Email, IReadOnlyList<string> Roles);

    private sealed class SetRolesInput
    {
        [Required]
        public IReadOnlyList<string>? Roles { get; init; }
    }
}
