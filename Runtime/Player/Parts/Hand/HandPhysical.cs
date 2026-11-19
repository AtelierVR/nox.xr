using System;
using System.Collections.Generic;
using Nox.Avatars.Hand;
using Nox.CCK.Utils;
using Nox.Interactions;
using Nox.XR.Runtime;
using Nox.XR.Runtime.Connectors;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Physical hand of a hand part: it keeps the <see cref="PhysicalHand"/> of the prefab in line with the
	/// avatar of the part.
	/// <para>
	/// The physical hand is the source of the pose: the tracker drives its fingers, and it gives the result
	/// back to the avatar bones. An avatar only changes it when it is loaded - the dimensions of the fingers,
	/// their blend poses, and which fingers exist: a finger the avatar does not carry is dropped, and comes
	/// back with the default shape when no avatar is left.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Hand Physical")]
	[RequireComponent(typeof(Hand))]
	[DefaultExecutionOrder(2200)]
	public class HandPhysical : MonoBehaviour {
		private static readonly FingerType[] _fingerOrder = {
			FingerType.Thumb,
			FingerType.Index,
			FingerType.Middle,
			FingerType.Ring,
			 FingerType.Pinky
		};

		[SerializeField, Tooltip("Hand part owning this body. Found on this GameObject when empty.")]
		private Hand hand;

		[SerializeField, Tooltip("Physical hand of the part. Found in the children when empty.")]
		private PhysicalHand physical;

		private IHand _source;
		private bool _fitted;
		private HandPokeConnector _connector;
		private IGrabber[] _grabbers;
		private float _grabbersAt;

		/// <summary>Physical hand carried by the part.</summary>
		public PhysicalHand Physical {
			get {
				if (!physical)
					physical = GetComponentInChildren<PhysicalHand>(true);

				return physical;
			}
		}

		/// <summary>Solid volume of the palm, used as the body of the hand.</summary>
		public Collider BodyCollider
			=> physical
				? physical.BodyCollider
				: null;

		/// <summary>Solid colliders of the hand: the palm and the fingers.</summary>
		public IReadOnlyList<Collider> Colliders
			=> physical
				? physical.Colliders
				: System.Array.Empty<Collider>();

		/// <summary>Adds every solid collider of the physical hand to <paramref name="results"/>.</summary>
		public void GetBodyColliders(List<Collider> results) {
			if (physical)
				physical.GetBodyColliders(results);
		}

		/// <summary>Pokes of the current hand, with the binding key of their finger.</summary>
		public IReadOnlyList<(string key, PokeInteractor poke)> Pokes
			=> physical
				? physical.Pokes
				: Array.Empty<(string, PokeInteractor)>();

		/// <summary>Whether the physical hand is currently fitted on an avatar (false: the default hand).</summary>
		public bool HasAvatar
			=> _source != null;

		/// <summary>Whether one of the fingertips is pressing something with its poke.</summary>
		public bool Gripping
			=> physical && physical.Gripping;

		/// <summary>Hand part this body belongs to.</summary>
		public Hand Hand {
			get => hand;
			set => hand = value;
		}

		/// <summary>
		/// Set by the pose engine: the physical fingers are written by it, so the fallback bend stays out of the way.
		/// </summary>
		public bool ExternalPose { get; set; }

		private void Reset()
			=> hand = GetComponent<Hand>();

		private void OnValidate() {
			if (!hand)
				hand = GetComponent<Hand>();
		}

		private void Awake() {
			if (!hand)
				hand = GetComponent<Hand>();

			if (!physical)
				physical = GetComponentInChildren<PhysicalHand>(true);
		}

		private void OnEnable() {
			if (!hand)
				return;

			hand.SourceChanged.RemoveListener(OnSourceChanged);
			hand.SourceChanged.AddListener(OnSourceChanged);
			Rebuild(hand.Source);
		}

		private void OnDisable() {
			if (hand)
				hand.SourceChanged.RemoveListener(OnSourceChanged);
		}

		private void LateUpdate() {
			if (!physical)
				return;

			var source = hand ? hand.Source : null;

			if (!ReferenceEquals(source, _source) || !Synced(source))
				Rebuild(source);

			if (hand && physical.SyncKeys(hand.Side))
				RefreshPokes();

			AlignPhysical();

			var fingers = physical.PhysicalFingers;
			var resized = false;

			for (var i = 0; i < fingers.Count; i++) {
				var finger = fingers[i];

				if (finger)
					resized |= finger.Sync();
			}

			if (resized) {
				physical.SizePalm();
				AlignEncapsulation();
				physical.RefreshColliders();
				RefreshPokes();
			}

			for (var i = 0; i < fingers.Count; i++) {
				var finger = fingers[i];

				if (finger && !ExternalPose && !finger.HasAvatar)
					finger.Bend(Mathf.Clamp01(Keybindings.GetFloatValue(Key(finger.Type))));
			}

			for (var i = 0; i < fingers.Count; i++)
				if (fingers[i])
					fingers[i].Mirror();
		}

		private void OnSourceChanged(IHand source)
			=> Rebuild(source);

		/// <summary>Applies an avatar hand to the physical one.</summary>
		public void Apply(IHand source)
			=> Rebuild(source);

		/// <summary>
		/// Keeps the physical hand on the frame of the hand bone: the part sits on the tracked controller, the rig
		/// target of the part is the avatar hand bone, offset by the pivot of the avatar hand. The fingers of the
		/// physical hand are laid out in that frame, so the two hands line up.
		/// </summary>
		private void AlignPhysical() {
			if (!physical || !hand)
				return;

			if (_source == null) {
				physical.transform.localPosition = Vector3.zero;
				physical.transform.localRotation = Quaternion.identity;
				return;
			}

			var target = hand.RigTarget;

			if (!target || target == transform)
				return;

			physical.transform.localPosition = target.localPosition;
			physical.transform.localRotation = target.localRotation;
		}

		/// <summary>
		/// Rebuilds the volume enclosing the whole hand and hands it to the part: the player keeps that volume
		/// out of the geometry, so the fingers are covered and no longer only the palm.
		/// </summary>
		private void AlignEncapsulation() {
			if (!physical)
				return;

			physical.AlignEncapsulation();

			if (hand)
				hand.EncapsulationBox = physical.EncapsulationBox;
		}

		private string Key(FingerType type)
			=> $"finger.{(hand && hand.Side == HandSide.Left ? "left" : "right")}.{type.ToString().ToLowerInvariant()}";
		/// <summary>
		/// Applies the avatar hand to the physical one: a finger it carries is fitted on it, a finger it does not
		/// carry is removed, and no avatar at all restores the default hand.
		/// </summary>
		private void Rebuild(IHand source) {
			_source = source;

			DisableSourceColliders(source);

			if (!physical)
				return;

			AlignPhysical();

			var fingers = source?.Fingers;
			var fitted = fingers != null && fingers.Length > 0;

			_fitted = fitted;

			for (var i = 0; i < _fingerOrder.Length; i++) {
				var type = _fingerOrder[i];
				var finger = fitted ? Find(fingers, type) : null;

				if (fitted && finger == null) {
					physical.Remove(type);
					continue;
				}

				var own = physical.Require(type);
				if (!own)
					continue;

				if (finger != null)
					own.Fit(finger);
				else
					own.Unfit();
			}

			physical.SizePalm();
			AlignEncapsulation();
			physical.RefreshColliders();
			RefreshPokes();
		}

		/// <summary>
		/// Turns off the colliders an avatar brings on its hand bones. The physical hand carries every solid volume of
		/// the hand, on its own body, so those would collide a second time (props, menus, level) on a hand that only
		/// mirrors the pose of the physical one.
		/// </summary>
		private void DisableSourceColliders(IHand source) {
			var anchor = source?.Anchor;
			if (!anchor)
				return;

			var colliders = anchor.GetComponentsInChildren<Collider>(true);

			for (var i = 0; i < colliders.Length; i++)
				if (colliders[i])
					colliders[i].enabled = false;
		}

		/// <summary>
		/// Hands the pokes of the fingers to a <see cref="HandPokeConnector"/>: the AutoHand path only equips the
		/// duplicate it builds, so without this the pokes of the physical hand ignored the poke settings and
		/// stayed on whatever the open hand or a grab should have cut.
		/// </summary>
		private void RefreshPokes() {
			var pokes = Pokes;
			if (pokes.Count == 0)
				return;

			var entries = new (string key, PokeInteractor poke)[pokes.Count];

			for (var i = 0; i < entries.Length; i++)
				entries[i] = pokes[i];

			_connector ??= gameObject.GetOrAddComponent<HandPokeConnector>();
			_connector.SetupPokes(entries, Busy);

			for (var i = 0; i < entries.Length; i++)
				if (entries[i].poke)
					entries[i].poke.RefreshGrip();
		}

		/// <summary>
		/// Whether one of the grabbers of the hand holds something: a hand holding an object stops poking with its fingertips,
		/// whatever it took the object with (the hand itself or its near/far ray).
		/// </summary>
		private bool Busy() {
			if (!hand)
				return false;

			if (hand.Physical)
				return true;

			if (_grabbers == null || _grabbers.Length == 0) {
				if (Time.unscaledTime < _grabbersAt)
					return false;

				_grabbersAt = Time.unscaledTime + 1f;
				_grabbers   = hand.GetComponents<IGrabber>();
			}

			for (var i = 0; i < _grabbers.Length; i++) {
				var grabber = _grabbers[i];
				if (grabber != null && grabber.State == GrabState.Held)
					return true;
			}

			return false;
		}

		/// <summary>
		/// Whether the physical hand already holds exactly the fingers of <paramref name="source"/>: an avatar can
		/// hand a different set (a 3 or 4 finger hand), and the physical follows without waiting for a new source.
		/// </summary>
		private bool Synced(IHand source) {
			var fingers = source?.Fingers;
			var fitted = fingers != null && fingers.Length > 0;

			if (fitted != _fitted)
				return false;

			if (!fitted)
				return true;

			var matched = 0;

			for (var i = 0; i < fingers.Length; i++) {
				var finger = fingers[i];

				if (finger == null)
					continue;

				if (!physical.Contains(finger.Type))
					return false;

				matched++;
			}

			return matched == physical.PhysicalFingers.Count;
		}

		private static IFinger Find(IFinger[] fingers, FingerType type) {
			for (var i = 0; i < fingers.Length; i++)
				if (fingers[i] != null && fingers[i].Type == type)
					return fingers[i];

			return null;
		}
	}
}
