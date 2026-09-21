using Microsoft.AspNetCore.SignalR;

namespace AudioStreaming.Backend.Hubs;

public sealed class AudioHub : Hub
{
    public static string Group(Guid sessionId) => $"audio:{sessionId:N}";
    public Task JoinSession(Guid sessionId) => Groups.AddToGroupAsync(Context.ConnectionId, Group(sessionId));
    public Task LeaveSession(Guid sessionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(sessionId));
}
