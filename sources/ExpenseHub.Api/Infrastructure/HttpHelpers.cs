using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Security.Claims;
using ExpenseHub.Core.Common;
using Microsoft.AspNetCore.Http;

namespace ExpenseHub.Api.Infrastructure;

/// <summary>Conversões entre HTTP e a camada de serviço.</summary>
internal static class HttpHelpers
{
    /// <summary>Identidade sempre derivada do token autenticado.</summary>
    public static UserContext ToUserContext(this ClaimsPrincipal principal)
    {
        string userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Usuário autenticado sem identificador.");
        string[] roles = [.. principal.FindAll(ClaimTypes.Role).Select(c => c.Value)];
        return new UserContext(userId, roles);
    }

    /// <summary>Validação declarativa (DataAnnotations) dos DTOs.</summary>
    public static IResult? Validate<T>(T? input)
        where T : class
    {
        if (input is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Corpo da requisição obrigatório.");
        }

        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true))
        {
            return null;
        }

        Dictionary<string, string[]> errors = results
            .SelectMany(r => r.MemberNames.DefaultIfEmpty(string.Empty).Select(m => (Member: m, Message: r.ErrorMessage ?? "Inválido.")))
            .GroupBy(x => x.Member, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Message).ToArray(), StringComparer.Ordinal);
        return Results.ValidationProblem(errors);
    }

    public static IResult ToProblem(this ServiceError? error, ServiceStatus status, IDictionary<string, object?>? extensions = null)
    {
        int code = status switch
        {
            ServiceStatus.BadRequest => StatusCodes.Status400BadRequest,
            ServiceStatus.Forbidden => StatusCodes.Status403Forbidden,
            ServiceStatus.NotFound => StatusCodes.Status404NotFound,
            ServiceStatus.Conflict => StatusCodes.Status409Conflict,
            ServiceStatus.Unavailable => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status500InternalServerError,
        };
        var ext = new Dictionary<string, object?>(extensions ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            ["code"] = error?.Code,
        };
        return Results.Problem(statusCode: code, title: error?.Message, extensions: ext);
    }

    public static IResult ToHttp<T>(this ServiceResult<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.Status is ServiceStatus.Ok or ServiceStatus.Created or ServiceStatus.Accepted && result.Value is not null
            ? onSuccess(result.Value)
            : result.Error.ToProblem(result.Status);
    }
}
