package io.github.ghermin.camtel

import android.util.Log
import java.io.BufferedOutputStream
import java.io.IOException
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URLDecoder
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicInteger

/**
 * Minimal HTTP server:
 *  GET /video    -> MJPEG stream (multipart/x-mixed-replace)
 *  GET /status   -> JSON status (?since=<version> long-polls until the state changes)
 *  GET /control  -> apply query parameters, returns JSON status
 *  GET /         -> tiny HTML viewer
 */
class StreamServer(
    private val port: Int,
    private val hub: FrameHub,
    private val controller: Controller,
) {
    interface Controller {
        fun statusJson(): String
        fun awaitChange(since: Long, timeoutMs: Long)
        fun control(params: Map<String, String>)
        fun onClientsChanged(count: Int)
    }

    private val clients = AtomicInteger(0)
    private val pool = Executors.newCachedThreadPool()
    @Volatile private var server: ServerSocket? = null

    val clientCount: Int get() = clients.get()

    fun start() {
        val ss = ServerSocket()
        ss.reuseAddress = true
        ss.bind(InetSocketAddress(port))
        server = ss
        pool.execute {
            while (!ss.isClosed) {
                try {
                    val socket = ss.accept()
                    pool.execute { handle(socket) }
                } catch (e: IOException) {
                    if (!ss.isClosed) Log.w(TAG, "accept failed", e)
                }
            }
        }
    }

    fun stop() {
        try {
            server?.close()
        } catch (_: IOException) {
        }
        pool.shutdownNow()
    }

    private fun handle(socket: Socket) {
        socket.use { s ->
            try {
                s.tcpNoDelay = true
                s.soTimeout = 10_000
                val reader = s.getInputStream().bufferedReader(Charsets.ISO_8859_1)
                val requestLine = reader.readLine() ?: return
                val headers = HashMap<String, String>()
                while (headers.size < 100) {
                    val line = reader.readLine() ?: break
                    if (line.isEmpty()) break
                    val colon = line.indexOf(':')
                    if (colon > 0) headers[line.substring(0, colon).trim().lowercase()] = line.substring(colon + 1).trim()
                }
                val target = requestLine.split(' ').getOrNull(1) ?: "/"
                val path = target.substringBefore('?')
                val query = parseQuery(target.substringAfter('?', ""))
                val out = BufferedOutputStream(s.getOutputStream(), 128 * 1024)
                if (path != "/" && !allowed(headers)) {
                    respond(out, "403 Forbidden", "text/plain", "forbidden")
                    return
                }
                when (path) {
                    "/video", "/mjpeg", "/video.mjpg" -> streamVideo(s, out)
                    "/status" -> {
                        query["since"]?.toLongOrNull()?.let {
                            s.soTimeout = 0
                            controller.awaitChange(it, 20_000)
                        }
                        respond(out, "200 OK", "application/json", controller.statusJson())
                    }
                    "/control" -> {
                        controller.control(query)
                        respond(out, "200 OK", "application/json", controller.statusJson())
                    }
                    "/" -> respond(out, "200 OK", "text/html; charset=utf-8", INDEX_HTML)
                    else -> respond(out, "404 Not Found", "text/plain", "not found")
                }
            } catch (_: IOException) {
                // Client went away.
            }
        }
    }

    private fun streamVideo(socket: Socket, out: OutputStream) {
        socket.soTimeout = 0
        socket.sendBufferSize = 512 * 1024
        out.write(
            ("HTTP/1.1 200 OK\r\n" +
                "Content-Type: multipart/x-mixed-replace; boundary=$BOUNDARY\r\n" +
                "Cache-Control: no-cache, no-store\r\n" +
                "Pragma: no-cache\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                "Connection: close\r\n\r\n").toByteArray(Charsets.ISO_8859_1)
        )
        out.flush()
        controller.onClientsChanged(clients.incrementAndGet())
        try {
            var seq = 0L
            while (!socket.isClosed) {
                val next = hub.await(seq, 2000) ?: continue
                seq = next.first
                val jpeg = next.second
                out.write(
                    "--$BOUNDARY\r\nContent-Type: image/jpeg\r\nContent-Length: ${jpeg.size}\r\n\r\n"
                        .toByteArray(Charsets.ISO_8859_1)
                )
                out.write(jpeg)
                out.write(CRLF)
                out.flush()
            }
        } catch (_: InterruptedException) {
        } finally {
            controller.onClientsChanged(clients.decrementAndGet())
        }
    }

    private fun respond(out: OutputStream, status: String, type: String, body: String) {
        val bytes = body.toByteArray(Charsets.UTF_8)
        out.write(
            ("HTTP/1.1 $status\r\nContent-Type: $type\r\nContent-Length: ${bytes.size}\r\n" +
                "X-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\n" +
                "Cache-Control: no-cache\r\nConnection: close\r\n\r\n")
                .toByteArray(Charsets.ISO_8859_1)
        )
        out.write(bytes)
        out.flush()
    }

    /**
     * The PC app (and the built-in viewer at /) talk to the phone by IP address. Refuse what only a
     * web page from another site could send: a domain name in Host (DNS rebinding), a cross-site
     * Sec-Fetch-Site, or an Origin that is not this server. No CORS header is sent, so other sites
     * cannot read the camera image or the status either.
     */
    private fun allowed(headers: Map<String, String>): Boolean {
        val host = headers["host"]?.lowercase() ?: return true
        val name = if (host.startsWith("[")) host.substringBefore(']') + "]" else host.substringBefore(':')
        val ipLiteral = name == "localhost" || IPV4.matches(name) || IPV6.matches(name)
        if (!ipLiteral) return false
        val site = headers["sec-fetch-site"]
        if (site != null && site != "same-origin" && site != "none") return false
        val origin = headers["origin"] ?: return true
        return origin == "http://$host"
    }

    private fun parseQuery(q: String): Map<String, String> =
        q.split('&').filter { it.isNotEmpty() }.associate {
            val k = URLDecoder.decode(it.substringBefore('='), "UTF-8")
            val v = URLDecoder.decode(it.substringAfter('=', ""), "UTF-8")
            k to v
        }

    companion object {
        private const val TAG = "CamTelServer"
        private const val BOUNDARY = "camtelframe"
        private val CRLF = "\r\n".toByteArray()
        private val IPV4 = Regex("""^\d{1,3}(\.\d{1,3}){3}$""")
        private val IPV6 = Regex("""^\[[0-9a-f:.%]+]$""")
        private const val INDEX_HTML =
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<title>CamTel</title>" +
                "<style>html,body{margin:0;background:#000;height:100%}" +
                "img{width:100%;height:100%;object-fit:contain}</style>" +
                "</head><body><img src=\"/video\" alt=\"CamTel\"></body></html>"
    }
}
