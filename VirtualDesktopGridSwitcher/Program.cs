using VirtualDesktopGridSwitcher.Settings;

namespace VirtualDesktopGridSwitcher {
    static class Program {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main() {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var settings = SettingValues.Load();
            using (SysTrayProcess sysTrayProcess = new SysTrayProcess(settings)) {

                using (VirtualDesktopGridManager gridManager =
                       new VirtualDesktopGridManager(sysTrayProcess, settings)) {
                    
                    // Make sure the application runs!
                    Application.Run();
                }

            }
        }
    
    }
}