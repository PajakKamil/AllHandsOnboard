namespace AllHandsOnboard
{
    /// <summary>
    /// ZDO keys used to sync rower state.
    /// Hashed once; FNV-1a 32-bit is deterministic across runs and safe to persist in ZDOs.
    /// Keys are mod-specific (prefix "rowing_") so collisions with Valheim or other mods are unlikely.
    /// </summary>
    public static class RowingZdoKeys
    {
        public static readonly int[] SlotTempo = new int[Plugin.MaxRowers];
        public static readonly int[] SlotPlayer = new int[Plugin.MaxRowers];

        public static readonly int[] SlotLastUpdate = new int[Plugin.MaxRowers];

        // Absolute ZNet time of the last stroke - used for crew phase sync.
        // All clients read the same ZNet time, so stroke phases can be compared between players.
        public static readonly int[] SlotLastStroke = new int[Plugin.MaxRowers];

        // Per-slot streak (PERFECT chain) - synced together with the stroke event.
        // Used for the mastery factor: a long rhythmic streak grants an extra speed bonus,
        // distinguishing disciplined rowing from L/R spam.
        public static readonly int[] SlotStreak = new int[Plugin.MaxRowers];

        static RowingZdoKeys()
        {
            for (int i = 0; i < Plugin.MaxRowers; i++)
            {
                SlotTempo[i] = StableHash($"rowing_tempo_{i}");
                SlotPlayer[i] = StableHash($"rowing_player_{i}");
                SlotLastUpdate[i] = StableHash($"rowing_last_{i}");
                SlotLastStroke[i] = StableHash($"rowing_strokeat_{i}");
                SlotStreak[i] = StableHash($"rowing_streak_{i}");
            }
        }

        private static int StableHash(string s)
        {
            // FNV-1a 32-bit
            int hash = unchecked((int)2166136261u);
            for (int i = 0; i < s.Length; i++)
            {
                hash = unchecked((hash ^ s[i]) * 16777619);
            }

            return hash;
        }
    }
}