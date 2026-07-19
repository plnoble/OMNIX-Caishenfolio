namespace Caishenfolio.Host.Security;

[Flags]
public enum ToolCapability
{
    None = 0,
    ReadOnly = 1 << 0,
    FileRead = 1 << 1,
    FileWrite = 1 << 2,
    Network = 1 << 3,
    Shell = 1 << 4,
    GeneratedCode = 1 << 5,
    ExternalWidget = 1 << 6,
}
