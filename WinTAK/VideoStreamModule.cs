using System;
using System.ComponentModel.Composition;
using System.Windows;
using ICUVideoStreamer.Models;
using Prism.Mef.Modularity;
using Prism.Modularity;
using WinTak.Framework.Docking;

namespace ICUVideoStreamer
{
    [ModuleExport(typeof(VideoStreamModule),
        InitializationMode = InitializationMode.WhenAvailable)]
    public class VideoStreamModule : IModule
    {
        private readonly IDockingManager _dockingManager;
        private StreamStatusWidget _statusWidget;

        [ImportingConstructor]
        public VideoStreamModule(IDockingManager dockingManager)
        {
            _dockingManager = dockingManager;
        }

        public void Initialize()
        {
            // Create the always-on map status widget at plugin load, independent of the
            // dock pane — the ATAK build does the same via ICUVideoMapComponent so the
            // streaming indicator is visible whether or not the pane has been opened.
            ShowStatusWidget();
        }

        private void ShowStatusWidget()
        {
            var app = Application.Current;
            if (app == null) return;

            app.Dispatcher.InvokeAsync(() =>
            {
                var mw = app.MainWindow;
                // Owner can only be a Window that has already been shown. During module init
                // the main window may be missing or not yet loaded — defer until it is.
                if (mw != null && mw.IsLoaded)
                    CreateWidget();
                else if (mw != null)
                    mw.Loaded += OnOwnerLoadedOnce;
                else
                    app.Activated += OnAppActivatedOnce;
            });
        }

        private void OnOwnerLoadedOnce(object sender, RoutedEventArgs e)
        {
            ((Window)sender).Loaded -= OnOwnerLoadedOnce;
            CreateWidget();
        }

        private void OnAppActivatedOnce(object sender, EventArgs e)
        {
            Application.Current.Activated -= OnAppActivatedOnce;
            CreateWidget();
        }

        private void CreateWidget()
        {
            if (_statusWidget != null) return;
            try
            {
                _statusWidget = new StreamStatusWidget(_dockingManager, AppSettings.Load())
                {
                    Owner = Application.Current.MainWindow
                };
                _statusWidget.Show();
            }
            catch
            {
                // Widget is a convenience HUD — never let a failure here block the plugin.
                _statusWidget = null;
            }
        }
    }
}
