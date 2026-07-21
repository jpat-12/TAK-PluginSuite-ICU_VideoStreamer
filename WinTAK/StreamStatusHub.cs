using System;

namespace ICUVideoStreamer
{
    /// <summary>
    /// One-line bridge between the dock pane (which owns the actual streaming state) and
    /// the free-floating <see cref="StreamStatusWidget"/> (owned by the module, created at
    /// plugin load). The pane is the single source of truth — it writes <see cref="IsStreaming"/>;
    /// the widget only reads it and reacts to <see cref="StreamingChanged"/>. This mirrors the
    /// ATAK build, where <c>ICUVideoMapComponent</c> owns the map widget independently of the
    /// drop-down pane so status stays visible whether or not the pane is open.
    /// </summary>
    public static class StreamStatusHub
    {
        private static bool _isStreaming;

        /// <summary>Raised (with the new value) whenever streaming starts or stops.</summary>
        public static event Action<bool> StreamingChanged;

        public static bool IsStreaming
        {
            get => _isStreaming;
            set
            {
                if (_isStreaming == value) return;
                _isStreaming = value;
                StreamingChanged?.Invoke(value);
            }
        }
    }
}
