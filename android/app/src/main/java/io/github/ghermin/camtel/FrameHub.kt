package io.github.ghermin.camtel

/** Holds the latest encoded JPEG frame and lets streaming threads wait for the next one. */
class FrameHub {
    private val lock = Object()
    private var frame: ByteArray? = null
    private var seq = 0L

    fun publish(jpeg: ByteArray) {
        synchronized(lock) {
            frame = jpeg
            seq++
            lock.notifyAll()
        }
    }

    /** Waits for a frame newer than [lastSeq]. Returns null on timeout. */
    fun await(lastSeq: Long, timeoutMs: Long): Pair<Long, ByteArray>? {
        synchronized(lock) {
            val deadline = System.currentTimeMillis() + timeoutMs
            while (seq == lastSeq || frame == null) {
                val remaining = deadline - System.currentTimeMillis()
                if (remaining <= 0) return null
                lock.wait(remaining)
            }
            return seq to frame!!
        }
    }
}
