// Dots.cs
using System.Collections.Generic;

namespace LupusInTabula.Models
{
    public record JoinResult(
        bool Ok,
        string? Error,
        string RoomId,
        string HostName,
        bool IsHost,
        bool GameStarted,
        bool VotingOpen,
        string? Role,
        List<PlayerDto> Players
    );
}
