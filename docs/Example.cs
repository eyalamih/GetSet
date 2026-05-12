// Example: how to use the GetSet library in your own project.
//
// 1.  Add to your .csproj:
//         <PackageReference Include="GetSet" Version="1.0.0" />
//
// 2.  Add this to suppress the parse-error squiggles in your IDE:
//         <NoWarn>$(NoWarn);CS1001;CS1002;CS1513</NoWarn>
//
// 3.  Make your class partial and write the shorthand:

using System;
using GetSet; // optional — just for the [UseGetSet] attribute

namespace MyGame
{
    [UseGetSet]
    public partial class Player
    {
        // ── Health: clamped 0–100 ───────────────────────────────────────────
        public int health = 100
        {
            get { return health; }
            set { health = Math.Clamp(value, 0, 100); }
        }

        // ── Name: trimmed on the way in ─────────────────────────────────────
        public string playerName = "Hero"
        {
            get { return playerName; }
            set { playerName = value?.Trim() ?? string.Empty; }
        }

        // ── Score: never goes negative ──────────────────────────────────────
        public int score = 0
        {
            get { return score; }
            set { if (value >= 0) score = value; }
        }

        // ── Read-only computed property (getter only) ───────────────────────
        public string displayName = "Hero"
        {
            get { return $"{playerName} (Score: {score})"; }
        }
    }
}

// ── Generated output (you never write this — the generator does) ────────────
//
// namespace MyGame
// {
//     public partial class Player
//     {
//         private int _health = 100;
//         public int health
//         {
//             get { return Math.Clamp(_health, 0, 100); }
//             set { _health = Math.Clamp(value, 0, 100); }
//         }
//
//         private string _playerName = "Hero";
//         public string playerName
//         {
//             get { return _playerName; }
//             set { _playerName = value?.Trim() ?? string.Empty; }
//         }
//
//         private int _score = 0;
//         public int score
//         {
//             get { return _score; }
//             set { if (value >= 0) _score = value; }
//         }
//
//         private string _displayName = "Hero";
//         public string displayName
//         {
//             get { return $"{_playerName} (Score: {_score})"; }
//         }
//     }
// }
