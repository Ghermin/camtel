package io.github.ghermin.camtel

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.NetworkInterface

/** Broadcasts "CAMTEL1|<name>|<port>" on UDP so the PC app can find the phone automatically. */
class Discovery(private val name: String, private val httpPort: Int) {
    @Volatile private var running = false
    private var thread: Thread? = null

    fun start() {
        running = true
        thread = Thread({
            val payload = "CAMTEL1|$name|$httpPort".toByteArray(Charsets.UTF_8)
            try {
                DatagramSocket().use { socket ->
                    socket.broadcast = true
                    while (running) {
                        for (addr in broadcastAddresses()) {
                            try {
                                socket.send(DatagramPacket(payload, payload.size, addr, DISCOVERY_PORT))
                            } catch (_: Exception) {
                            }
                        }
                        Thread.sleep(1000)
                    }
                }
            } catch (_: InterruptedException) {
            } catch (_: Exception) {
            }
        }, "camtel-discovery").apply {
            isDaemon = true
            start()
        }
    }

    fun stop() {
        running = false
        thread?.interrupt()
    }

    private fun broadcastAddresses(): Set<InetAddress> {
        val result = mutableSetOf<InetAddress>(InetAddress.getByName("255.255.255.255"))
        try {
            for (nif in NetworkInterface.getNetworkInterfaces()) {
                if (!nif.isUp || nif.isLoopback) continue
                for (ia in nif.interfaceAddresses) {
                    if (ia.address is Inet4Address) ia.broadcast?.let { result.add(it) }
                }
            }
        } catch (_: Exception) {
        }
        return result
    }

    companion object {
        const val DISCOVERY_PORT = 4748

        fun localIpv4Addresses(): List<String> = try {
            NetworkInterface.getNetworkInterfaces().toList()
                .filter { it.isUp && !it.isLoopback }
                .flatMap { it.inetAddresses.toList() }
                .filterIsInstance<Inet4Address>()
                .mapNotNull { it.hostAddress }
        } catch (_: Exception) {
            emptyList()
        }
    }
}
