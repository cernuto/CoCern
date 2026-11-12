namespace CoCern.Contracts;

/// <summary>Shared HTTP/stdio resource projection. Contains no acting-chat credentials.</summary>
public sealed record SessionContextResourceReply(bool Ok, string Code, string Uri, string Text = "", long Version = 0);
