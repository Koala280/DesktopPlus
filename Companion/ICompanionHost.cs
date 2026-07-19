using System.Collections.Generic;
using System.Threading.Tasks;

namespace DesktopPlus.Companion
{
    /// <summary>
    /// Bridge the server uses to read live UI state (open panels) from the WPF side.
    /// Implemented by MainWindow, which marshals to the dispatcher.
    /// </summary>
    internal interface ICompanionHost
    {
        Task<IReadOnlyList<CompanionPanelInfo>> GetPanelsAsync();

        /// <summary>
        /// Returns the pinned item paths of a List-type panel, or null if the panel isn't found
        /// or isn't a list. Marshalled to the UI thread.
        /// </summary>
        Task<IReadOnlyList<string>?> GetPanelItemsAsync(string panelId);

        /// <summary>Navigates an open desktop panel to a folder (marshalled to the UI thread).</summary>
        Task<bool> NavigatePanelAsync(string panelId, string path);
    }

    internal sealed class CompanionPanelInfo
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Type { get; set; } = "";
        public string FolderPath { get; set; } = "";
    }
}
