using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace Nox.XR.Connectors {
	public class PokeInteractor : XRPokeInteractor {

		public bool Enable {
			get => enabled;
			set => enabled = value;
		}

		public float Radius {
			get => pokeWidth;
			set {
				requirePokeFilter = false;
				pokeWidth = value;
				pokeSelectWidth = value;
				pokeHoverRadius = value;
			}
		}

		private void OnDrawGizmos() {
			Gizmos.color = Color.red;
			Gizmos.DrawWireSphere(transform.position, pokeWidth * transform.lossyScale.x);
		}
	}
}
