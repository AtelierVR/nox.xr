using Autohand;
using UnityEngine;

namespace Nox.XR.Connectors {
	public class KeybindControllerLink : MonoBehaviour {
		public AutoHandPlayer player;

		private void OnEnable() {
			Keybindings.KeyFloatEvent.AddListener(OnFloatKey);
		}

		private void OnDisable() {
			Keybindings.KeyFloatEvent.RemoveListener(OnFloatKey);
		}

		private void OnFloatKey(string key, float value, float oldValue) {
			if (key == "jump" && value > 0.1f && oldValue <= 0.1f)
				player.Jump();
		}

		private void FixedUpdate() {
			player.Move(Keybindings.GetVector2Value("move"));
			player.Turn(Keybindings.GetVector2Value("turn").x);
		}

		private void Update() {
			player.Move(Keybindings.GetVector2Value("move"));
			player.Turn(Keybindings.GetVector2Value("turn").x);
		}
	}
}
