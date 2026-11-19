using Nox.Interactions;
using Nox.XR.Bindings;
using Nox.XR.Player;
using Nox.XR.Runtime;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Interactions {
	/// <summary>
	/// Bridges the XR grab binding to the two grabbers of a hand, the way the v1 <c>HandGrabConnector</c>
	/// drove AutoHand's <c>Hand</c>: the rising edge grabs (near grab first, then a distance pull), the
	/// falling edge releases with the hand throw power.
	/// <para>
	/// The binding is read live every frame - it is an axis, so both edges are detected - and nothing is
	/// cached: the active runtime owns the values.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Interactions/Hand Grab Input")]
	[DefaultExecutionOrder(2002)]
	[RequireComponent(typeof(Hand))]
	public class HandGrabInput : MonoBehaviour {
		[SerializeField, Tooltip("Hand part whose binding is read. Found on this GameObject when empty.")]
		private Hand hand;

		[SerializeField, Tooltip("Near grabber driven by this input: the hand itself. Resolved from the hand when empty.")]
		private Hand near;

		[SerializeField, Tooltip("Distance grabber driven by this input. Found on this GameObject when empty.")]
		private HandDistanceGrabber far;

		[SerializeField, Tooltip("Base key of the grab binding, suffixed with the hand side ('select.left' / 'select.right').")]
		private string grabBinding = "select";

		[SerializeField, Tooltip("Fallback grab binding tried with the main one (AutoHand grabs with the trigger). Empty disables it.")]
		private string altGrabBinding = "activate";

		[SerializeField, Range(0f, 1f), Tooltip("Value at or above which the grab binding counts as pressed.")]
		private float grabThreshold = 0.5f;

		[SerializeField, Min(0f), Tooltip("Throw power applied when the binding is released.")]
		private float throwPower = 1.25f;

		[SerializeField, Range(0f, 1f), Tooltip("Value at or above which the squeeze binding crushes the held object.")]
		private float squeezeThreshold = 0.8f;

		[SerializeField, Range(0f, 1f), Tooltip("Value at or below which the squeeze is released, so it cannot blink at the threshold.")]
		private float squeezeRelease = 0.6f;

		private string _key;
		private string _altKey;
		private bool _pressed;

		private IGrabSqueeze _squeezed;
		private bool _squeezing;

		/// <summary>Hand part this input belongs to.</summary>
		public Hand HandPart {
			get => hand;
			set => hand = value;
		}

		/// <summary>Wires the input to its hand and grabbers.</summary>
		public void Configure(Hand part, HandDistanceGrabber farGrabber, float throwMultiplier = 1.25f) {
			hand = part;
			near = part;
			far = farGrabber;
			throwPower = throwMultiplier;
			RefreshKey();
		}

		private void Awake() {
			if (!hand)
				hand = GetComponent<Hand>();

			if (!near)
				near = hand;

			if (!far)
				far = GetComponent<HandDistanceGrabber>();

			RefreshKey();
		}

		private void OnEnable() {
			_pressed = false;
			RefreshKey();
		}

		private void RefreshKey() {
			var side = hand && hand.Side == HandSide.Left ? "left" : "right";
			_key = $"{grabBinding}.{side}";
			_altKey = string.IsNullOrEmpty(altGrabBinding) ? null : $"{altGrabBinding}.{side}";
		}

		private void Update() {
			if (!hand)
				hand = GetComponent<Hand>();

			if (!hand)
				return;

			if (!near)
				near = hand;

			if (!far)
				far = GetComponent<HandDistanceGrabber>();

			if (string.IsNullOrEmpty(_key))
				RefreshKey();

			UpdateSqueeze();

			var value = Keybindings.GetFloatValue(_key);
			if (!string.IsNullOrEmpty(_altKey))
				value = Mathf.Max(value, Keybindings.GetFloatValue(_altKey));

			var pressed = value >= grabThreshold;
			if (pressed == _pressed)
				return;

			_pressed = pressed;

			if (!pressed) {
				if (near != null && near.Release(throwPower))
					return;

				if (far != null && far.Release(throwPower))
					return;

				far?.Cancel();
				return;
			}

			Logger.LogDebug(
				$"[HandGrab] {_key}={Keybindings.GetFloatValue(_key):0.00} pressed; near={(near ? near.CurrentTarget?.Target : null)}, far={(far ? far.CurrentTarget?.Target : null)}",
				this, tag: nameof(HandGrabInput));

			// the palm always goes first: a fresh targeting pass decides what it can take now, so the ray never
			// steals an object the hand could have grabbed
			if (near != null) {
				near.RefreshTarget();

				if (near.Grab())
					return;
			}

			if (far != null && far.Select())
				far.Pull();
		}

		/// <summary>
		/// Crushes the object the hand holds, the way AutoHand fires <c>onSqueeze</c> when the squeeze input is pressed on a
		/// held grabbable: the grip axis of the hand is the squeeze here, so closing the hand further on what it already
		/// carries is the gesture. The object decides what it means (a shape key, a sound, a break).
		/// </summary>
		private void UpdateSqueeze() {
			var grabber = near != null && near.HeldGrabbable != null
				? (IGrabber)near
				: far != null && far.HeldGrabbable != null ? far : null;

			var held = grabber != null ? grabber.HeldGrabbable as IGrabSqueeze : null;

			if (held == null) {
				ReleaseSqueeze();
				return;
			}

			var left = hand && hand.Side == HandSide.Left;
			var grip = Keybindings.GetFloatValue(left ? XRBinding.FingerLeftMiddle : XRBinding.FingerRightMiddle);

			if (!_squeezing && grip >= squeezeThreshold) {
				_squeezing = true;
				_squeezed  = held;
				held.Squeeze(grabber);
				return;
			}

			if (_squeezing && grip <= squeezeRelease) {
				_squeezing = false;
				_squeezed  = null;
				held.Unsqueeze(grabber);
			}
		}

		private void ReleaseSqueeze() {
			if (!_squeezing)
				return;

			_squeezing = false;

			if (_squeezed == null)
				return;

			_squeezed.Unsqueeze(near);
			_squeezed = null;
		}
	}
}
