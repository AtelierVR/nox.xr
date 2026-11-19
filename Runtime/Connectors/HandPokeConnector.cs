using System;
using System.Collections.Generic;
using Autohand;
using Nox.XR.Runtime.Settings;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Drives the <see cref="PokeInteractor"/> of every fingertip of a hand.
	///
	/// <para>
	/// Rules: a <b>fully open</b> hand cuts every poke, so pointing with an extended finger never pokes
	/// by itself; the <c>PokeSettings.OpenHandIndex</c> setting (off by default) restores the previous
	/// behaviour where only the index finger poked on an open hand. As soon as one finger is bent, the
	/// <c>poke disable threshold</c> takes over: only the extended fingers (value below the
	/// threshold) keep their poke. The <b>thumb</b> only pokes when it is the only extended finger, so an
	/// index pointing next to a raised thumb does not poke with both tips. Finally, a hand that is
	/// <b>grabbing</b> something also cuts all of its pokes.
	/// </para>
	/// </summary>
	public class HandPokeConnector : MonoBehaviour {
		private Hand _hand;
		private Func<bool> _grabbing;
		private readonly Dictionary<string, float> _fingerValues = new();
		private readonly Dictionary<string, bool> _bent = new();
		private (string key, PokeInteractor poke)[] _pokes = Array.Empty<(string, PokeInteractor)>();

		/// <summary>
		/// Hand that actually carries the grab: the physical duplicate created by <c>NoxAutoHandVRIK</c>.
		/// This connector normally lives on that very duplicate — the pokes and the colliders belong to the
		/// body that collides and grabs — and the reference is then left <c>null</c>, the local hand being
		/// used. Set it explicitly only when the pokes sit on another hand than the one that grabs (the
		/// avatar's own hand kept as the physical body when no duplicate is built).
		/// </summary>
		public Hand physicalHand { get; set; }

		public void Setup(Hand hand, (string key, PokeInteractor poke)[] pokes) {
			_hand = hand;
			_grabbing = null;
			_pokes = pokes ?? Array.Empty<(string key, PokeInteractor poke)>();
			RefreshAllPokes();
		}

		/// <summary>
		/// Drives pokes that do not belong to an AutoHand hand: the physical hand of a <c>Nox.XR.Player.Hand</c>,
		/// which builds its own fingers and pokes. The grab state then comes from <paramref name="grabbing"/>.
		/// </summary>
		public void SetupPokes((string key, PokeInteractor poke)[] pokes, Func<bool> grabbing) {
			_hand = null;
			_grabbing = grabbing;
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
			if (_hand == null && _grabbing == null)
				return;

			RefreshAllPokes();
		}

		private void RefreshAllPokes() {
			var threshold = PokeSettings.DisablePokePercent;
			var release   = Mathf.Max(0f, threshold - PokeSettings.BEND_HYSTERESIS);
			var module    = XRInteractorBinder.Current();

			// Read every finger and detect a "fully open" hand: NO finger bent past the threshold.
			// In that case the hand would poke with all of its fingers -> we cut them all.
			var allOpen = _pokes.Length > 0;
			var otherUp = false;

			for (var i = 0; i < _pokes.Length; i++) {
				var key   = _pokes[i].key;
				var value = Keybindings.GetFloatValue(key);
				_fingerValues[key] = value;

				var wasBent = _bent.TryGetValue(key, out var bent) && bent;
				var isBent  = value >= (wasBent ? release : threshold);
				_bent[key]  = isBent;

				if (isBent)
					allOpen = false;
				else if (!IsThumbFinger(key))
					otherUp = true;
			}

			// A hand grabbing something does not poke: it is busy with the object.
			var blocked = !PokeSettings.Enabled || IsGrabbing();

			for (var i = 0; i < _pokes.Length; i++) {
				var poke = _pokes[i].poke;
				if (poke == null) continue;

				var value = _fingerValues.TryGetValue(_pokes[i].key, out var v) ? v : 0f;

				// The thumb only pokes when it is the only extended finger: an extended index in front of an
				// extended thumb (a pointing hand) would poke with both tips at once, and the thumb presses
				// whatever it grazes next to the button the index aims at.
				var thumbOnly = !IsThumbFinger(_pokes[i].key) || !otherUp;

				// A fully open hand cuts every poke by default: an unrestricted open hand would poke with
				// all five fingers. `PokeSettings.OpenHandIndex` (off by default) restores the opposite -
				// only the index finger pokes on an open hand, to point with the index and poke.
				// A poke that is already interacting stays on: cutting it mid-press cancels the UI press and
				// the player gets the hover sound without the click.
				var interacting = poke.Enable && (poke.hasHover || poke.hasSelection || PressesUi(poke, module));
				var enabled = !blocked && (interacting
					|| (thumbOnly && value < threshold && (!allOpen || (PokeSettings.OpenHandIndex && IsIndexFinger(_pokes[i].key)))));
				if (poke.Enable != enabled)
					poke.Enable = enabled;

				// Drops the element the graphic raycaster keeps for this poke while it is not selecting: it only
				// ever replaces that hit with one behind it, so a label reached once locked the poke out of every
				// other element (see XRInteractorBinder.ForgetUiHit).
				if (!TrackedDeviceGraphicRaycaster.IsPokeSelectingWithUI(poke))
					XRInteractorBinder.ForgetUiHit(poke);

				// Keeps the poke on the rig's manager and on the UI module in use: a poke built before the
				// desktop -> VR switch keeps the module of the previous controller and never reaches the UI.
				XRInteractorBinder.Bind(poke, module);
			}
		}

		/// <summary>Is this finger binding key the thumb (<c>finger.&lt;side&gt;.thumb</c>)?</summary>
		private static bool IsThumbFinger(string key)
			=> key != null && key.EndsWith(".thumb", StringComparison.Ordinal);

		/// <summary>Is this finger binding key the index finger (<c>finger.&lt;side&gt;.index</c>)?</summary>
		private static bool IsIndexFinger(string key)
			=> key != null && key.EndsWith(".index", StringComparison.Ordinal);

		/// <summary>Whether the poke is currently pressing a UI element through the XR UI module.</summary>
		private static bool PressesUi(PokeInteractor poke, XRUIInputModule module)
			=> module && module.GetTrackedDeviceModel(poke, out var model) && model.select;

		private bool IsGrabbing() {
			if (_grabbing != null)
				return _grabbing();

			var hand = physicalHand != null ? physicalHand : _hand;
			return hand != null && (hand.IsGrabbing() || hand.IsHolding());
		}
	}
}
