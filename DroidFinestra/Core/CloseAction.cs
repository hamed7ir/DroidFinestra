using System;

namespace DroidFinestra.Core
{
    /// <summary>What the main window's ✕ does — Finestra's CloseAction (its Startup.cs), without the run-at-startup key.</summary>
    public enum CloseAction { Ask, MinimizeToTray, Exit }

    public static class CloseActions
    {
        public static CloseAction Parse(string s)
        {
            CloseAction a;
            return Enum.TryParse(s, true, out a) ? a : CloseAction.Ask;
        }
    }
}
