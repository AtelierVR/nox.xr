using System;
using System.Linq;
using Nox.KeyBindings;
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
		private static readonly (string, string, string, Action<float>, float)[] Keys = {
			("nox.ui", "menu", "<Keyboard>/tab", value => SetValue("menu", value), 0f),
			("nox.ui", "menu.left", "<XRController>{LeftHand}/{SecondaryButton}", value => SetValue("menu.left", value), 0f),
			("nox.ui", "menu.right", "<XRController>{RightHand}/{SecondaryButton}", value => SetValue("menu.right", value), 0f),
		};

		static readonly internal UnityEvent<string, float, float> KeyEvent = new();

		/// <summary>
		/// Gets the value of a specific key binding.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		private static float GetValue(string key) {
			var index = Array.FindIndex(Keys, k => k.Item2 == key);
			return index == -1 ? 0f : Keys[index].Item5;
		}

		/// <summary>
		/// Checks if a specific key binding is pressed.
		/// </summary>
		/// <param name="key"></param>
		/// <returns></returns>
		public static bool IsPressed(string key)
			=> GetValue(key) > 0.1f;

		/// <summary>
		/// Sets the value of a specific key binding.
		/// </summary>
		/// <param name="key"></param>
		/// <param name="value"></param>
		private static void SetValue(string key, float value) {
			var index = Array.FindIndex(Keys, k => k.Item2 == key);
			if (index == -1)
				return;
			var keyTuple = Keys[index];
			var oldValue = keyTuple.Item5;
			keyTuple.Item5 = value;
			Keys[index]    = keyTuple;
			KeyEvent.Invoke(key, value, oldValue);
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
			foreach (var key in Keys)
				Rebind(key.Item2);
		}

		/// <summary>
		/// Rebinds a specific key binding by its ID.
		/// </summary>
		/// <param name="id"></param>
		private static void Rebind(string id) {
			var key        = Keys.FirstOrDefault(k => k.Item2 == id);
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
			foreach (var key in Keys) {
				var keybinding = Keybinding.GetKeyBinding(key.Item2, key.Item1);
				keybinding.RemoveListener(key.Item4);
				if (keybinding.GetListenerCount() == 0)
					Keybinding.RemoveKeyBinding(keybinding.GetId(), keybinding.GetCategory());
			}
		}
	}
}