using System.Collections.Generic;
using System.Linq;

namespace ExpenseHub.Core.Common;

/// <summary>Classificação do resultado de uma operação de serviço.</summary>
internal enum ServiceStatus
{
    Ok,
    Created,
    Accepted,
    BadRequest,
    Forbidden,
    NotFound,
    Conflict,
    Unavailable,
}

/// <summary>Erro estável e sanitizado.</summary>
internal sealed record ServiceError(string Code, string Message);

/// <summary>Identidade autenticada, obtida exclusivamente do token.</summary>
internal sealed record UserContext(string UserId, IReadOnlyCollection<string> Roles)
{
    public bool IsInRole(string role) => Roles.Contains(role);
}

/// <summary>Resultado de serviço sem exceções para fluxos esperados.</summary>
/// <typeparam name="T">Tipo do valor.</typeparam>
internal sealed class ServiceResult<T>
{
    private ServiceResult(ServiceStatus status, T? value, ServiceError? error)
    {
        Status = status;
        Value = value;
        Error = error;
    }

    public ServiceStatus Status { get; }

    public T? Value { get; }

    public ServiceError? Error { get; }

    public static ServiceResult<T> Success(T value, ServiceStatus status = ServiceStatus.Ok) => new(status, value, null);

    public static ServiceResult<T> Fail(ServiceStatus status, string code, string message) => new(status, default, new ServiceError(code, message));

    public static ServiceResult<T> FailWithValue(ServiceStatus status, T value, string code, string message) => new(status, value, new ServiceError(code, message));
}
