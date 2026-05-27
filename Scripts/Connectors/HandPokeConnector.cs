using System;
using System.Collections.Generic;
using Autohand;
using Nox.XR.Settings;
using UnityEngine;

namespace Nox.XR.Connectors {
	public class HandPokeConnector : MonoBehaviour {
		private Hand _hand;
		private readonly Dictionary<string, float> _fingerValues = new();
		private (string key, PokeInteractor poke)[] _pokes = Array.Empty<(string, PokeInteractor)>();

		public void Setup(Hand hand, (string key, PokeInteractor poke)[] pokes) {
			_hand = hand;
			_pokes = pokes ?? Array.Empty<(string, PokeInteractor)>();
			RefreshAllPokes();
		}

		private void OnEnable() {
			Keybindings.KeyFloatEvent.AddListener(OnFloatKey);
			PokeSettings.Changed.AddListener(OnGlobalPokeChanged);
			PokeSettings.DisablePercentChanged.AddListener(OnDisablePercentChanged);
			RefreshAllPokes();
		}

		private void OnDisable() {
			Keybindings.KeyFloatEvent.RemoveListener(OnFloatKey);
			PokeSettings.Changed.RemoveListener(OnGlobalPokeChanged);
			PokeSettings.DisablePercentChanged.RemoveListener(OnDisablePercentChanged);
		}

		private void OnGlobalPokeChanged(bool _) 
			=> RefreshAllPokes();

		private void OnDisablePercentChanged(float _) 
			=> RefreshAllPokes();

		private void OnFloatKey(string key, float value, float oldValue) {
			if (_hand == null) return;

			var side = _hand.left ? "left" : "right";
			if (!key.StartsWith($"finger.{side}.", StringComparison.Ordinal))
				return;

			_fingerValues[key] = value;
			RefreshPoke(key);
		}

		private void RefreshAllPokes() {
			foreach (var entry in _pokes)
				RefreshPoke(entry.key);
		}

		private void RefreshPoke(string key) {
			var threshold = PokeSettings.DisablePokePercent;
			for (var i = 0; i < _pokes.Length; i++) {
				if (_pokes[i].key != key || _pokes[i].poke == null) continue;
				var fingerValue = _fingerValues.TryGetValue(key, out var v) ? v : 0f;
				_pokes[i].poke.Enable = PokeSettings.Enabled && fingerValue < threshold;
			}
		}
	}
}
