using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR;

namespace Nox.XR.Panels {
	/// <summary>
	/// Type d'input XR
	/// </summary>
	public enum XRInputType {
		Trigger,
		Grip,
		Primary2DAxis,
		Secondary2DAxis,
		PrimaryButton,
		SecondaryButton,
		Primary2DAxisClick,
		Secondary2DAxisClick,
		PrimaryTouch,
		SecondaryTouch,
		MenuButton,
		TriggerButton
	}

	/// <summary>
	/// Main XR utilisée
	/// </summary>
	public enum XRHandType {
		Left,
		Right,
		Both
	}

	/// <summary>
	/// État d'un bouton XR
	/// </summary>
	public enum XRButtonState {
		None,
		Pressed,
		Released,
		Held
	}

	/// <summary>
	/// Événement d'input XR avec des données contextuelles
	/// </summary>
	[Serializable]
	public class XRInputEvent : UnityEvent<XRInputData> { }

	/// <summary>
	/// Données d'un événement d'input XR
	/// </summary>
	[Serializable]
	public struct XRInputData {
		public XRHandType handType;
		public XRInputType inputType;
		public XRButtonState buttonState;
		public float floatValue;
		public Vector2 vector2Value;
		public Vector3 position;
		public Quaternion rotation;
		public bool boolValue;
		public InputDevice device;

		public XRInputData(
			XRHandType hand,
			XRInputType input,
			XRButtonState state = XRButtonState.None,
			float value = 0f,
			Vector2 vec2 = default,
			bool boolean = false,
			InputDevice dev = default
		) {
			handType = hand;
			inputType = input;
			buttonState = state;
			floatValue = value;
			vector2Value = vec2;
			boolValue = boolean;
			device = dev;
			position = Vector3.zero;
			rotation = Quaternion.identity;
		}
	}

	/// <summary>
	/// Helper statique pour gérer les inputs XR et les convertir en UnityEvents
	/// </summary>
	public static class XRInputHelper {
		private static Dictionary<XRHandType, InputDevice> _devices = new();
		private static Dictionary<string, bool> _lastButtonStates = new();
		private static Dictionary<string, float> _lastFloatValues = new();
		private static Dictionary<string, Vector2> _lastVector2Values = new();

		// Events globaux pour chaque type d'input
		public static XRInputEvent OnTriggerPressed = new();
		public static XRInputEvent OnTriggerReleased = new();
		public static XRInputEvent OnTriggerValue = new();
		
		public static XRInputEvent OnGripPressed = new();
		public static XRInputEvent OnGripReleased = new();
		public static XRInputEvent OnGripValue = new();
		
		public static XRInputEvent OnPrimaryButtonPressed = new();
		public static XRInputEvent OnPrimaryButtonReleased = new();
		
		public static XRInputEvent OnSecondaryButtonPressed = new();
		public static XRInputEvent OnSecondaryButtonReleased = new();
		
		public static XRInputEvent OnPrimary2DAxisChanged = new();
		public static XRInputEvent OnSecondary2DAxisChanged = new();
		
		public static XRInputEvent OnPrimary2DAxisClickPressed = new();
		public static XRInputEvent OnPrimary2DAxisClickReleased = new();
		
		public static XRInputEvent OnMenuButtonPressed = new();
		public static XRInputEvent OnMenuButtonReleased = new();

		/// <summary>
		/// Initialise les devices XR
		/// </summary>
		public static void Initialize() {
			RefreshDevices();
		}

		/// <summary>
		/// Rafraîchit la liste des devices connectés
		/// </summary>
		public static void RefreshDevices() {
			_devices.Clear();
			
			var leftDevices = new List<InputDevice>();
			InputDevices.GetDevicesAtXRNode(XRNode.LeftHand, leftDevices);
			if (leftDevices.Count > 0)
				_devices[XRHandType.Left] = leftDevices[0];
			
			var rightDevices = new List<InputDevice>();
			InputDevices.GetDevicesAtXRNode(XRNode.RightHand, rightDevices);
			if (rightDevices.Count > 0)
				_devices[XRHandType.Right] = rightDevices[0];
		}

		/// <summary>
		/// Obtient un device par type de main
		/// </summary>
		public static bool TryGetDevice(XRHandType handType, out InputDevice device) {
			return _devices.TryGetValue(handType, out device);
		}

