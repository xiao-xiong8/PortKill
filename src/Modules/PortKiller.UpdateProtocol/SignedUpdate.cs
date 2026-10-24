namespace PortKiller.Updates;

public sealed class SignedUpdate
{
    // 客户端、发布工具和通知服务共用原始 payload 签名格式。
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}
