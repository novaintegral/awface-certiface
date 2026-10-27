using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace AWFace.Api.Services;

/// <summary>
/// Converte exceções não tratadas em respostas ProblemDetails (RFC 7807) com traceId,
/// sem expor detalhes internos ao cliente.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            BadHttpRequestException badRequest => (badRequest.StatusCode, "Requisição inválida", "O corpo da requisição é inválido ou está em formato inesperado."),
            ArgumentException argument => (StatusCodes.Status400BadRequest, "Requisição inválida", argument.Message),
            JsonException => (StatusCodes.Status400BadRequest, "Requisição inválida", "JSON inválido."),
            CertifaceProviderException provider => (StatusCodes.Status502BadGateway, "Falha no provedor de liveness", $"A Certiface não respondeu como esperado na operação '{provider.Operation}'."),
            OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested => (499, "Requisição cancelada", "A conexão foi encerrada pelo cliente."),
            TimeoutException or TaskCanceledException => (StatusCodes.Status504GatewayTimeout, "Tempo esgotado", "O processamento excedeu o tempo limite."),
            _ => (StatusCodes.Status500InternalServerError, "Erro interno", "Ocorreu um erro inesperado. Informe o traceId ao suporte."),
        };

        if (status >= 500)
        {
            _logger.LogError(exception, "Erro não tratado em {Method} {Path}. TraceId={TraceId}.", httpContext.Request.Method, httpContext.Request.Path, httpContext.TraceIdentifier);
        }
        else
        {
            _logger.LogWarning("Requisição rejeitada em {Method} {Path}: {Detail}. TraceId={TraceId}.", httpContext.Request.Method, httpContext.Request.Path, detail, httpContext.TraceIdentifier);
        }

        if (httpContext.Response.HasStarted)
        {
            return true;
        }

        httpContext.Response.StatusCode = status;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Detail = detail,
                Extensions = { ["traceId"] = httpContext.TraceIdentifier }
            }
        });
    }
}
