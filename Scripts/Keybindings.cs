using System;
using System.Linq;
using Nox.KeyBindings;
using UnityEngine;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR {
	/// <summary>
	/// A static class that manages key bindings for the player system.
	/// </summary>
	public static class Keybindings {
		/// <summary>
		/// A collection of key bindings used by the player system.
		/// </summary>
		private static readonly (string, string, string, Action<float>, float)[] FloatKeys = {
			("nox.ui", "menu", "<Keyboard>/tab", value => SetFloatValue("menu", value), 0f),
			("nox.ui", "menu.left", "<XRController>{LeftHand}/{SecondaryButton}", value => SetFloatValue("menu.left", value), 0f),
			("nox.ui", "menu.right", "<XRController>{RightHand}/{SecondaryButton}", value => SetFloatValue("menu.right", value), 0f),
			("nox.movement", "jump", "<XRController>{LeftHand}/{PrimaryButton}", value => SetFloatValue("jump", value), 0f),
			
			("nox.hand", "finger.left.thumb", "<XRController>{LeftHand}/{PrimaryTouch}", value => SetFloatValue("finger.left.thumb", value), 0f),
			("nox.hand", "finger.left.index", "<XRController>{LeftHand}/{Trigger}", value => SetFloatValue("finger.left.index", value), 0f),
			("nox.hand", "finger.left.middle", "<XRController>{LeftHand}/{Grip}", value => SetFloatValue("finger.left.middle", value), 0f),
			("nox.hand", "finger.left.ring", "<XRController>{LeftHand}/{Grip}", value => SetFloatValue("finger.left.ring", value), 0f),
			("nox.hand", "finger.left.pinky", "<XRController>{LeftHand}/{Grip}", value => SetFloatValue("finger.left.pinky", value), 0f),
			
			("nox.hand", "finger.right.thumb", "<XRController>{RightHand}/{PrimaryTouch}", value => SetFloatValue("finger.right.thumb", value), 0f),
			("nox.hand", "finger.right.index", "<XRController>{RightHand}/{Trigger}", value => SetFloatValue("finger.right.index", value), 0f),
			("nox.hand", "finger.right.middle", "<XRController>{RightHand}/{Grip}", value => SetFloatValue("finger.right.middle", value), 0f),
			("nox.hand", "finger.right.ring", "<XRController>{RightHand}/{Grip}", value => SetFloatValue("finger.right.ring", value), 0f),
			("nox.hand", "finger.right.pinky", "<XRController>{RightHand}/{Grip}", value => SetFloatValue("finger.right.pinky", value), 0f),
		};

		private static readonly (string, string, string, Action<Vector2>, Vector2)[] Vector2Keys = {
			("nox.movement", "move", "<XRController>{LeftHand}/Primary2DAxis", value => SetVector2Value("move", value), Vector2.zero),
			("nox.movement", "turn", "<XRController>{RightHand}/Primary2DAxis", value => SetVector2Value("turn", value), Vector2.zero),
		};

		static readonly internal UnityEvent<string, float, float> KeyFloatEvent = new();
		static readonly internal UnityEvent<string, Vector2, Vector2> KeyVector2Event = new();

		/// <summary>
		/// Gets the Vector2 value of a specific key binding.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public static Vector2 GetVector2Value(string key) {
			var index = Array.FindIndex(Vector2Keys, k => k.Item2 == key);
			return index == -1 ? Vector2.zero : Vector2Keys[index].Item5;
		}

		/// <summary>
		/// Sets the Vector2 value of a specific key binding.
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		private static void SetVector2Value(string key, Vector2 value) {
			var index = Array.FindIndex(Vector2Keys, k => k.Item2 == key);
			if (index == -1) return;
			var keyTuple = Vector2Keys[index];
			var oldValue = keyTuple.Item5;
			keyTuple.Item5 = value;
			Vector2Keys[index] = keyTuple;
			KeyVector2Event.Invoke(key, value, oldValue);
		}

		/// <summary>
		/// Gets the value of a specific key binding.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		private static float GetFloatValue(string key) {
			var index = Array.FindIndex(FloatKeys, k => k.Item2 == key);
			return index == -1 ? 0f : FloatKeys[index].Item5;
		}

		/// <summary>
		/// Checks if a specific key binding is pressed.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public static bool IsPressed(string key)
			=> GetFloatValue(key) > 0.1f;

		/// <summary>
		/// Sets the value of a specific key binding.
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		private static void SetFloatValue(string key, float value) {
			var index = Array.FindIndex(FloatKeys, k => k.Item2 == key);
			if (index == -1)
				return;
			var keyTuple = FloatKeys[index];
			var oldValue = keyTuple.Item5;
			keyTuple.Item5 = value;
			FloatKeys[index]    = keyTuple;
			KeyFloatEvent.Invoke(key, value, oldValue);
		}

		/// <summary>
		/// Gets the key binding manager instance from the player system.
		/// </summary>
		private static IKeyBindingManager Keybinding
			=> Client
				.CoreAPI.ModAPI
				.GetMod("keybinding")
				?.GetInstance<IKeyBindingManager>();

		/// <summary>
		/// Rebinds all key bindings defined in the Keys array.
		/// </summary>
		public static void Rebind() {
			foreach (var key in FloatKeys)
				RebindFloat(key.Item2);
			foreach (var key in Vector2Keys)
				RebindVector2(key.Item2);
		}

		/// <summary>
		/// Rebinds a specific key binding by its ID.
		/// </summary>
		/// <param name="id"></param>
		private static void RebindFloat(string id) {
			var key        = FloatKeys.FirstOrDefault(k => k.Item2 == id);
			var keybinding = Keybinding.AddKeyBinding(key.Item2, key.Item3, key.Item1);
			if (keybinding == null) {
				Logger.LogError($"Failed to add or get key binding for {id}");
				return;
			}

			keybinding.AddListener(key.Item4);
		}

		/// <summary>
		/// Rebinds a specific Vector2 key binding by its ID.
		/// </summary>
		/// <param name="id"></param>
		private static void RebindVector2(string id) {
			var key        = Vector2Keys.FirstOrDefault(k => k.Item2 == id);
			var keybinding = Keybinding.AddKeyBinding(key.Item2, key.Item3, key.Item1);
			if (keybinding == null) {
				Logger.LogError($"Failed to add or get key binding for {id}");
				return;
			}

			keybinding.AddListener(key.Item4);
		}

		/// <summary>
		/// Clears all key bindings defined in the Keys array.
		/// </summary>
		public static void Clear() {
			foreach (var key in FloatKeys) {
				var keybinding = Keybinding.GetKeyBinding(key.Item2, key.Item1);
				keybinding.RemoveListener(key.Item4);
				if (keybinding.GetListenerCount() == 0)
					Keybinding.RemoveKeyBinding(keybinding.GetId(), keybinding.GetCategory());
			}
			foreach (var key in Vector2Keys) {
				var keybinding = Keybinding.GetKeyBinding(key.Item2, key.Item1);
				keybinding.RemoveListener(key.Item4);
				if (keybinding.GetListenerCount() == 0)
					Keybinding.RemoveKeyBinding(keybinding.GetId(), keybinding.GetCategory());
			}
		}
	}
}