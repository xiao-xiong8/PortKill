using PortKiller.Models;
using PortKiller.Services;

namespace PortKiller.Modules.Ports.Abstractions;

public interface ITcpConnectionResetter
{
    OperationResult ResetTcpConnection(PortConnection connection);
}
