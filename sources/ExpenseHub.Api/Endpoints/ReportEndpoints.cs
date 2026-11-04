using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using ExpenseHub.Api.Infrastructure;
using ExpenseHub.Core.Common;
using ExpenseHub.Core.Reports;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ExpenseHub.Api.Endpoints;

/// <summary>Rotas de relatório do CP3.</summary>
internal static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder group = app.MapGroup("/api/reports").RequireAuthorization().WithTags("Reports");

        group.MapPost(string.Empty, CreateAsync);
        group.MapGet(string.Empty, ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{id:guid}/excel", ExcelAsync);
        return app;
    }

    private static async Task<IResult> CreateAsync(CreateReportRequest? request, ClaimsPrincipal user, ReportService service, CancellationToken cancellationToken)
    {
        IResult? invalid = HttpHelpers.Validate(request);
        if (invalid is not null)
        {
            return invalid;
        }

        ServiceResult<ReportEnvelope> result = await service.CreateAsync(request!, user.ToUserContext(), cancellationToken);
        if (result.Status == ServiceStatus.Accepted && result.Value is not null)
        {
            return Results.Accepted($"/api/reports/{result.Value.Id}", result.Value);
        }

        if (result.Status == ServiceStatus.Unavailable && result.Value is not null)
        {
            return result.Error.ToProblem(result.Status, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = result.Value.Id,
                ["status"] = result.Value.Status.ToString(),
            });
        }

        return result.Error.ToProblem(result.Status);
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal user, ReportService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(user.ToUserContext(), cancellationToken));

    private static async Task<IResult> GetAsync(Guid id, ClaimsPrincipal user, ReportService service, CancellationToken cancellationToken)
    {
        ServiceResult<ReportEnvelope> result = await service.GetAsync(id, user.ToUserContext(), cancellationToken);
        return result.ToHttp(value => Results.Ok(value));
    }

    private static async Task<IResult> ExcelAsync(Guid id, ClaimsPrincipal user, ReportService service, CancellationToken cancellationToken)
    {
        ServiceResult<byte[]> result = await service.ExportExcelAsync(id, user.ToUserContext(), cancellationToken);
        return result.ToHttp(bytes => Results.File(bytes, ReportExcelBuilder.ContentType, ReportExcelBuilder.FileName(id)));
    }
}
