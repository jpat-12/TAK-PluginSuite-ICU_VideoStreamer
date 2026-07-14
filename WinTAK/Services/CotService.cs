// CotService is superseded by ICotMessageSender (WinTAK's native CoT pipeline).
// Kept as an empty stub so the csproj compile list doesn't need editing.
namespace ICUVideoStreamer.Services
{
    internal sealed class CotService : System.IDisposable
    {
        public CotService(Models.AppSettings _) { }
        public void Dispose() { }
    }
}
