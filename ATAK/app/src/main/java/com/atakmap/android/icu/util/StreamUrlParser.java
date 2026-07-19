package com.atakmap.android.icu.util;

import com.atakmap.android.icu.serve.MediaServerConfig;

import java.net.URI;

/**
 * Parses the plain-text stream URL a QR code encodes back into its parts, so the
 * settings dialog can fill itself in from a scan instead of manual entry. Mirrors
 * {@link MediaServerConfig#pushUrl()} / {@code viewUrl()} in reverse:
 *
 * <pre>
 *   rtsp://{address}:{port}/{path}
 *   rtmp://{address}:{port}/{path}
 *   srt://{address}:{port}?streamid={read|publish}:{path}[&amp;passphrase=...]
 * </pre>
 *
 * The QR itself is just that raw string — no JSON/XML/custom scheme — so this is a
 * plain URI parse, not a custom format decode.
 */
public final class StreamUrlParser {

    private StreamUrlParser() {}

    public static class Parsed {
        public final MediaServerConfig.PushProtocol protocol;
        public final String host;
        public final int port;
        public final String path;
        public final String passphrase; // SRT only; null if absent

        Parsed(MediaServerConfig.PushProtocol protocol, String host, int port,
                String path, String passphrase) {
            this.protocol = protocol;
            this.host = host;
            this.port = port;
            this.path = path;
            this.passphrase = passphrase;
        }
    }

    /** @throws IllegalArgumentException if {@code raw} isn't one of the three supported shapes. */
    public static Parsed parse(String raw) {
        if (raw == null || raw.trim().isEmpty())
            throw new IllegalArgumentException("Empty QR payload");

        URI uri;
        try {
            uri = new URI(raw.trim());
        } catch (Exception e) {
            throw new IllegalArgumentException("Not a URI: " + e.getMessage());
        }

        String scheme = uri.getScheme();
        if (scheme == null)
            throw new IllegalArgumentException("Missing scheme (expected rtsp/rtmp/srt)");

        String host = uri.getHost();
        if (host == null || host.isEmpty())
            throw new IllegalArgumentException("Missing host");

        switch (scheme.toLowerCase()) {
            case "rtsp":
            case "rtmp": {
                MediaServerConfig.PushProtocol protocol = scheme.equalsIgnoreCase("rtsp")
                        ? MediaServerConfig.PushProtocol.RTSP : MediaServerConfig.PushProtocol.RTMP;
                int port = uri.getPort() >= 0 ? uri.getPort() : protocol.defaultPort;
                String path = stripLeadingSlash(uri.getPath());
                return new Parsed(protocol, host, port, path, null);
            }
            case "srt": {
                int port = uri.getPort() >= 0 ? uri.getPort()
                        : MediaServerConfig.PushProtocol.SRT.defaultPort;
                String streamId = null;
                String passphrase = null;
                String query = uri.getQuery();
                if (query != null) {
                    for (String param : query.split("&")) {
                        int eq = param.indexOf('=');
                        if (eq < 0) continue;
                        String key = param.substring(0, eq);
                        String value = param.substring(eq + 1);
                        if (key.equalsIgnoreCase("streamid")) streamId = value;
                        else if (key.equalsIgnoreCase("passphrase")) passphrase = value;
                    }
                }
                // streamid is "{read|publish}:{path}" — the direction tag isn't stored,
                // only the path (this plugin always publishes; "read" URLs are what
                // gets shared with viewers and is what's typically scanned back in).
                String path = "";
                if (streamId != null) {
                    int colon = streamId.indexOf(':');
                    path = colon >= 0 ? streamId.substring(colon + 1) : streamId;
                }
                return new Parsed(MediaServerConfig.PushProtocol.SRT, host, port, path, passphrase);
            }
            default:
                throw new IllegalArgumentException("Unsupported protocol: " + scheme);
        }
    }

    private static String stripLeadingSlash(String path) {
        if (path == null) return "";
        return path.startsWith("/") ? path.substring(1) : path;
    }
}
