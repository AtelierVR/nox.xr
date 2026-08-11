using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

using ISDevice = UnityEngine.InputSystem.InputDevice;

namespace Nox.XR.Editor
{
	/// <summary>
	/// Editor-only debug tools for OpenXR device diagnostics.
	/// Access via Nox > XR menu.
	/// </summary>
	public static class XROpenXRDebugTools
	{
		private const string MenuRoot = "Nox/XR/";
		private const string SubDevices = MenuRoot + "Devices/";

		// ── OpenXR System Info ──────────────────────────────────────────────

		[MenuItem(MenuRoot + "OpenXR Runtime Info")]
		private static void DumpOpenXRInfo()
		{
			var sb = new StringBuilder();
			sb.AppendLine("=== OpenXR Runtime Info ===");

			// Check if any XR loader is active
			var isXRActive = XRGeneralSettings.Instance?.Manager?.activeLoader != null;
			sb.AppendLine($"XR Loader Active: {isXRActive}");

			if (isXRActive)
			{
				var activeLoader = XRGeneralSettings.Instance.Manager.activeLoader;
				sb.AppendLine($"  Loader: {activeLoader.GetType().Name}");

				var isOpenXR = activeLoader is OpenXRLoaderBase;
				sb.AppendLine($"  Is OpenXR: {isOpenXR}");
			}

			var runtimeName = OpenXRRuntime.name;
			if (!string.IsNullOrEmpty(runtimeName))
			{
				sb.AppendLine("OpenXR Runtime: ACTIVE");
				sb.AppendLine($"  Runtime Name: {runtimeName}");
				sb.AppendLine($"  Runtime Version: {OpenXRRuntime.version}");
				sb.AppendLine($"  Runtime API Version: {OpenXRRuntime.apiVersion}");
				sb.AppendLine($"  Plugin Version: {OpenXRRuntime.pluginVersion}");
			}
			else
			{
				sb.AppendLine("OpenXR Runtime: NOT ACTIVE");
			}

			// Check if tracker extension is enabled
			sb.AppendLine();
			sb.AppendLine("--- Extension Check ---");
			var extName = HTCViveTrackerProfile.extensionName;
			var extEnabled = !string.IsNullOrEmpty(runtimeName) && OpenXRRuntime.IsExtensionEnabled(extName);
			sb.AppendLine($"  {extName}: {(extEnabled ? "ENABLED" : "not enabled")}");

			Debug.Log(sb.ToString());
		}

		// ── Enabled OpenXR Features ─────────────────────────────────────────

		[MenuItem(MenuRoot + "List Enabled OpenXR Features")]
		private static void ListEnabledFeatures()
		{
			var sb = new StringBuilder();
			sb.AppendLine("=== OpenXR Interaction Features ===");

			var settings = OpenXRSettings.ActiveBuildTargetInstance;
			if (settings == null)
			{
				Debug.LogWarning("No OpenXRSettings for active build target.");
				return;
			}

			try
			{
				var features = settings.GetFeatures<OpenXRInteractionFeature>();
				var allFeatures = settings.GetFeatures<OpenXRFeature>();

				sb.AppendLine($"Total OpenXR Features: {allFeatures.Length}");
				sb.AppendLine($"Interaction Features: {features.Length}");
				sb.AppendLine();

				foreach (var feature in allFeatures)
				{
					var isInteraction = feature is OpenXRInteractionFeature;
					var marker = feature.enabled ? "✓" : "✗";
					var type = isInteraction ? "[Interaction]" : "[Feature]";

					// Use reflection to get internal fields
					var nameUi = GetInternalField<string>(feature, "nameUi");
					var featVersion = GetInternalField<string>(feature, "version");
					var company = GetInternalField<string>(feature, "company");

					sb.AppendLine($"  {marker} {type} {nameUi ?? feature.GetType().Name}");
					sb.AppendLine($"       Type: {feature.GetType().FullName}");
					if (!string.IsNullOrEmpty(featVersion))
						sb.AppendLine($"       Version: {featVersion}");
					if (!string.IsNullOrEmpty(company))
						sb.AppendLine($"       Company: {company}");
					sb.AppendLine();
				}

				// Highlight tracker profile
				sb.AppendLine("--- Tracker Profile Check ---");
				var hasTracker = false;
				foreach (var f in allFeatures)
				{
					if (f.GetType() == typeof(HTCViveTrackerProfile))
					{
						hasTracker = true;
						sb.AppendLine($"HTCViveTrackerProfile found: enabled={f.enabled}");
					}
				}
				if (!hasTracker)
					sb.AppendLine("HTCViveTrackerProfile NOT registered in OpenXR features!");
			}
			catch (System.Exception e)
			{
				sb.AppendLine($"Error reading features: {e.Message}");
			}

			Debug.Log(sb.ToString());
		}

		// ── All InputSystem Devices ─────────────────────────────────────────

