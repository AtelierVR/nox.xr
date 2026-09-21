using System;
using System.Collections.Generic;
using Autohand;
using Nox.XR.Runtime.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Connectors {
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
		/// Relit les bindings des doigts de cette main et ne rafraîchit que ceux qui ont changé :
		/// les valeurs sont lues auprès du runtime XR actif, il n'y a plus d'événement à écouter.
		/// </summary>
		private void Update() {
			if (_hand == null)
				return;

			foreach (var (key, _) in _pokes) {
				var value = Keybindings.GetFloatValue(key);
				if (_fingerValues.TryGetValue(key, out var last) && Mathf.Approximately(last, value))
					continue;

				_fingerValues[key] = value;
				RefreshPoke(key);
			}
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
