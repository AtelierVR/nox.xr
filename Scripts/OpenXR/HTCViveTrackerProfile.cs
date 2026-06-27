using System.Collections.Generic;
using UnityEngine.Scripting;
using UnityEngine.XR.OpenXR.Input;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.InputSystem;
using System.Runtime.InteropServices;
using System;

#if UNITY_EDITOR
using UnityEditor;
#endif

using PoseControl = UnityEngine.InputSystem.XR.PoseControl;

namespace UnityEngine.XR.OpenXR.Features.Interactions
{
	/// <summary>
	/// Custom <see cref="InputDeviceCharacteristics"/> flags for Vive Tracker roles.
	/// Uses bits 16-29 to avoid conflicts with Unity's built-in flags (bits 0-10).
	/// </summary>
	[Flags]
	public enum InputDeviceTrackerCharacteristics : uint
	{
		TrackerLeftFoot      = 1 << 16,  // 0x00010000
		TrackerRightFoot     = 1 << 17,  // 0x00020000
		TrackerLeftShoulder  = 1 << 18,  // 0x00040000
		TrackerRightShoulder = 1 << 19,  // 0x00080000
		TrackerLeftElbow     = 1 << 20,  // 0x00100000
		TrackerRightElbow    = 1 << 21,  // 0x00200000
		TrackerLeftKnee      = 1 << 22,  // 0x00400000
		TrackerRightKnee     = 1 << 23,  // 0x00800000
		TrackerWaist         = 1 << 24,  // 0x01000000
		TrackerChest         = 1 << 25,  // 0x02000000
		TrackerCamera        = 1 << 26,  // 0x04000000
		TrackerKeyboard      = 1 << 27,  // 0x08000000
		TrackerLeftWrist     = 1 << 28,  // 0x10000000
		TrackerRightWrist    = 1 << 29,  // 0x20000000
	}

	/// <summary>
	/// This <see cref="OpenXRInteractionFeature"/> enables the use of HTC Vive Trackers
	/// via the XR_HTCX_vive_tracker_interaction OpenXR extension.
	/// Add it in Project Settings > XR Plug-in Management > OpenXR > Interaction Profiles.
	/// </summary>
#if UNITY_EDITOR
	[UnityEditor.XR.OpenXR.Features.OpenXRFeature(
		UiName = "HTC Vive Tracker Profile",
		BuildTargetGroups = new[] { BuildTargetGroup.Standalone },
		Company = "Nox",
		Desc = "Enables HTC Vive Tracker (2.0/3.0) detection via OpenXR for Full-Body Tracking.",
		OpenxrExtensionStrings = HTCViveTrackerProfile.extensionName,
		Version = "1.0.0",
		Category = UnityEditor.XR.OpenXR.Features.FeatureCategory.Interaction,
		FeatureId = featureId)]
#endif
	public class HTCViveTrackerProfile : OpenXRInteractionFeature
	{
		public const string featureId = "com.nox.openxr.feature.input.htcvivetracker";
		public const string profile = "/interaction_profiles/htc/vive_tracker_htcx";
		public const string extensionName = "XR_HTCX_vive_tracker_interaction";

		private const string kDeviceLocalizedName = "HTC Vive Tracker OpenXR";

		/// <summary>OpenXR user paths for each tracker role.</summary>
		public static class TrackerUserPaths
		{
			public const string leftFoot     = "/user/vive_tracker_htcx/role/left_foot";
			public const string rightFoot    = "/user/vive_tracker_htcx/role/right_foot";
			public const string leftShoulder = "/user/vive_tracker_htcx/role/left_shoulder";
			public const string rightShoulder= "/user/vive_tracker_htcx/role/right_shoulder";
			public const string leftElbow    = "/user/vive_tracker_htcx/role/left_elbow";
			public const string rightElbow   = "/user/vive_tracker_htcx/role/right_elbow";
			public const string leftKnee     = "/user/vive_tracker_htcx/role/left_knee";
			public const string rightKnee    = "/user/vive_tracker_htcx/role/right_knee";
			public const string waist        = "/user/vive_tracker_htcx/role/waist";
			public const string chest        = "/user/vive_tracker_htcx/role/chest";
			public const string camera       = "/user/vive_tracker_htcx/role/camera";
			public const string keyboard     = "/user/vive_tracker_htcx/role/keyboard";
			public const string leftWrist    = "/user/vive_tracker_htcx/role/left_wrist";
			public const string rightWrist   = "/user/vive_tracker_htcx/role/right_wrist";
		}

		public static class TrackerComponentPaths
		{
			public const string grip = "/input/grip/pose";
		}