		[MenuItem(SubDevices + "Dump InputSystem Devices")]
		private static void DumpInputSystemDevices()
		{
			var sb = new StringBuilder();
			sb.AppendLine("=== InputSystem Devices ===");

			var devices = UnityEngine.InputSystem.InputSystem.devices;
			sb.AppendLine($"Count: {devices.Count}");

			foreach (var device in devices)
			{
				sb.AppendLine();
				sb.AppendLine($"  Device: {device.displayName}");
				sb.AppendLine($"    Layout: {device.layout}");
				sb.AppendLine($"    Path: {device.path}");
				sb.AppendLine($"    DeviceId: {device.deviceId}");
				sb.AppendLine($"    Usages: [{string.Join(", ", device.usages)}]");
				sb.AppendLine($"    Enabled: {device.enabled}");
				sb.AppendLine($"    Native: {device.native}");
				sb.AppendLine($"    Added: {device.added}");

				try
				{
					sb.AppendLine($"    Description: {device.description}");
				}
				catch { /* ignore */ }
			}

			Debug.Log(sb.ToString());
		}

		// ── All XR Devices (UnityEngine.XR) ─────────────────────────────────

		[MenuItem(SubDevices + "Dump XR Devices (Detailed)")]
		private static void DumpXRDevicesDetailed()
		{
			var sb = new StringBuilder();
			sb.AppendLine("=== XR Devices (UnityEngine.XR) ===");

			var devices = new List<UnityEngine.XR.InputDevice>();
			InputDevices.GetDevices(devices);

			sb.AppendLine($"Count: {devices.Count}");

			foreach (var device in devices)
			{
				sb.AppendLine();
				sb.AppendLine($"  Device: {device.name}");
				sb.AppendLine($"    Manufacturer: {device.manufacturer}");
				sb.AppendLine($"    Serial: {device.serialNumber}");
				sb.AppendLine($"    Role: {device.role}");
				sb.AppendLine($"    Characteristics: 0x{device.characteristics:X8} ({device.characteristics})");
				sb.AppendLine($"    IsValid: {device.isValid}");

				// Pose
				if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out var pos) &&
					device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out var rot))
				{
					var euler = rot.eulerAngles;
					sb.AppendLine($"    Position: ({pos.x:F3}, {pos.y:F3}, {pos.z:F3})");
					sb.AppendLine($"    Rotation Euler: ({euler.x:F1}°, {euler.y:F1}°, {euler.z:F1}°)");
				}

