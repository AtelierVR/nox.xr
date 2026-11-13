using System;
using System.Collections.Generic;
using Nox.Avatars.Hand;
using Nox.CCK.Utils;
using Nox.Interactions;
using Nox.XR.Runtime;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Settings;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Poke interactors of a hand part: one per fingertip of the avatar hand, driven by the finger bindings.
	/// <para>
	/// The pokes follow <see cref="Hand.SourceChanged"/>, the way the fingers do: they are built once, when the
	/// avatar arrives, and released when it goes away. <see cref="PokeSettings"/> decides which fingers poke -
	/// a grabbing hand pokes with none, an open hand keeps the index only when the setting asks for it.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Hand Pokes")]
	[RequireComponent(typeof(Hand))]
	public class HandPokes : MonoBehaviour {
		[SerializeField, Tooltip("Hand part of this side. Found on this GameObject when empty.")]
		private Hand hand;

		[SerializeField, Tooltip("Equip the fingertips of the avatar hand with poke interactors.")]
		private bool addPokes = true;

		/// <summary>Pokes of the current avatar hand, with the binding key of their finger.</summary>
		private readonly List<(string key, PokeInteractor poke)> _pokes = new();

		/// <summary>Last value of each finger binding, to detect a fully open hand.</summary>
		private readonly Dictionary<string, float> _values = new();

		private IGrabber _grabber;

		/// <summary>Hand part these pokes belong to.</summary>
		public Hand Hand {
			get => hand;
			set => hand = value;
		}

		/// <summary>Whether the fingertips are equipped with pokes.</summary>
		public bool AddPokes {
			get => addPokes;
			set {
				if (addPokes == value)
					return;

				addPokes = value;

				if (!isActiveAndEnabled)
					return;

				if (value)
					Bind(hand ? hand.Source : null);
				else
					Release();
			}
		}

		/// <summary>Pokes currently equipped.</summary>
		public IReadOnlyList<(string key, PokeInteractor poke)> Pokes
			=> _pokes;

		private void Reset()
			=> hand = GetComponent<Hand>();

		private void OnValidate() {
			if (!hand)
				hand = GetComponent<Hand>();
		}

		private void Awake() {
			if (!hand)
				hand = GetComponent<Hand>();
		}

		private void OnEnable() {
			if (!hand)
				return;

			// The part announces its avatar: the pokes are bound when it arrives, never polled.
			hand.SourceChanged.RemoveListener(OnSourceChanged);
			hand.SourceChanged.AddListener(OnSourceChanged);
			OnSourceChanged(hand.Source);

			PokeSettings.Changed.RemoveListener(OnPokeSettingChanged);
			PokeSettings.Changed.AddListener(OnPokeSettingChanged);
			PokeSettings.DisablePercentChanged.RemoveListener(OnPokeThresholdChanged);
			PokeSettings.DisablePercentChanged.AddListener(OnPokeThresholdChanged);
		}

		private void OnDisable() {
			if (hand)
				hand.SourceChanged.RemoveListener(OnSourceChanged);

			PokeSettings.Changed.RemoveListener(OnPokeSettingChanged);
			PokeSettings.DisablePercentChanged.RemoveListener(OnPokeThresholdChanged);

			Release();
		}

		private void OnPokeSettingChanged(bool _)
			=> Refresh();

		private void OnPokeThresholdChanged(float _)
			=> Refresh();

		private void Update()
			=> Refresh();

		private void OnSourceChanged(IHand source)
			=> Bind(source);

		/// <summary>
		/// Gives every fingertip of the avatar hand a poke interactor, sized from the fingertip radius so the
		/// poke keeps following the avatar scale.
		/// </summary>
		private void Bind(IHand source) {
			Release();

			if (!addPokes || source?.Fingers == null || source.Fingers.Length == 0)
				return;

			var side = hand ? (hand.Side == HandSide.Left ? "left" : "right") : "right";

			for (var i = 0; i < source.Fingers.Length; i++) {
				var finger = source.Fingers[i];
				if (finger?.Tip == null)
					continue;

				var poke = finger.Tip.gameObject.GetOrAddComponent<PokeInteractor>();
				poke.localRadius = finger.TipRadius > 0f ? finger.TipRadius : 0.01f;
				poke.Radius = poke.localRadius * Mathf.Abs(finger.Tip.lossyScale.x);

				_pokes.Add((Key(side, finger.Type), poke));
			}

			Refresh();
		}

		/// <summary>Drops the pokes of the avatar hand that is going away.</summary>
		private void Release() {
			for (var i = 0; i < _pokes.Count; i++) {
				var poke = _pokes[i].poke;
				if (!poke)
					continue;

				poke.Enable = false;
				Destroy(poke);
			}

			_pokes.Clear();
			_values.Clear();
		}

		/// <summary>
		/// Re-reads the finger bindings and the grab state every frame: the values come from the running XR
		/// runtime (there is no event to listen to) and a hand can start a grab without its fingers crossing
		/// the threshold.
		/// </summary>
		private void Refresh() {
			if (_pokes.Count == 0)
				return;

			var threshold = PokeSettings.DisablePokePercent;
			var module    = XRInteractorBinder.Current();

			// A fully open hand would poke with all five fingers: that state is detected here.
			var allOpen = true;
			for (var i = 0; i < _pokes.Count; i++) {
				var key   = _pokes[i].key;
				var value = Keybindings.GetFloatValue(key);
				_values[key] = value;

				if (value >= threshold)
					allOpen = false;
			}

			// A hand grabbing something does not poke: it is busy with the object.
			if (_grabber == null)
				_grabber = GetComponent<IGrabber>();

			var blocked = !PokeSettings.Enabled || (_grabber != null && _grabber.State == GrabState.Held);

			for (var i = 0; i < _pokes.Count; i++) {
				var (key, poke) = _pokes[i];
				if (!poke)
					continue;

				var value = _values.TryGetValue(key, out var v) ? v : 0f;

				// An open hand cuts every poke; PokeSettings.OpenHandIndex keeps the index one, to point.
				var enabled = !blocked && value < threshold
					&& (!allOpen || (PokeSettings.OpenHandIndex && IsIndexFinger(key)));

				if (poke.Enable != enabled)
					poke.Enable = enabled;

				// Keeps the poke on the manager of the rig and of the UI module in use: a poke built before a
				// desktop -> VR switch keeps the module of the previous controller and never reaches the UI.
				XRInteractorBinder.Bind(poke, module);
			}
		}

		/// <summary>Is this finger binding key the index finger (<c>finger.&lt;side&gt;.index</c>)?</summary>
		private static bool IsIndexFinger(string key)
			=> key != null && key.EndsWith(".index", StringComparison.Ordinal);

		/// <summary>Binding key of a finger: <c>finger.&lt;side&gt;.&lt;type&gt;</c>, as <c>XRBinding</c> declares it.</summary>
		private static string Key(string side, FingerType type)
			=> $"finger.{side}.{type.ToString().ToLowerInvariant()}";
	}
}
