namespace ShadowsMcp
{
    /// <summary>
    /// Runtime configuration. Defaults suit the "game PC + client elsewhere on the LAN" setup;
    /// values can be overridden from the game's per-mod config options (see ModCore's
    /// receiveModConfigOpts_* overrides).
    /// </summary>
    public sealed class ModConfig
    {
        /// <summary>Listen on all interfaces (LAN-reachable). false → 127.0.0.1 only.</summary>
        public bool ListenLan = true;

        /// <summary>Hide the archetype ability previews in list_recruitable_agents so an AI can
        /// discover the game blind. false (default) → previews shown.</summary>
        public bool DiscoveryMode = false;

        public int Port = 8017;

        /// <summary>How long an HTTP worker waits for the main thread to run one tool.</summary>
        public int ToolTimeoutMs = 10000;

        /// <summary>How long end_turn waits for the turn number to advance.</summary>
        public int EndTurnTimeoutMs = 60000;

        /// <summary>How long new_game waits for map generation + burn-in (a large map can
        /// take minutes; the started job runs to completion even if this expires).</summary>
        public int NewGameTimeoutMs = 180000;
    }
}
