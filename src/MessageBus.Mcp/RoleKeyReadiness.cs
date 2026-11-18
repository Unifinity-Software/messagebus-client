namespace MessageBus.Mcp;

/// <summary>A local key refusal, kept beside the runner's answer even when inbox could not open it.</summary>
public static class RoleKeyReadiness
{
    public const string FieldPrefix = "role-key:";
    public static string FileName(string settleFile) => settleFile + ".role-key";
}
