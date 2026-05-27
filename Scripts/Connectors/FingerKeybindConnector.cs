using UnityEngine;
using Autohand;

namespace Nox.XR.Connectors {
	[RequireComponent(typeof(Finger))]
	public class FingerKeybindConnector : MonoBehaviour {
		public string BindKey;
		private Finger _finger;

		private void Awake() {
			_finger = GetComponent<Finger>();
		}

		private void OnEnable() {
			Keybindings.KeyFloatEvent.AddListener(OnFloatKey);
		}

		private void OnDisable() {
			Keybindings.KeyFloatEvent.RemoveListener(OnFloatKey);
		}

		private void OnFloatKey(string key, float value, float oldValue) {
			if (key == BindKey && _finger != null) {
				_finger.bendOffset = value;
			}
		}
	}
}