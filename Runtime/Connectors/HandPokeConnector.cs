using System;
using System.Collections.Generic;
using Autohand;
using Nox.XR.Runtime.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Drives the <see cref="PokeInteractor"/> of every fingertip of a hand.
	///
	/// <para>
	/// Rules: a <b>fully open</b> hand (no finger bent past the threshold) has <b>no</b> poke at
	/// all - otherwise every finger would pass the "extended finger" test and a flat hand would poke
	/// with all five fingers. As soon as one finger is bent, the <c>poke disable threshold</c> takes
	/// over: only the extended fingers (value below the threshold) keep their poke. Finally, a hand
	/// that is <b>grabbing</b> something also cuts all of its pokes.
	/// </para>
	/// </summary>
	public class HandPokeConnector : MonoBehaviour {
		private Hand _hand;
		private readonly Dictionary<string, float> _fingerValues = new();
		private (string key, PokeInteractor poke)[] _pokes = Array.Empty<(string, PokeInteractor)>();

		/// <summary>
		/// Hand that actually carries the grab: the physical duplicate created by <c>NoxAutoHandVRIK</c>.
		/// This connector lives on the armature hand (kinematic), which never grabs itself;
		/// <c>NoxAutoHandVRIK</c> fills this reference. When absent, the local hand is used instead.
		/// </summary>
		public Hand physicalHand { get; set; }

		public void Setup(Hand hand, (string key, PokeInteractor poke)[] pokes) {
			_hand = hand;
			_pokes = pokes ?? Array.Empty<(string, PokeInteractor)>();
			RefreshAllPokes();
		}

		private void OnEnable() {
			PokeSettings.Changed.AddListener(OnGlobalPokeChanged);
			PokeSettings.DisablePercentChanged.AddListener(OnDisablePercentChanged);
			RefreshAllPokes();
		}

		private void OnDisable() {
			PokeSettings.Changed.RemoveListener(OnGlobalPokeChanged);
			PokeSettings.DisablePercentChanged.RemoveListener(OnDisablePercentChanged);
		}

		private void OnGlobalPokeChanged(bool _) 
			=> RefreshAllPokes();

		private void OnDisablePercentChanged(float _) 
			=> RefreshAllPokes();

		/// <summary>
		/// Re-reads the finger bindings and the grab state every frame: the values come from the
		/// running XR runtime (there is no event to listen to) and the hand can start a grab without
		/// its fingers crossing the threshold.
		/// </summary>
		private void Update() {
			if (_hand == null)
				return;

			RefreshAllPokes();
		}

		private void RefreshAllPokes() {
			var threshold = PokeSettings.DisablePokePercent;

			// Read every finger and detect a "fully open" hand: NO finger bent past the threshold.
			// In that case the hand would poke with all of its fingers -> we cut them all.
			var allOpen = _pokes.Length > 0;
			for (var i = 0; i < _pokes.Length; i++) {
				var key   = _pokes[i].key;
				var value = Keybindings.GetFloatValue(key);
				_fingerValues[key] = value;
				if (value >= threshold)
					allOpen = false;
			}

			// A hand grabbing something does not poke: it is busy with the object.
			var blocked = !PokeSettings.Enabled || allOpen || IsGrabbing();

			for (var i = 0; i < _pokes.Length; i++) {
				var poke = _pokes[i].poke;
				if (poke == null) continue;

				var value   = _fingerValues.TryGetValue(_pokes[i].key, out var v) ? v : 0f;
				var enabled = !blocked && value < threshold;
				if (poke.Enable != enabled)
					poke.Enable = enabled;
			}
		}

		private bool IsGrabbing() {
			var hand = physicalHand != null ? physicalHand : _hand;
			return hand != null && (hand.IsGrabbing() || hand.IsHolding());
		}
	}
}
