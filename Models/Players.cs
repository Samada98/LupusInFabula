using System.Collections.Generic;

namespace LupusInTabula.Models
{
    public record PlayerDto(
        string Name,
        bool IsOnline,
        string Role,
        int Votes,
        bool Eliminated,
        string? CurrentVote,
        List<string> VotedBy,
        bool Silenced
    );

    public class Player
    {
        public string Name { get; set; } = "";
        public string? ConnectionId { get; set; }
        public bool IsOnline { get; set; } = true;
        public string Role { get; set; } = "";
        public bool Eliminated { get; set; } = false;
        public bool SilencedToday { get; set; } = false;
        public string? CurrentVote { get; set; }
    }
}
