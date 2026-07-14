using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: AssemblyTitle("ICUVideoStreamer")]
[assembly: AssemblyDescription("WinTAK video streaming plugin with CoT integration")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("ICUVideoStreamer")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyTrademark("")]
[assembly: ComVisible(false)]
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// WinTAK plugin identity — TakSdkVersion must match installed WinTAK version
[assembly: WinTak.Framework.TakSdkVersion("5.6.0.151")]
[assembly: WinTak.Framework.PluginName("ICU VideoStreamer")]
[assembly: WinTak.Framework.PluginDescription("FFmpeg-based video streaming with CoT SA and TAK Server integration.")]
