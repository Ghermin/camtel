package io.github.ghermin.camtel

import android.content.Context
import java.net.Inet6Address
import java.net.NetworkInterface
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.TimeUnit
import java.util.concurrent.locks.ReentrantLock
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/**
 * Pairing secret shared with CamTel.exe.
 *
 * The app creates a random 256-bit token on first use. The PC gets it once, either over USB (adb
 * reads it through [PairingProvider], which only answers the adb shell) or over Wi-Fi by sending
 * the 6-digit code shown on the phone screen to `/pair`.
 *
 * The token itself is never sent on each request: the PC sends `Authorization: CamTel <hex>` with
 * HMAC-SHA256(token, "camtel-auth:" + the address it connects to), and the phone only accepts its
 * own addresses. A device that pretends to be the phone on the LAN therefore only ever receives a
 * value that is useless against the real phone.
 */
object Pairing {
    private const val PREFS = "camtel"
    private const val KEY = "pairingToken"
    private const val MAX_FAILURES = 5

    private val random = SecureRandom()
    private val lock = Object()
    @Volatile private var token: ByteArray? = null

    /** Bumped when the token changes: open streams of the old token are closed. */
    @Volatile var generation = 0L
        private set

    /** The code to type on the PC, or null while the app is not on screen (pairing closed). */
    @Volatile var code: String? = null
        private set
    private var failures = 0
    private val attempts = ReentrantLock()

    fun token(context: Context): ByteArray {
        token?.let { return it }
        synchronized(lock) {
            token?.let { return it }
            val prefs = context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            val stored = prefs.getString(KEY, null)?.let(::fromHex)
            val t = stored?.takeIf { it.size == 32 } ?: ByteArray(32).also {
                random.nextBytes(it)
                prefs.edit().putString(KEY, toHex(it)).commit()
            }
            token = t
            return t
        }
    }

    fun tokenHex(context: Context): String = toHex(token(context))

    /** Public, stable name of this pairing (changes with the token), so the PC finds its secret. */
    fun id(context: Context): String = id(token(context))

    private fun id(t: ByteArray): String =
        toHex(MessageDigest.getInstance("SHA-256").digest("camtel-id:${toHex(t)}".toByteArray())).take(16)

    /** Forgets every paired PC: new token, open connections are closed. */
    fun reset(context: Context) {
        synchronized(lock) {
            val t = ByteArray(32).also { random.nextBytes(it) }
            context.applicationContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                .edit().putString(KEY, toHex(t)).commit()
            token = t
            generation++
        }
        CamState.changed()
    }

    /** True when [authorization] (the Authorization header) proves the token for [host]. */
    fun authorized(context: Context, host: String?, authorization: String?): Boolean {
        if (host == null || authorization == null) return false
        val parts = authorization.trim().split(' ', limit = 2)
        if (parts.size != 2 || !parts[0].equals("CamTel", ignoreCase = true)) return false
        val given = fromHex(parts[1].trim()) ?: return false
        val name = hostName(host)
        if (!isOwnAddress(name)) return false
        return MessageDigest.isEqual(credential(token(context), name), given)
    }

    private fun credential(t: ByteArray, host: String): ByteArray =
        Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(t, "HmacSHA256"))
            doFinal("camtel-auth:$host".toByteArray())
        }

    /** "192.168.1.12:4747" -> "192.168.1.12", "[fe80::1]:4747" -> "fe80::1". */
    private fun hostName(host: String): String {
        val h = host.trim().lowercase()
        return if (h.startsWith("[")) h.substring(1).substringBefore(']') else h.substringBefore(':')
    }

    @Volatile private var ownAddresses: Pair<Long, Set<String>> = 0L to emptySet()

    /** Loopback (adb forward over USB) or one of this phone's addresses, refreshed every 3 s. */
    private fun isOwnAddress(name: String): Boolean {
        if (name == "127.0.0.1" || name == "localhost" || name == "::1") return true
        val now = System.currentTimeMillis()
        var cached = ownAddresses
        if (now - cached.first > 3000) {
            val set = try {
                NetworkInterface.getNetworkInterfaces().toList().flatMap { it.inetAddresses.toList() }
                    .mapNotNull { a ->
                        a.hostAddress?.lowercase()?.let { if (a is Inet6Address) it.substringBefore('%') else it }
                    }.toSet()
            } catch (_: Exception) {
                emptySet()
            }
            cached = now to set
            ownAddresses = cached
        }
        return name in cached.second
    }

    // ---- 6-digit code (Wi-Fi pairing) -------------------------------------

    /** The app is on screen: show a fresh code. */
    fun open() {
        synchronized(lock) {
            if (code == null) newCode()
        }
        CamState.changed()
    }

    /** The app left the screen: nobody can pair until it comes back. */
    fun close() {
        synchronized(lock) { code = null }
        CamState.changed()
    }

    sealed class Result {
        class Paired(val id: String, val token: String) : Result()
        object WrongCode : Result()
        object Closed : Result()
    }

    /**
     * Checks a code sent by a PC. Attempts are serialized and a wrong one costs one second; after
     * [MAX_FAILURES] wrong codes, a new one is shown. A right code is used up.
     */
    fun pair(context: Context, attempt: String): Result {
        val t = token(context)
        // One attempt at a time; a flood of requests waits a little, then gets "wrong code".
        val locked = try {
            attempts.tryLock(3, TimeUnit.SECONDS)
        } catch (_: InterruptedException) {
            false
        }
        if (!locked) return Result.WrongCode
        try {
            val result = synchronized(lock) {
                val expected = code ?: return Result.Closed
                if (MessageDigest.isEqual(expected.toByteArray(), attempt.trim().take(16).toByteArray())) {
                    newCode()
                    Result.Paired(id(t), toHex(t))
                } else {
                    if (++failures >= MAX_FAILURES) newCode()
                    Result.WrongCode
                }
            }
            CamState.changed()
            if (result is Result.WrongCode) Thread.sleep(1000)
            return result
        } catch (_: InterruptedException) {
            return Result.WrongCode
        } finally {
            attempts.unlock()
        }
    }

    private fun newCode() {
        code = "%06d".format(random.nextInt(1_000_000))
        failures = 0
    }

    private fun toHex(b: ByteArray): String = b.joinToString("") { "%02x".format(it) }

    private fun fromHex(s: String): ByteArray? {
        if (s.length % 2 != 0 || s.length > 128) return null
        return try {
            ByteArray(s.length / 2) { s.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
        } catch (_: NumberFormatException) {
            null
        }
    }
}
