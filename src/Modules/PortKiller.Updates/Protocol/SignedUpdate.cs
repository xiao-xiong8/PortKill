namespace PortKiller.Updates;

public sealed class SignedUpdate
{
    // 签名覆盖解码后的原始 UTF-8 payload，不依赖 JSON 属性排序。
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}