		/// <summary>
		/// Met à jour tous les inputs XR (à appeler dans Update)
		/// </summary>
		public static void UpdateInputs() {
			if (_devices.Count == 0)
				RefreshDevices();

			foreach (var kvp in _devices) {
				UpdateHandInputs(kvp.Key, kvp.Value);
			}
		}

		/// <summary>
		/// Met à jour les inputs pour une main spécifique
		/// </summary>
		private static void UpdateHandInputs(XRHandType handType, InputDevice device) {
			// Trigger
			UpdateFloatButton(
				device, handType, XRInputType.Trigger,
				CommonUsages.trigger,
				OnTriggerPressed, OnTriggerReleased, OnTriggerValue,
				0.9f // Seuil pour considérer comme "pressé"
			);

			// Grip
			UpdateFloatButton(
				device, handType, XRInputType.Grip,
				CommonUsages.grip,
				OnGripPressed, OnGripReleased, OnGripValue,
				0.9f
			);

			// Primary Button
			UpdateBoolButton(
				device, handType, XRInputType.PrimaryButton,
				CommonUsages.primaryButton,
				OnPrimaryButtonPressed, OnPrimaryButtonReleased
			);

			// Secondary Button
			UpdateBoolButton(
				device, handType, XRInputType.SecondaryButton,
				CommonUsages.secondaryButton,
				OnSecondaryButtonPressed, OnSecondaryButtonReleased
			);

			// Primary 2D Axis
			UpdateVector2Axis(
				device, handType, XRInputType.Primary2DAxis,
				CommonUsages.primary2DAxis,
				OnPrimary2DAxisChanged
			);

			// Primary 2D Axis Click
			UpdateBoolButton(
				device, handType, XRInputType.Primary2DAxisClick,
				CommonUsages.primary2DAxisClick,
				OnPrimary2DAxisClickPressed, OnPrimary2DAxisClickReleased
			);

			// Menu Button
			UpdateBoolButton(
				device, handType, XRInputType.MenuButton,
				CommonUsages.menuButton,
				OnMenuButtonPressed, OnMenuButtonReleased
			);
		}

		/// <summary>
		/// Met à jour un bouton float (trigger, grip)
		/// </summary>
		private static void UpdateFloatButton(
			InputDevice device,
			XRHandType handType,
			XRInputType inputType,
			InputFeatureUsage<float> usage,
			XRInputEvent pressedEvent,
			XRInputEvent releasedEvent,
			XRInputEvent valueEvent,
			float pressThreshold
		) {
			if (!device.TryGetFeatureValue(usage, out float value))
				return;

			var key = $"{handType}_{inputType}";
			var wasPressed = _lastFloatValues.ContainsKey(key) 
				&& _lastFloatValues[key] >= pressThreshold;
			var isPressed = value >= pressThreshold;

			var data = new XRInputData(
				handType, inputType,
				isPressed ? XRButtonState.Held : XRButtonState.None,
				value, Vector2.zero, isPressed, device
			);

			// Invoke value event chaque frame
			valueEvent?.Invoke(data);

			// Pressed
			if (isPressed && !wasPressed) {
				data.buttonState = XRButtonState.Pressed;
				pressedEvent?.Invoke(data);
			}
			// Released
			else if (!isPressed && wasPressed) {
				data.buttonState = XRButtonState.Released;
				releasedEvent?.Invoke(data);
			}

			_lastFloatValues[key] = value;
		}

		/// <summary>
		/// Met à jour un bouton bool
		/// </summary>
		private static void UpdateBoolButton(
			InputDevice device,
			XRHandType handType,
			XRInputType inputType,
			InputFeatureUsage<bool> usage,
			XRInputEvent pressedEvent,
			XRInputEvent releasedEvent
		) {
			if (!device.TryGetFeatureValue(usage, out bool value))
				return;

			var key = $"{handType}_{inputType}";
			var wasPressed = _lastButtonStates.ContainsKey(key) && _lastButtonStates[key];

			var data = new XRInputData(
				handType, inputType,
				value ? XRButtonState.Held : XRButtonState.None,
				value ? 1f : 0f, Vector2.zero, value, device
			);

			// Pressed
			if (value && !wasPressed) {
				data.buttonState = XRButtonState.Pressed;
				pressedEvent?.Invoke(data);
			}
			// Released
			else if (!value && wasPressed) {
				data.buttonState = XRButtonState.Released;
				releasedEvent?.Invoke(data);
			}

			_lastButtonStates[key] = value;
		}

