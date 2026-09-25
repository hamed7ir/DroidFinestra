namespace DroidFinestra.Core
{
    /// <summary>
    /// The app's identity strings in one place. Every user-visible or on-disk use of the name reads from
    /// here (the assembly name / namespace are set in the .csproj).
    ///
    /// ⚠ <see cref="DataFolder"/> is where saved profiles live (Documents\&lt;DataFolder&gt;). Changing it after
    /// people have saved profiles makes them start empty.
    /// </summary>
    public static class AppIdentity
    {
        /// <summary>Shown in title bars, dialogs and the log banner.</summary>
        public const string Name = "DroidFinestra";

        public const string Description = "Use your Android phone from ARM32 Windows - a GUI for scrcpy-rt";

        /// <summary>Prefix for the single-instance mutex/pipe and the app log file name. Distinct from
        /// Finestra's, so the two apps never answer each other's second launch.</summary>
        public const string Id = "DroidFinestra";

        /// <summary>Folder name under Documents (or %APPDATA%, or beside the exe) — see StoragePaths.</summary>
        public const string DataFolder = "DroidFinestra";

        /// <summary>A file with this name beside the exe = portable mode (Finestra's mechanism).</summary>
        public const string PortableSentinel = "DroidFinestra.portable";
    }
}
