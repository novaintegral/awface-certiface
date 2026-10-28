using System.Net.Security;
using System.Net.Sockets;
using AWFace.Api.Configuration;

namespace AWFace.Api.Services;

/// <summary>
/// Handler HTTP das chamadas ao Tenant (webhook e token OAuth2).
/// <para>
/// Quando a AWFace.Api roda em container, "localhost" passa a ser o próprio container e o
/// Host instalado na máquina (ex.: Portal em https://localhost:44305) deixa de ser alcançável.
/// Com <see cref="WebhookOptions.LocalhostAlias"/> configurado (ex.: host.docker.internal),
/// conexões para localhost/127.0.0.1 são encaminhadas ao alias, mantendo a URL original
/// (Host header e SNI continuam "localhost"). Nesse modo, o certificado de desenvolvimento
/// apresentado pelo Host em localhost é aceito; demais destinos seguem a validação TLS normal.
/// </para>
/// </summary>
public static class TenantCallbackHttpHandler
{
    private static readonly string[] LoopbackNames = ["localhost", "127.0.0.1", "::1", "[::1]"];

    public static SocketsHttpHandler Create(WebhookOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 2, 60)),
        };

        var alias = options.LocalhostAlias?.Trim();
        if (string.IsNullOrWhiteSpace(alias))
        {
            return handler;
        }

        handler.ConnectCallback = async (context, cancellationToken) =>
        {
            var host = IsLoopback(context.DnsEndPoint.Host) ? alias : context.DnsEndPoint.Host;
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(host, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };

        handler.SslOptions.RemoteCertificateValidationCallback = (sender, _, _, errors) =>
            errors == SslPolicyErrors.None || IsLoopback(GetTargetHost(sender));

        return handler;
    }

    private static string? GetTargetHost(object? sender)
    {
        return sender switch
        {
            HttpRequestMessage request => request.RequestUri?.Host,
            SslStream stream => stream.TargetHostName,
            _ => null
        };
    }

    public static bool IsLoopback(string? host)
    {
        return host is not null && LoopbackNames.Any(item => string.Equals(item, host, StringComparison.OrdinalIgnoreCase));
    }
}
