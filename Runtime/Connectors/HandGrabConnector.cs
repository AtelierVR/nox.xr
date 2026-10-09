using Autohand;
using UnityEngine;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Drives an AutoHand <see cref="Hand"/> grab/release from the XR key bindings of the active
	/// runtime (see <see cref="Keybindings"/>).
	///
	/// <para>
	/// The AutoHand input bridge shipped with the package (<c>Autohand.Demo.XRHandControllerLink</c>)
	/// only lives on the fallback <c>RobotHand</c> prefabs and targets their own <see cref="Hand"/>;
	/// the player, however, grabs with the physical duplicate built by <c>NoxAutoHandVRIK</c>, which
	/// has no input component at all. This connector is installed on those duplicates so a pressed
	/// binding actually reaches the hand the <c>AutoHandPlayer</c> drives.
	/// </para>
	///
	/// <para>
	/// The binding is read live every frame: it is an axis, so the press is detected on both edges
	/// (rising → <see cref="Hand.Grab"/>, falling → <see cref="Hand.Release"/>). No
	/// <c>SetGrip</c> is issued here: the finger curl is already driven by
	/// <c>FingerKeybindConnector</c>.
	/// </para>
	/// </summary>
	[RequireComponent(typeof(Hand))]
	public class HandGrabConnector : MonoBehaviour {
		/// <summary>
		/// Base key of the grab binding, suffixed with the hand side: <c>select.left</c> /
		/// <c>select.right</c> (the project maps <c>select</c> to the controller grip).
		/// </summary>
		[Tooltip("Base key of the grab binding, suffixed with the hand side ('select.left' / 'select.right').")]
		public string grabBinding = "select";

		/// <summary>Value at or above which the grab binding counts as pressed (the grip axis rests around 0.15).</summary>
		[Tooltip("Value at or above which the grab binding counts as pressed.")]
		[Range(0f, 1f)] public float grabThreshold = 0.5f;

		private Hand _hand;
		private string _key;
		private bool _pressed;

		private void Awake() {
			_hand = GetComponent<Hand>();
			_key  = $"{grabBinding}.{(_hand != null && _hand.left ? "left" : "right")}";
		}

		private void Update() {
			if (_hand == null || string.IsNullOrEmpty(_key))
				return;

			var pressed = Keybindings.GetFloatValue(_key) >= grabThreshold;
			if (pressed == _pressed)
				return;

			_pressed = pressed;
			if (!pressed) {
				_hand.Release();
				return;
			}

			if (!CanGrabTarget())
				return;

			_hand.Grab();
		}

		/// <summary>
		/// Asks the <see cref="AdditionalGrabbable"/> sitting next to the highlighted grabbable whether the grab
		/// is allowed: it adds its conditions (position / angle of the hand relative to the collider) on top of
		/// the grabbable's own <see cref="Grabbable.CanGrab"/>. Without an <see cref="AdditionalGrabbable"/> the
		/// fallback is the grabbable's own <see cref="Grabbable.CanGrab"/>, i.e. AutoHand's default behaviour.
		/// </summary>
		private bool CanGrabTarget() {
			var highlighter = _hand.GetComponent<HandGrabbableHighlighter>();
			var grabbable   = highlighter != null ? highlighter.currentHighlightTarget : null;
			if (grabbable == null)
				return true;

			var extra = grabbable.GetComponent<AdditionalGrabbable>()
			         ?? grabbable.GetComponentInChildren<AdditionalGrabbable>(true);

			return extra != null 
                ? extra.CanGrab(_hand) 
                : grabbable.CanGrab(_hand);
		}
	}
}
