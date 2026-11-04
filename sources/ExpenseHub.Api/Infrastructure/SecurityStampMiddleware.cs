using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ExpenseHub.Api.Infrastructure;

/// <summary>Invalida tokens emitidos antes de uma alteração de roles (o usuário precisa autenticar novamente).</summary>
internal sealed class SecurityStampMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityStampMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, UserManager<IdentityUser> userManager, IOptions<IdentityOptions> identityOptions)
    {
        ClaimsPrincipal principal = context.User;
        if (principal.Identity?.IsAuthenticated == true)
        {
            string? userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
            string? tokenStamp = principal.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
            IdentityUser? user = userId is null ? null : await userManager.FindByIdAsync(userId);
            string? currentStamp = user is null ? null : await userManager.GetSecurityStampAsync(user);
            if (user is null || tokenStamp is null || tokenStamp != currentStamp)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
        }

        await _next(context);
    }
}
