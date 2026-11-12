namespace JarvisHud.Core;

/// <summary>Agent identity and runtime. Registration is not a live health check.</summary>
public sealed record AgentInventoryItem(string Name, string Detail, string Status);
