using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DesktopPlus
{
    public class DesktopAutoSortSettings
    {
        public bool AutoSortEnabled { get; set; }
        public List<DesktopSortRuleState> Rules { get; set; } = new List<DesktopSortRuleState>();
    }

    public class DesktopSortRuleState
    {
        public string RuleId { get; set; } = "";
        public bool IsBuiltIn { get; set; }
        public bool Enabled { get; set; } = true;
        public bool MatchFolders { get; set; }
        public bool CatchAll { get; set; }
        public string RuleName { get; set; } = "";
        public string TargetPanelName { get; set; } = "";
        public List<string> Extensions { get; set; } = new List<string>();

        [JsonIgnore]
        public string DisplayName { get; set; } = "";

        [JsonIgnore]
        public string ExtensionsSummary { get; set; } = "";
    }
}
