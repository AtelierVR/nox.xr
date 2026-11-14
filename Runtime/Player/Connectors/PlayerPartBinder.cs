using System.Collections.Generic;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using Nox.XR.Loaders;
using Nox.XR.Runtime.Loaders;
using Nox.XR.Runtime.Settings;
using Nox.XR.Trackers;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Player {
	/// <summary>
	/// Creates the hand/foot parts of a <see cref="Player"/> from the trackers exposed by the active XR loader.
	/// <para>
	/// <see cref="Player"/> stays free of any XR dependency: it only owns its <see cref="Part"/> registry, this
	/// binder is the one talking to the tracker providers. A part exists <b>while its device is present and
	/// tracking</b>: it is created when a tracker claims its rig and removed as soon as the tracker leaves or
	/// stops tracking. A part spawned from a template is destroyed; the part the prefab owns (the head, which
	/// carries the view camera) is released and registered again from <see cref="Player.serializedParts"/>.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Player Part Binder")]
	[DefaultExecutionOrder(10)]
	public class PlayerPartBinder : MonoBehaviour {
		[SerializeField, Tooltip("Player receiving the hand/foot parts. Found in the parents when empty.")]
		private Player player;

		[Header("Templates")]
		[SerializeField, Tooltip("Hand prefab instantiated for each tracked hand.")]
		private Hand handTemplate;

		[SerializeField, Tooltip("Foot prefab instantiated for each tracked foot.")]
		private Foot footTemplate;

		[SerializeField, Tooltip("Generic part prefab instantiated for every other tracked rig.")]
		private TrackedPart trackedPartTemplate;

		public Player Player {
			get => player;
			set => player = value;
		}

		private void OnEnable() {
			XRLoaderManager.ProviderChanged.AddListener(OnLoaderChanged);
			FullBodyTrackingSetting.Changed.AddListener(OnFullBodyTrackingChanged);
			AttachProvider();
		}

		private void OnDisable() {
			XRLoaderManager.ProviderChanged.RemoveListener(OnLoaderChanged);
			FullBodyTrackingSetting.Changed.RemoveListener(OnFullBodyTrackingChanged);
			DetachProvider();
		}

		private void OnFullBodyTrackingChanged(bool _)
			=> RefreshParts();

		private void OnLoaderChanged(IXRLoaderProvider loader)
            => AttachProvider();

		/// <summary>
		/// Rebuilds the body parts from the trackers.
		/// <para>
		/// A full-body calibration changes the rig slot each tracker drives: the feet/hip parts spawned from
		/// templates no longer match. They are unregistered and destroyed, then created again from the new
		/// bindings. The parts that do not depend on the calibration are kept: the head (owned by the prefab)
		/// and the hands (fixed by the controller characteristics). Recreating the hands would destroy their
		/// poke/ray interactors and break the UI until they are rebuilt, which is exactly what must not happen
		/// when a calibration is confirmed from the menu.
		/// </para>
		/// </summary>
		public void RefreshParts() {
			if (!player)
				return;

			var removed = new List<Part>();
			foreach (var pair in player.Parts) {
				var part = pair.Value;
				if (!part || part.PersistsWithoutSource)
					continue;

				var rig = part.Rig;
				if (rig == EntityRig.Head || rig == EntityRig.LeftHand || rig == EntityRig.RightHand)
					continue;

				removed.Add(part);
			}

			for (var i = 0; i < removed.Count; i++) {
				player.UnregisterPart(removed[i]);
				removed[i].gameObject.Destroy();
			}

			DetachProvider();
			AttachProvider();
		}

		private static bool IsFullBodySlot(EntityRig rig)
			=> rig != EntityRig.Head && rig != EntityRig.LeftHand && rig != EntityRig.RightHand;

		private void AttachProvider() {
			var provider = XRLoaderManager.Provider?.Features?.Trackers;
			if (provider == null)
				return;

			provider.OnRegistered.AddListener(OnTrackerRegistered);
			provider.OnUnregistered.AddListener(OnTrackerUnregistered);
			provider.OnTracking.AddListener(OnTrackerTracking);

			var trackers = provider.Trackers;
			for (var i = 0; i < trackers.Count; i++)
				OnTrackerRegistered(trackers[i]);
		}

		private void DetachProvider() {
			var provider = XRLoaderManager.Provider?.Features?.Trackers;
			if (provider != null) {
				provider.OnRegistered.RemoveListener(OnTrackerRegistered);
				provider.OnUnregistered.RemoveListener(OnTrackerUnregistered);
				provider.OnTracking.RemoveListener(OnTrackerTracking);
			}
		}

		private void OnTrackerRegistered(ITracker tracker) {
			if (!player || tracker == null || !tracker.IsConnected || !tracker.IsTracking)
				return;

			var rig = tracker.Rig.ToEntityRig();
			if (rig == EntityRig.None) {
				WarnUnassigned(tracker);
				return;
			}

			if (!FullBodyTrackingSetting.Value && IsFullBodySlot(rig))
				return;

			if (player.Parts.TryGetValue(rig, out var existing)) {
				if (existing)
					player.SetPartEnabled(existing, true);

				return;
			}

			var part = CreatePart(rig);
			if (part) {
				player.RegisterPart(part);
				return;
			}

			RegisterPrefabPart(rig);
		}

		/// <summary>
		/// A tracker left or stopped tracking: the part it drove goes away with it. A part spawned from a template
		/// is destroyed (its node must not stay in the tracking space), the part the prefab owns is only released.
		/// </summary>
		private void OnTrackerUnregistered(ITracker tracker)
			=> RemovePart(tracker);

		private void OnTrackerTracking(ITracker tracker, bool tracking) {
			if (tracking)
				OnTrackerRegistered(tracker);
			else
				RemovePart(tracker);
		}

		/// <summary>Unregisters the part of a tracker's rig, and destroys it unless it outlives its device.</summary>
		private void RemovePart(ITracker tracker) {
			if (!player || tracker == null)
				return;

			var rig = tracker.Rig.ToEntityRig();
			if (rig == EntityRig.None || HasOtherTracking(rig, tracker))
				return;

			if (!player.Parts.TryGetValue(rig, out var part) || !part)
				return;

			player.UnregisterPart(part);

			if (part.PersistsWithoutSource)
				return;

			part.gameObject.Destroy();
		}

		/// <summary>Registers the part the prefab wires for this rig (the head), when it is not registered.</summary>
		private void RegisterPrefabPart(EntityRig rig) {
			var parts = player.serializedParts;
			if (parts == null)
				return;

			for (var i = 0; i < parts.Length; i++) {
				var part = parts[i];
				if (!part || part.Rig != rig)
					continue;

				player.RegisterPart(part);
				player.SetPartEnabled(part, true);
				return;
			}
		}

		/// <summary>
		/// A device can be tracked and still serve no rig: the full-body calibration has not assigned it
		/// yet, so there is no part to spawn. Say it once, otherwise the tracker just looks dead.
		/// </summary>
		private void WarnUnassigned(ITracker tracker) {
			if (!tracker.IsTracking)
				return;

			Logger.LogWarning(
				$"Tracker '{tracker.Name}' ({tracker.Id}) is tracking but has no rig slot: " +
				"run the full-body calibration to assign it to a bone.",
				this, tag: nameof(PlayerPartBinder));
		}

		/// <summary>Instantiates the part matching a tracker rig; null for rigs owned elsewhere.</summary>
		private Part CreatePart(EntityRig rig) {
			var tracking = player ? player.TrackingContainer : null;
			if (!tracking) {
				Logger.LogError($"No tracking container on {name}: {rig} cannot be spawned.", this, tag: nameof(PlayerPartBinder));
				return null;
			}

			switch (rig) {
				case EntityRig.LeftHand:
				case EntityRig.RightHand:
					if (handTemplate)
						return Hand.Create(handTemplate, rig, tracking);

					Logger.LogError($"No hand template assigned: {rig} cannot be spawned.", this, tag: nameof(PlayerPartBinder));
					return null;
				case EntityRig.LeftFoot:
				case EntityRig.RightFoot:
					if (footTemplate)
						return Foot.Create(footTemplate, rig, tracking);

					Logger.LogError($"No foot template assigned: {rig} cannot be spawned.", this, tag: nameof(PlayerPartBinder));
					return null;
				case EntityRig.None:
				case EntityRig.Base:
				case EntityRig.Head:
					return null;
				default:
					if (trackedPartTemplate)
						return TrackedPart.Create(trackedPartTemplate, rig, tracking);

					Logger.LogError($"No tracked part template assigned: {rig} cannot be spawned.", this, tag: nameof(PlayerPartBinder));
					return null;
			}
		}

		/// <summary>Whether another tracker still drives this rig and is tracking (a swap keeps the part alive).</summary>
		private bool HasOtherTracking(EntityRig rig, ITracker except) {
			var provider = XRLoaderManager.Provider?.Features?.Trackers;
			if (provider == null)
				return false;

			var trackers = provider.Trackers;
			for (var i = 0; i < trackers.Count; i++) {
				var tracker = trackers[i];
				if (tracker == null || tracker == except || !tracker.IsTracking)
					continue;

				if (tracker.Rig.ToEntityRig() == rig)
					return true;
			}

			return false;
		}
	}
}
