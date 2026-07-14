using System.ComponentModel.Composition;
using Prism.Mef.Modularity;
using Prism.Modularity;

namespace ICUVideoStreamer
{
    [ModuleExport(typeof(VideoStreamModule),
        InitializationMode = InitializationMode.WhenAvailable)]
    public class VideoStreamModule : IModule
    {
        public void Initialize() { }
    }
}