				// Tracking state
				if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trackingState, out InputTrackingState trackingState))
					sb.AppendLine($"    TrackingState: {trackingState}");

				// Battery
				if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.batteryLevel, out float battery))
					sb.AppendLine($"    Battery: {battery:P0}");

				// All feature usages
				var featureUsages = new List<InputFeatureUsage>();
				if (device.TryGetFeatureUsages(featureUsages))
				{
					sb.AppendLine($"    Features ({featureUsages.Count}):");
					foreach (var f in featureUsages)
						sb.AppendLine($"      {f.name} ({f.type})");
				}
			}

			Debug.Log(sb.ToString());
		}

		// ── Trackers Only ───────────────────────────────────────────────────

		[MenuItem(SubDevices + "Dump Trackers Only")]
		private static void DumpTrackersOnly()
		{
			var sb = new StringBuilder();
			sb.AppendLine("=== Trackers (XR) ===");

			var devices = new List<UnityEngine.XR.InputDevice>();
			InputDevices.GetDevicesWithCharacteristics(
				InputDeviceCharacteristics.TrackedDevice,
				devices);

			// Filter to only those that are NOT HMD and NOT controllers
			var trackers = new List<UnityEngine.XR.InputDevice>();
			foreach (var d in devices)
			{
				var ch = (InputDeviceCharacteristics)d.characteristics;
				if ((ch & InputDeviceCharacteristics.HeadMounted) != 0) continue;
				if ((ch & InputDeviceCharacteristics.Controller) != 0) continue;
				trackers.Add(d);
			}

			sb.AppendLine($"Trackers detected: {trackers.Count}");

			foreach (var t in trackers)
			{
				sb.AppendLine();
				sb.AppendLine($"  Tracker: {t.name}");
				sb.AppendLine($"    Serial: {t.serialNumber}");
				sb.AppendLine($"    Manufacturer: {t.manufacturer}");
				sb.AppendLine($"    Characteristics: 0x{t.characteristics:X8} ({t.characteristics})");

				// Check for our custom tracker characteristics (bits 16-29)
				var ch = (uint)t.characteristics;
				var customBits = ch >> 16;
				if (customBits != 0)
				{
					sb.AppendLine($"    Custom bits (16-29): 0x{customBits:X4}");
					if ((ch & 0x00010000) != 0) sb.AppendLine($"    > TrackerLeftFoot");
					if ((ch & 0x00020000) != 0) sb.AppendLine($"    > TrackerRightFoot");
					if ((ch & 0x00040000) != 0) sb.AppendLine($"    > TrackerLeftShoulder");
					if ((ch & 0x00080000) != 0) sb.AppendLine($"    > TrackerRightShoulder");
					if ((ch & 0x00100000) != 0) sb.AppendLine($"    > TrackerLeftElbow");
					if ((ch & 0x00200000) != 0) sb.AppendLine($"    > TrackerRightElbow");
					if ((ch & 0x00400000) != 0) sb.AppendLine($"    > TrackerLeftKnee");
					if ((ch & 0x00800000) != 0) sb.AppendLine($"    > TrackerRightKnee");
					if ((ch & 0x01000000) != 0) sb.AppendLine($"    > TrackerWaist");
					if ((ch & 0x02000000) != 0) sb.AppendLine($"    > TrackerChest");
					if ((ch & 0x04000000) != 0) sb.AppendLine($"    > TrackerCamera");
					if ((ch & 0x08000000) != 0) sb.AppendLine($"    > TrackerKeyboard");
					if ((ch & 0x10000000) != 0) sb.AppendLine($"    > TrackerLeftWrist");
					if ((ch & 0x20000000) != 0) sb.AppendLine($"    > TrackerRightWrist");
				}
				else
				{
					sb.AppendLine($"    No custom tracker role bits set (SteamVR may not be routing roles)");
				}

				// Pose
				if (t.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out var pos) &&
					t.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out var rot))
				{
					var euler = rot.eulerAngles;
					sb.AppendLine($"    Position: ({pos.x:F3}, {pos.y:F3}, {pos.z:F3})");
					sb.AppendLine($"    Rotation: ({euler.x:F1}°, {euler.y:F1}°, {euler.z:F1}°)");
				}
			}

			if (trackers.Count == 0)
			{
				sb.AppendLine();
				sb.AppendLine("No trackers found!");
				sb.AppendLine("Possible causes:");
				sb.AppendLine("  1. SteamVR may not be forwarding tracker paths via OpenXR");
				sb.AppendLine("  2. HTCViveTrackerProfile may not be enabled in OpenXR settings");
				sb.AppendLine("  3. SteamVR version may not support XR_HTCX_vive_tracker_interaction");
				sb.AppendLine("  4. Trackers may not be assigned roles in SteamVR");
				sb.AppendLine();
				sb.AppendLine("Try: Window > Analysis > Input Debugger to see raw InputSystem devices");
			}

			Debug.Log(sb.ToString());
		}

		// ── All-in-One Dump ─────────────────────────────────────────────────

		[MenuItem(MenuRoot + "Dump ALL XR Info")]
		private static void DumpAllXRInfo()
		{
			DumpOpenXRInfo();
			ListEnabledFeatures();
			DumpInputSystemDevices();
			DumpXRDevicesDetailed();
			DumpTrackersOnly();
			Debug.Log("=== ALL XR INFO DUMPED ===");
		}

		// ── Check HTCViveTrackerProfile Registration ────────────────────────

		[MenuItem(MenuRoot + "Check Tracker Profile Registration")]
		private static void CheckTrackerRegistration()
		{
			var sb = new StringBuilder();
			sb.AppendLine("=== HTCViveTrackerProfile Registration ===");

			var settings = OpenXRSettings.ActiveBuildTargetInstance;
			if (settings == null)
			{
				Debug.LogWarning("No OpenXRSettings for active build target.");
				return;
			}

			var features = settings.GetFeatures<OpenXRFeature>();

			sb.AppendLine($"Extension: {HTCViveTrackerProfile.extensionName}");
			sb.AppendLine($"Profile: {HTCViveTrackerProfile.profile}");
			sb.AppendLine($"FeatureId: {HTCViveTrackerProfile.featureId}");

			var found = false;
			foreach (var f in features)
			{
				if (f.GetType() == typeof(HTCViveTrackerProfile))
				{
					found = true;
					var nameUi = GetInternalField<string>(f, "nameUi");
					var priority = GetInternalField<int>(f, "priority");
					sb.AppendLine($"Profile registered: YES — {nameUi}");
					sb.AppendLine($"Enabled: {f.enabled}");
					sb.AppendLine($"Priority: {priority}");
					break;
				}
			}

			if (!found)
				sb.AppendLine("Profile registered: NO — add it in Project Settings > XR Plug-in Management > OpenXR > Interaction Profiles");

			// Check if any feature declares the same extension
			sb.AppendLine();
			sb.AppendLine("Features declaring XR_HTCX_vive_tracker_interaction:");
			foreach (var f in features)
			{
				try
				{
					var extAttr = f.GetType().GetCustomAttributes(
						typeof(UnityEditor.XR.OpenXR.Features.OpenXRFeatureAttribute), false);
					foreach (UnityEditor.XR.OpenXR.Features.OpenXRFeatureAttribute attr in extAttr)
					{
						if (attr.OpenxrExtensionStrings?.Contains(HTCViveTrackerProfile.extensionName) == true)
							sb.AppendLine($"  {f.GetType().Name}: enabled={f.enabled}, extensions=\"{attr.OpenxrExtensionStrings}\"");
					}
				}
				catch { /* skip */ }
			}

			Debug.Log(sb.ToString());
		}

		// ── Reflection helper for internal fields ───────────────────────────

		private static T GetInternalField<T>(object obj, string fieldName)
		{
			var field = obj.GetType().GetField(fieldName,
				BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
			if (field != null && field.GetValue(obj) is T value)
				return value;
			return default;
		}
	}
}
