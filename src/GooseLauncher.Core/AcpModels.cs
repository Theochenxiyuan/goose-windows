namespace GooseLauncher.Core;

public enum AcpUpdateKind { Message, Thought, Tool, Plan, Other }

public sealed record AcpUpdate(AcpUpdateKind Kind, string Text, string? ToolCallId = null, string? Status = null);

public sealed record AcpPermissionOption(string Id, string Label, string Kind);

public sealed record AcpPermissionRequest(string ToolCallId, string Title, IReadOnlyList<AcpPermissionOption> Options);

public sealed record AcpPermissionDecision(string? OptionId)
{
    public static AcpPermissionDecision Cancelled { get; } = new((string?)null);
}
