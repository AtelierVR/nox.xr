using System.Collections.Generic;
using Nox.CCK.Utils;
using Nox.Interactions;
using Nox.XR.Interactions;
using Nox.XR.Player;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.UI;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Controller.V2 {
	/// <summary>
	/// Interaction side of the rig: the systems XRI interactors (pokes, rays, hands) need to reach the UI and to
	/// know each other.
	/// <para>
	/// The v1 controller carried them in its <c>xr_proxy</c> prefab. Here they come from the prefab too - the
	/// <c>EventSystem</c> and <c>XRInteractionManager</c> nodes under the rig - and this part only makes sure they
	/// exist and that the XR input module is the <b>current</b> one: without that, XRI keeps the previous
	/// (desktop) module and no poke ever reaches a menu.
	/// </para>
	/// </summary>
	public partial class XRControllerV2 {
		[Header("UI")]
		[SerializeField, Tooltip("EventSystem driving the XR UI. Found in the children when empty, created when missing.")]
		private EventSystem eventSystem;

		[SerializeField, Tooltip("XRI interaction manager the interactors register on. Found in the children when empty, created when missing.")]
		private XRInteractionManager interactionManager;

		/// <summary>EventSystem of the rig, driving the XR UI (pokes, rays).</summary>
		public EventSystem UiEventSystem {
			get => eventSystem;
			set => eventSystem = value;
		}

		/// <summary>Interaction manager every XRI interactor of the rig registers on.</summary>
		public XRInteractionManager InteractionManager {
			get => interactionManager;
			set => interactionManager = value;
		}

		/// <summary>
		/// Scales the EventSystem drag threshold for tracked devices (the prefab ships 2). A poke that drifts
		/// past it while pressed is a drag, which cancels the click: the button plays its hover sound and never
		/// fires. Raised so hand jitter and a small finger slide stay within a click.
		/// </summary>
		private const float TRACKED_DRAG_THRESHOLD_MULTIPLIER = 4f;

		[Header("Grab")]
		[SerializeField, Tooltip("Give every hand part a near grabber, a distance grabber and the input that drives them.")]
		private bool enableGrab = true;
		[SerializeField, Tooltip("Layers a hand may grab. Empty uses the 'Grabbable' layer.")]
		private LayerMask grabLayers;
		[SerializeField, Tooltip("Highlight material of a targeted object. Empty keeps the one of the grabbable.")]
		private Material grabHighlight;

		[SerializeField, Min(0.01f), Tooltip("Reach of the near grab, in metres.")]
		private float grabReach = InteractionConstants.DefaultReachDistance;

		[SerializeField, Min(0.1f), Tooltip("Reach of the distance grab, in metres.")]
		private float distanceReach = 5f;

		[SerializeField, Min(0f), Tooltip("Throw power applied when a hand releases an object.")]
		private float grabThrowPower = 1.25f;

		[SerializeField, Tooltip("Bend the fingers of the avatar hand around the grabbed object.")]
		private bool conformFingers = true;

		private readonly List<Hand> _grabbedHands = new();

		/// <summary>
		/// Wires the UI systems the interactors of this rig need: the <see cref="EventSystem"/> carrying the
		/// <c>XRUIInputModule</c> (what a poke registers on, and what a ray UI uses) has to be the current one.
		/// Both it and the <see cref="XRInteractionManager"/> come from the prefab.
		/// <para>
		/// Only one <see cref="EventSystem"/> may be enabled at a time, or Unity logs "there can be only one
		/// active Event System" and the input module of the rig may never become the current one. The rig's own
		/// ships <b>inactive</b> in the prefab: the one already running (a scene or desktop one) is switched off
		/// first, then this one is enabled - so the two are never enabled together, whatever order the objects of
		/// the instantiated prefab are awakened in.
		/// </para>
		/// </summary>
		private void SetupUi() {
			var current = EventSystem.current;
			if (current && !current.transform.IsChildOf(transform)) {
				Logger.LogDebug($"Switching off '{current.name}': the rig carries the active EventSystem.", this, tag: nameof(XRControllerV2));
				current.gameObject.SetActive(false);
			}

			if (eventSystem.gameObject.activeSelf == false)
				eventSystem.gameObject.SetActive(true);

			if (EventSystem.current != eventSystem)
				EventSystem.current = eventSystem;

			var module = eventSystem.GetComponent<XRUIInputModule>();
			if (module)
				module.trackedDeviceDragThresholdMultiplier = TRACKED_DRAG_THRESHOLD_MULTIPLIER;
		}

		/// <summary>See <see cref="Nox.Controllers.IController.GetEventSystem"/>.</summary>
		public EventSystem GetEventSystem()
			=> eventSystem;

		/* ------------------------------------------------------------------ */
		/* Grab                                                               */
		/* ------------------------------------------------------------------ */

		/// <summary>
		/// Installs the grab pipeline on the hands of the player. Called once from <c>Awake</c> and kept up to
		/// date by the part events: a hand that registers later (a controller that connects after the avatar)
		/// still receives its grabbers.
		/// </summary>
		private void SetupInteraction() {
			if (!player)
				return;

			var feedback = gameObject.GetOrAddComponent<HandUiFeedback>();
			feedback.Player = player;

			if (!enableGrab)
				return;

			player.OnPartRegistered.RemoveListener(OnInteractionPartRegistered);
			player.OnPartRegistered.AddListener(OnInteractionPartRegistered);
			player.OnPartUnregistered.RemoveListener(OnInteractionPartUnregistered);
			player.OnPartUnregistered.AddListener(OnInteractionPartUnregistered);

			var parts = player.Parts;
			if (parts == null)
				return;

			foreach (var part in parts.Values)
				OnInteractionPartRegistered(part);
		}

		/// <summary>Releases every grabber and stops listening (the controller is being disposed).</summary>
		private void DisposeInteraction() {
			if (player) {
				player.OnPartRegistered.RemoveListener(OnInteractionPartRegistered);
				player.OnPartUnregistered.RemoveListener(OnInteractionPartUnregistered);
			}

			foreach (var hand in _grabbedHands)
				if (hand)
					hand.Release(grabThrowPower);

			_grabbedHands.Clear();
		}

		/// <summary>
		/// Gives a hand its grabbers, the binding bridge that drives them, and the pose of the fingers from
		/// the XR provider skeleton (used whenever the hand is not interacting).
		/// </summary>
		private void OnInteractionPartRegistered(Part part) {
			if (part is not Hand hand)
				return;

			if (enableGrab) {
				var layers = grabLayers == 0
					? InteractionConstants.GrabbableMask
					: grabLayers;

				hand.ConfigureGrab(grabReach, layers, grabHighlight, conformFingers);

				var distance = hand.gameObject.GetOrAddComponent<HandDistanceGrabber>();
				distance.Configure(hand, distanceReach, layers, grabHighlight);

				hand.gameObject.GetOrAddComponent<HandGrabInput>()
					.Configure(hand, distance, grabThrowPower);

				_grabbedHands.Add(hand);
			}

			hand.gameObject.GetOrAddComponent<HandSkeletonPose>()
				.Configure(hand);

			hand.gameObject.GetOrAddComponent<HandNearFar>();
		}

		/// <summary>Releases the hand that is going away.</summary>
		private void OnInteractionPartUnregistered(Part part) {
			if (part is not Hand hand)
				return;

			hand.Release(grabThrowPower);

			_grabbedHands.Remove(hand);
		}
	}
}
