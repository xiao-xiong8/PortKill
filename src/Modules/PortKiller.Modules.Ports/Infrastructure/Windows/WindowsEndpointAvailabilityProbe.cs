using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PortKiller.Docker;

public sealed class WindowsEndpointAvailabilityProbe : IEndpointAvailabilityProbe
{
    public Task<(bool? Available, string Evidence)> ProbeAsync(HostEndpoint endpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            bool tcp = endpoint.Transport == PortTransport.Tcp;
            using var socket = new Socket(endpoint.Address.AddressFamily, tcp ? SocketType.Stream : SocketType.Dgram,
                tcp ? ProtocolType.Tcp : ProtocolType.Udp) { ExclusiveAddressUse = true };
            if (endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6) socket.DualMode = false;
            socket.Bind(new System.Net.IPEndPoint(endpoint.Address, endpoint.Port));
            return Task.FromResult<(bool?, string)>((true, "对应协议和地址的独占绑定探测通过。"));
        }
        catch (SocketException error)
        {
            bool? available = error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied ? false : null;
            return Task.FromResult((available, $"{endpoint} 绑定探测失败 [Socket {error.ErrorCode}]：{error.Message}；尚未确定是否为转发/保留残留。"));
        }
    }
}
