package com.example.quacktion_frontend

import android.Manifest
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.le.ScanCallback
import android.bluetooth.le.ScanFilter
import android.bluetooth.le.ScanResult
import android.bluetooth.le.ScanSettings
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.net.wifi.WifiManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import androidx.core.content.ContextCompat
import com.espressif.provisioning.DeviceConnectionEvent
import com.espressif.provisioning.ESPConstants
import com.espressif.provisioning.ESPDevice
import com.espressif.provisioning.ESPProvisionManager
import com.espressif.provisioning.listeners.ResponseListener
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import org.greenrobot.eventbus.EventBus
import org.greenrobot.eventbus.Subscribe
import org.greenrobot.eventbus.ThreadMode
import org.json.JSONObject
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean

class MainActivity : FlutterActivity() {
    private val serviceUuid = UUID.fromString("c5649ba0-9be0-43aa-bfb6-8902ab366fad")
    private val mainHandler = Handler(Looper.getMainLooper())
    private val discovered = mutableMapOf<String, BluetoothDevice>()
    private var bleScannerCallback: ScanCallback? = null
    private var scanResult: MethodChannel.Result? = null
    private var pendingPermissionResult: MethodChannel.Result? = null
    private var pendingPermissionAction: (() -> Unit)? = null
    private var connectionResult: MethodChannel.Result? = null
    private var espDevice: ESPDevice? = null

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, "quacktion/speaker_provisioning")
            .setMethodCallHandler { call, result ->
                when (call.method) {
                    "scan" -> withBluetoothPermissions(result) { startScan(result) }
                    "connect" -> withBluetoothPermissions(result) {
                        connect(call.argument<String>("address"), result)
                    }
                    "exchange" -> exchange(
                        call.argument<String>("endpoint"),
                        call.argument<String>("payload"),
                        result,
                    )
                    "disconnect" -> {
                        disconnect()
                        result.success(null)
                    }
                    "isDppSupported" -> result.success(isDppSupported())
                    "openDpp" -> openDpp(call.argument<String>("uri"), result)
                    else -> result.notImplemented()
                }
            }
        EventBus.getDefault().register(this)
    }

    override fun onDestroy() {
        stopScan()
        disconnect()
        if (EventBus.getDefault().isRegistered(this)) EventBus.getDefault().unregister(this)
        super.onDestroy()
    }

    private fun withBluetoothPermissions(result: MethodChannel.Result, action: () -> Unit) {
        val permissions = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            arrayOf(Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT)
        } else {
            arrayOf(Manifest.permission.ACCESS_FINE_LOCATION)
        }
        val missing = permissions.filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (missing.isEmpty()) {
            action()
            return
        }
        if (pendingPermissionResult != null) {
            result.error("permission_busy", "A Bluetooth permission request is already active.", null)
            return
        }
        pendingPermissionResult = result
        pendingPermissionAction = action
        requestPermissions(missing.toTypedArray(), REQUEST_BLUETOOTH_PERMISSIONS)
    }

    @Deprecated("Required for runtime permission callback")
    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray,
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQUEST_BLUETOOTH_PERMISSIONS) return
        val result = pendingPermissionResult
        val action = pendingPermissionAction
        pendingPermissionResult = null
        pendingPermissionAction = null
        if (result == null || action == null) return
        if (grantResults.isNotEmpty() && grantResults.all { it == PackageManager.PERMISSION_GRANTED }) {
            action()
        } else {
            result.error("bluetooth_permission_denied", "Allow Nearby devices access to find your speaker.", null)
        }
    }

    @Suppress("MissingPermission")
    private fun startScan(result: MethodChannel.Result) {
        stopScan()
        val adapter = (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager).adapter
        if (adapter == null || !adapter.isEnabled) {
            result.error("bluetooth_off", "Turn on Bluetooth to find your speaker.", null)
            return
        }
        val scanner = adapter.bluetoothLeScanner
        if (scanner == null) {
            result.error("bluetooth_unavailable", "Bluetooth scanning is unavailable on this phone.", null)
            return
        }
        discovered.clear()
        scanResult = result
        val rows = mutableMapOf<String, Map<String, Any>>()
        val callback = object : ScanCallback() {
            override fun onScanResult(callbackType: Int, scan: ScanResult) {
                val record = scan.scanRecord ?: return
                if (record.serviceUuids?.any { it.uuid == serviceUuid } != true) return
                val name = record.deviceName ?: scan.device.name ?: return
                if (!name.startsWith("Quacktion-")) return
                val address = scan.device.address
                discovered[address] = scan.device
                rows[address] = mapOf("address" to address, "name" to name, "rssi" to scan.rssi)
            }

            override fun onScanFailed(errorCode: Int) {
                stopScan()
                scanResult?.error("scan_failed", "Bluetooth scan failed ($errorCode).", null)
                scanResult = null
            }
        }
        bleScannerCallback = callback
        try {
            scanner.startScan(
                listOf(ScanFilter.Builder().setServiceUuid(android.os.ParcelUuid(serviceUuid)).build()),
                ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build(),
                callback,
            )
        } catch (error: Exception) {
            stopScan()
            result.error("scan_failed", error.message ?: "Could not start Bluetooth scan.", null)
            return
        }
        mainHandler.postDelayed({
            stopScan()
            val pending = scanResult ?: return@postDelayed
            scanResult = null
            pending.success(rows.values.sortedByDescending { it["rssi"] as Int })
        }, SCAN_WINDOW_MS)
    }

    @Suppress("MissingPermission")
    private fun stopScan() {
        val callback = bleScannerCallback ?: return
        try {
            (getSystemService(BLUETOOTH_SERVICE) as BluetoothManager).adapter?.bluetoothLeScanner?.stopScan(callback)
        } catch (_: Exception) {
            // Scanner may already be stopped or permissions revoked.
        }
        bleScannerCallback = null
    }

    @Suppress("MissingPermission")
    private fun connect(address: String?, result: MethodChannel.Result) {
        if (address.isNullOrBlank()) {
            result.error("invalid_device", "Select a speaker from the scan results.", null)
            return
        }
        val device = discovered[address]
        if (device == null) {
            result.error("device_not_found", "That speaker is no longer in the scan results. Scan again.", null)
            return
        }
        disconnect()
        val esp = ESPProvisionManager.getInstance(applicationContext).createESPDevice(
            ESPConstants.TransportType.TRANSPORT_BLE,
            ESPConstants.SecurityType.SECURITY_2,
        )
        esp.setUserName(SECURITY_USERNAME)
        esp.setProofOfPossession(SECURITY_PASSWORD)
        esp.setPrimaryServiceUuid(serviceUuid.toString())
        espDevice = esp
        connectionResult = result
        try {
            esp.connectBLEDevice(device, serviceUuid.toString())
        } catch (error: Exception) {
            connectionResult = null
            disconnect()
            result.error("connect_failed", error.message ?: "Could not connect to the speaker.", null)
            return
        }
        mainHandler.postDelayed({
            val pending = connectionResult ?: return@postDelayed
            connectionResult = null
            disconnect()
            pending.error("connect_timeout", "The speaker did not connect. Make sure it is in setup mode and try again.", null)
        }, CONNECT_TIMEOUT_MS)
    }

    @Subscribe(threadMode = ThreadMode.MAIN)
    fun onDeviceConnection(event: DeviceConnectionEvent) {
        val pending = connectionResult
        if (pending == null) {
            if (event.eventType == ESPConstants.EVENT_DEVICE_DISCONNECTED) {
                espDevice = null
            }
            return
        }
        when (event.eventType) {
            ESPConstants.EVENT_DEVICE_CONNECTED -> {
                connectionResult = null
                val version = try { JSONObject(espDevice?.versionInfo ?: "{}") } catch (_: Exception) { JSONObject() }
                val provision = version.optJSONObject("prov")
                val appProtocol = version.optJSONObject("quacktion")
                if (provision == null || provision.optInt("sec_ver") != 2 ||
                    provision.optInt("sec_patch_ver") != 1 ||
                    appProtocol?.optInt("protocolVersion") != 1
                ) {
                    disconnect()
                    pending.error("unsupported_firmware", "This speaker is using an unsupported setup protocol. Update its firmware and try again.", null)
                } else {
                    pending.success(version.toString())
                }
            }
            ESPConstants.EVENT_DEVICE_CONNECTION_FAILED -> {
                connectionResult = null
                disconnect()
                pending.error("connect_failed", "Could not connect to the speaker. Make sure it is in setup mode and try again.", null)
            }
            ESPConstants.EVENT_DEVICE_DISCONNECTED -> {
                connectionResult = null
                pending.error("disconnected", "The speaker disconnected. Reconnect and press its setup button again.", null)
                espDevice = null
            }
        }
    }

    private fun exchange(endpoint: String?, payload: String?, result: MethodChannel.Result) {
        val esp = espDevice
        if (esp == null || endpoint.isNullOrBlank() || payload == null) {
            result.error("not_connected", "Connect to the speaker over Bluetooth first.", null)
            return
        }
        val completed = AtomicBoolean(false)
        val timeout = Runnable {
            if (completed.compareAndSet(false, true)) {
                disconnect()
                result.error("endpoint_timeout", "The speaker did not respond. Reconnect and try again.", null)
            }
        }
        mainHandler.postDelayed(timeout, ENDPOINT_TIMEOUT_MS)
        try {
            esp.sendDataToCustomEndPoint(endpoint, payload.toByteArray(Charsets.UTF_8), object : ResponseListener {
                override fun onSuccess(returnData: ByteArray?) {
                    runOnUiThread {
                        if (completed.compareAndSet(false, true)) {
                            mainHandler.removeCallbacks(timeout)
                            result.success(returnData?.toString(Charsets.UTF_8) ?: "{}")
                        }
                    }
                }

                override fun onFailure(error: Exception) {
                    runOnUiThread {
                        if (completed.compareAndSet(false, true)) {
                            mainHandler.removeCallbacks(timeout)
                            disconnect()
                            result.error("endpoint_failed", error.message ?: "Speaker request failed.", null)
                        }
                    }
                }
            })
        } catch (error: Exception) {
            mainHandler.removeCallbacks(timeout)
            if (completed.compareAndSet(false, true)) {
                disconnect()
                result.error("endpoint_failed", error.message ?: "Speaker request failed.", null)
            }
        }
    }

    private fun disconnect() {
        connectionResult = null
        val current = espDevice
        espDevice = null
        try { current?.disconnectDevice() } catch (_: Exception) { }
    }

    private fun isDppSupported(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) return false
        val wifi = applicationContext.getSystemService(WIFI_SERVICE) as? WifiManager ?: return false
        return try { wifi.isEasyConnectSupported } catch (_: Exception) { false }
    }

    private fun openDpp(uri: String?, result: MethodChannel.Result) {
        if (uri.isNullOrBlank() || Build.VERSION.SDK_INT < Build.VERSION_CODES.Q || !isDppSupported()) {
            result.error("dpp_unavailable", "Wi-Fi Easy Connect is not available on this phone.", null)
            return
        }
        try {
            val intent = Intent(Settings.ACTION_PROCESS_WIFI_EASY_CONNECT_URI).apply {
                data = Uri.parse(uri)
            }
            startActivity(intent)
            result.success(null)
        } catch (error: Exception) {
            result.error("dpp_unavailable", error.message ?: "Could not start Wi-Fi Easy Connect.", null)
        }
    }

    companion object {
        private const val REQUEST_BLUETOOTH_PERMISSIONS = 901
        private const val SCAN_WINDOW_MS = 12_000L
        private const val CONNECT_TIMEOUT_MS = 25_000L
        private const val ENDPOINT_TIMEOUT_MS = 20_000L
        private const val SECURITY_USERNAME = "quacktion"
        private const val SECURITY_PASSWORD = "quacktion-setup"
    }
}