		/// <summary>Base Input System device for XR Trackers.</summary>
		[InputControlLayout(isGenericTypeOfDevice = true, displayName = "XR Tracker")]
		public class XRTracker : TrackedDevice { }

		/// <summary>Input System device for HTC Vive Tracker via OpenXR.</summary>
		[Preserve]
		[InputControlLayout(
			displayName = "HTC Vive Tracker (OpenXR)",
			isGenericTypeOfDevice = true,
			commonUsages = new[] {
				"Left Foot", "Right Foot",
				"Left Shoulder", "Right Shoulder",
				"Left Elbow", "Right Elbow",
				"Left Knee", "Right Knee",
				"Waist", "Chest", "Camera", "Keyboard",
				"Left Wrist", "Right Wrist"
			})]
		public class XRViveTracker : XRTracker
		{
			protected override void FinishSetup()
			{
				base.FinishSetup();

				var capabilities = description.capabilities;
				var deviceDescriptor = XRDeviceDescriptor.FromJson(capabilities);

				var ch = (InputDeviceCharacteristics)deviceDescriptor.characteristics;
				if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftFoot) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Left Foot");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightFoot) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Right Foot");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftShoulder) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Left Shoulder");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightShoulder) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Right Shoulder");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftElbow) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Left Elbow");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightElbow) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Right Elbow");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftKnee) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Left Knee");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightKnee) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Right Knee");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerWaist) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Waist");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerChest) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Chest");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerCamera) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Camera");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerKeyboard) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Keyboard");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftWrist) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Left Wrist");
				else if ((ch & (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightWrist) != 0)
					InputSystem.InputSystem.SetDeviceUsage(this, "Right Wrist");
			}
		}

		protected override void RegisterDeviceLayout()
		{
			InputSystem.InputSystem.RegisterLayout<XRViveTracker>(
				"HTCViveTracker",
				matches: new InputDeviceMatcher()
					.WithInterface(XRUtilities.InterfaceMatchAnyVersion)
					.WithProduct(@"VIVE Tracker( Pro)?( MV)?$"));
		}

		protected override void UnregisterDeviceLayout()
		{
			InputSystem.InputSystem.RemoveLayout("HTCViveTracker");
		}

		protected override void RegisterActionMapsWithRuntime()
		{
			// Register all 14 tracker roles so SteamVR can route poses
			// for whichever roles the user assigned to their trackers.
			// Only actually-connected trackers will report valid pose data.
			var deviceConfigs = new List<DeviceConfig>();

			foreach (var (userPath, characteristics) in new (string, InputDeviceCharacteristics)[] {
				(TrackerUserPaths.leftFoot,      InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftFoot),
				(TrackerUserPaths.rightFoot,     InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightFoot),
				(TrackerUserPaths.leftShoulder,  InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftShoulder),
				(TrackerUserPaths.rightShoulder, InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightShoulder),
				(TrackerUserPaths.leftElbow,     InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftElbow),
				(TrackerUserPaths.rightElbow,    InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightElbow),
				(TrackerUserPaths.leftKnee,      InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftKnee),
				(TrackerUserPaths.rightKnee,     InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightKnee),
				(TrackerUserPaths.waist,         InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerWaist),
				(TrackerUserPaths.chest,         InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerChest),
				(TrackerUserPaths.camera,        InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerCamera),
				(TrackerUserPaths.keyboard,      InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerKeyboard),
				(TrackerUserPaths.leftWrist,     InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerLeftWrist),
				(TrackerUserPaths.rightWrist,    InputDeviceCharacteristics.TrackedDevice | (InputDeviceCharacteristics)InputDeviceTrackerCharacteristics.TrackerRightWrist),
			})
			{
				deviceConfigs.Add(new DeviceConfig
				{
					characteristics = characteristics,
					userPath = userPath
				});
			}

			var actionMap = new ActionMapConfig
			{
				name = "vive_tracker",
				localizedName = kDeviceLocalizedName,
				desiredInteractionProfile = profile,
				manufacturer = "HTC",
				serialNumber = "",
				deviceInfos = deviceConfigs,
				actions = new List<ActionConfig>
				{
					new ActionConfig
					{
						name = "devicePose",
						localizedName = "Device Pose",
						type = ActionType.Pose,
						usages = new List<string> { "Device" },
						bindings = new List<ActionBinding>
						{
							new ActionBinding
							{
								interactionPath = TrackerComponentPaths.grip,
								interactionProfileName = profile,
							}
						}
					}
				}
			};

			AddActionMap(actionMap);
		}
	}
}
