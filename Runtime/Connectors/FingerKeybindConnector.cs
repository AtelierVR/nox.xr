using Autohand;
using UnityEngine;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Alimente la flexion d'un doigt AutoHand depuis un binding XR logique
	/// (<see cref="Keybindings.GetFloatValue(string)"/>), par exemple <c>finger.left.index</c>.
	///
	/// <para>
	/// La valeur est relue à chaque frame : c'est le runtime XR actif qui détient le binding, et
	/// la flexion n'est réécrite que si elle change.
	/// </para>
	/// </summary>
	[RequireComponent(typeof(Finger))]
	public class FingerKeybindConnector : MonoBehaviour {
		[Tooltip("Clé du binding XR qui pilote ce doigt (ex. finger.left.index).")]
		public string BindKey;

		private Finger _finger;
		private float  _bendOffset = float.NaN;

		private void Awake()
			=> _finger = GetComponent<Finger>();

		private void Update() {
			if (_finger == null || string.IsNullOrEmpty(BindKey))
				return;

			var value = Keybindings.GetFloatValue(BindKey);
			if (Mathf.Approximately(value, _bendOffset))
				return;

			_bendOffset       = value;
			_finger.bendOffset = value;
		}
	}
}