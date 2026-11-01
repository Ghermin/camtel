package io.github.ghermin.camtel

import java.net.Inet4Address
import java.net.NetworkInterface

/** The PC finds the phone by probing its LAN for port 4747; this only reports our own addresses. */
object NetInfo {
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
