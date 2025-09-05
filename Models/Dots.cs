using System.Collections.Generic;

namespace LupusInTabula.Models
{
    public record PlayerDto(
        string Name,
        bool IsOnline,
        string Role,
        int Votes,
        bool Eliminated = false,
        string? CurrentVote = null,
        List<string>? VotedBy = null // <-- AGGIUNTO
    );


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