		/// <summary>
		/// Met à jour un axe 2D
		/// </summary>
		private static void UpdateVector2Axis(
			InputDevice device,
			XRHandType handType,
			XRInputType inputType,
			InputFeatureUsage<Vector2> usage,
			XRInputEvent changedEvent
		) {
			if (!device.TryGetFeatureValue(usage, out Vector2 value))
				return;

			var key = $"{handType}_{inputType}";
			var lastValue = _lastVector2Values.ContainsKey(key) 
				? _lastVector2Values[key] 
				: Vector2.zero;

			if (Vector2.Distance(value, lastValue) > 0.01f) {
				var data = new XRInputData(
					handType, inputType, XRButtonState.None,
					value.magnitude, value, false, device
				);
				changedEvent?.Invoke(data);
				_lastVector2Values[key] = value;
			}
		}

		/// <summary>
		/// Obtient la position d'une main
		/// </summary>
		public static bool TryGetHandPosition(XRHandType handType, out Vector3 position, out Quaternion rotation) {
			position = Vector3.zero;
			rotation = Quaternion.identity;

			if (!TryGetDevice(handType, out var device))
				return false;

			var hasPos = device.TryGetFeatureValue(CommonUsages.devicePosition, out position);
			var hasRot = device.TryGetFeatureValue(CommonUsages.deviceRotation, out rotation);

			return hasPos && hasRot;
		}

		/// <summary>
		/// Vérifie si un bouton est actuellement pressé
		/// </summary>
		public static bool IsButtonPressed(XRHandType handType, XRInputType inputType, float threshold = 0.9f) {
			if (!TryGetDevice(handType, out var device))
				return false;

			switch (inputType) {
				case XRInputType.Trigger:
					return device.TryGetFeatureValue(CommonUsages.trigger, out float trigger) 
						&& trigger >= threshold;
				
				case XRInputType.Grip:
					return device.TryGetFeatureValue(CommonUsages.grip, out float grip) 
						&& grip >= threshold;
				
				case XRInputType.PrimaryButton:
					return device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primary) 
						&& primary;
				
				case XRInputType.SecondaryButton:
					return device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondary) 
						&& secondary;
				
				case XRInputType.Primary2DAxisClick:
					return device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool click) 
						&& click;
				
				case XRInputType.MenuButton:
					return device.TryGetFeatureValue(CommonUsages.menuButton, out bool menu) 
						&& menu;
				
				default:
					return false;
			}
		}

		/// <summary>
		/// Obtient la valeur d'un axe 2D
		/// </summary>
		public static bool TryGetAxis2DValue(XRHandType handType, XRInputType axisType, out Vector2 value) {
			value = Vector2.zero;
			
			if (!TryGetDevice(handType, out var device))
				return false;

			var usage = axisType == XRInputType.Primary2DAxis 
				? CommonUsages.primary2DAxis 
				: CommonUsages.secondary2DAxis;

			return device.TryGetFeatureValue(usage, out value);
		}

		/// <summary>
		/// Nettoie les ressources
		/// </summary>
		public static void Cleanup() {
			_devices.Clear();
			_lastButtonStates.Clear();
			_lastFloatValues.Clear();
			_lastVector2Values.Clear();
			
			OnTriggerPressed?.RemoveAllListeners();
			OnTriggerReleased?.RemoveAllListeners();
			OnTriggerValue?.RemoveAllListeners();
			OnGripPressed?.RemoveAllListeners();
			OnGripReleased?.RemoveAllListeners();
			OnGripValue?.RemoveAllListeners();
			OnPrimaryButtonPressed?.RemoveAllListeners();
			OnPrimaryButtonReleased?.RemoveAllListeners();
			OnSecondaryButtonPressed?.RemoveAllListeners();
			OnSecondaryButtonReleased?.RemoveAllListeners();
			OnPrimary2DAxisChanged?.RemoveAllListeners();
			OnSecondary2DAxisChanged?.RemoveAllListeners();
			OnPrimary2DAxisClickPressed?.RemoveAllListeners();
			OnPrimary2DAxisClickReleased?.RemoveAllListeners();
			OnMenuButtonPressed?.RemoveAllListeners();
			OnMenuButtonReleased?.RemoveAllListeners();
		}
	}
}

