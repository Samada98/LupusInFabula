namespace LupusInTabula.Models
{
    public class Player
    {
        public string Name { get; set; } = "";
        public string ConnectionId { get; set; } = "";
        public bool IsOnline { get; set; }
        public string Role { get; set; } = "";          // Wolf, Villager, Seer, Guard
        public string? CurrentVote { get; set; }        // Nome target votato (se presente)
        public bool Eliminated { get; set; } = false;
    }
}
