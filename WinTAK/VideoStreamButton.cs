using System.ComponentModel.Composition;
using WinTak.Framework.Docking;
using WinTak.Framework.Tools;
using WinTak.Framework.Tools.Attributes;

namespace ICUVideoStreamer
{
    [Button("ICUVideoStreamer_VideoStreamButton", "ICU VideoStreamer",
        LargeImage = "pack://application:,,,/ICUVideoStreamer;component/assets/Large.png",
        SmallImage = "pack://application:,,,/ICUVideoStreamer;component/assets/Small.png",
        // Home tab, "Tools" section — next to WinTAK's Video Player. Plugins must use the
        // literal display names (WinTAK's own RadioControls button uses "Home"/"Tools");
        // the internal resource keys ("HomeTab"/"ToolsTabGroup") make a separate tab/group.
        Tab        = "Home",
        TabGroup   = "Tools",
        ToolTip    = "Open the ICU VideoStreamer plugin")]
    [Export(typeof(Button))]
    public class VideoStreamButton : Button
    {
        private readonly IDockingManager _dockingManager;

        [ImportingConstructor]
        public VideoStreamButton(IDockingManager dockingManager)
        {
            _dockingManager = dockingManager;
        }

        protected override void OnClick()
        {
            base.OnClick();
            var pane = _dockingManager.GetDockPane(VideoStreamDockPane.ID);
            pane?.Activate();
        }
    }
}
